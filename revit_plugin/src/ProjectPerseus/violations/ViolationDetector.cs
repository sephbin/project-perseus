using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.web;
using System.Threading.Tasks;

namespace ProjectPerseus.violations
{
    internal static class ViolationDetector
    {
        private static readonly ConcurrentQueue<ActionDto> _queue = new ConcurrentQueue<ActionDto>();
        private static readonly List<PresyncViolation> _pendingViolations = new List<PresyncViolation>();
        private static readonly object _pendingLock = new object();

        internal static readonly string SessionId = Guid.NewGuid().ToString("N");

        private static string _baseUrl;

        // Set by SyncOrchestrator.Subscribe(); raised when the user cancels a violation dialog.
        internal static ExternalEvent UndoViolationExternalEvent { get; set; }

        // Set to true before a programmatic re-delete so OnDocumentChanged skips the dialog for that transaction.
        internal static bool BypassNextViolationCheck { get; set; }

        internal static List<ActionDto> DrainQueue()
        {
            var result = new List<ActionDto>();
            while (_queue.TryDequeue(out var item))
                result.Add(item);
            return result;
        }

        internal static int QueueCount => _queue.Count;

        internal static void AddPendingViolation(PresyncViolation v)
            { lock (_pendingLock) _pendingViolations.Add(v); }

        internal static List<PresyncViolation> GetPresyncViolations(string docGuid)
            { lock (_pendingLock) return _pendingViolations.Where(v => v.DocGuid == docGuid).ToList(); }

        internal static void ClearPendingViolations(string docGuid)
            { lock (_pendingLock) _pendingViolations.RemoveAll(v => v.DocGuid == docGuid); }

        internal static void Subscribe(UIControlledApplication application, string baseUrl)
        {
            _baseUrl = baseUrl;
            application.ControlledApplication.DocumentChanged    += OnDocumentChanged;
            application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
        }

        internal static void Unsubscribe(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged    -= OnDocumentChanged;
            application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing;
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            Document doc = e.GetDocument();
            if (doc == null || !doc.IsWorkshared) return;

            IList<string> txnNames    = e.GetTransactionNames();
            ICollection<ElementId> deletedIds  = e.GetDeletedElementIds();
            ICollection<ElementId> modifiedIds = e.GetModifiedElementIds();

            string txnJoined = string.Join("|", txnNames);
            string user      = doc.Application.Username;
            string docGuid   = TryGetDocGuid(doc);
            string now       = DateTime.UtcNow.ToString("o");

            bool isUngroup = txnNames.Any(t => t.IndexOf("Ungroup", StringComparison.OrdinalIgnoreCase) >= 0);
            // CAUTION: "Unpin" and "Ungroup" are language-pack-dependent UI strings.
            bool isUnpin   = txnNames.Any(t => t.IndexOf("Unpin",   StringComparison.OrdinalIgnoreCase) >= 0);
            // CAUTION: "Unload" for link unload is provisional.
            bool isUnload  = txnNames.Any(t => t.IndexOf("Unload",  StringComparison.OrdinalIgnoreCase) >= 0);

            // Update element cache before processing deletions — deleted elements return null after this point.
            var addedIds = e.GetAddedElementIds();
            foreach (var id in addedIds.Concat(modifiedIds))
            {
                var el = doc.GetElement(id);
                if (el?.Category != null)
                    ElementCategoryCache.Track(docGuid, id.GetIdValue(),
                        el.Category.Name, el.UniqueId, el.Name ?? "",
                        ElementCategoryCache.GetFamilyTypeName(el));
            }

            // On-edit enforcement: check before enqueuing so a Cancel discards all actions.
            // Bypassed for our own programmatic re-delete transactions.
            bool bypass = BypassNextViolationCheck;
            BypassNextViolationCheck = false;

            if (!bypass && deletedIds.Count > 0)
            {
                var vsettings = ViolationSettingsCache.Get(docGuid);
                if (vsettings != null && vsettings.ResolveEditStyle(models.ActionType.ElementDeleted) == "dialog")
                {
                    var elementInfos = BuildViolationElementInfos(vsettings, deletedIds, docGuid);
                    if (elementInfos.Any(ei => ei.IsProtected))
                    {
                        var result = ShowViolationWarningForm(elementInfos, deletedIds, docGuid);
                        if (result == ViolationDialogResult.UndoAll)
                        {
                            UndoViolationExternalEvent?.Raise();
                            return;
                        }
                        if (result == ViolationDialogResult.UndoThenReDelete)
                        {
                            // Raise fires the undo; IdsToReDeleteAfterUndo drives the follow-up re-delete.
                            UndoViolationExternalEvent?.Raise();
                            return;
                        }
                        // ProceedAll: fall through and enqueue everything normally.
                    }
                }

                // On-edit enforcement for Ungroup — all-or-nothing (no partial undo).
                if (vsettings != null && isUngroup &&
                    vsettings.ResolveEditStyle(models.ActionType.Ungroup) == "dialog")
                {
                    var groupInfos = BuildUngroupElementInfos(deletedIds, docGuid);
                    if (ShowUngroupWarningForm(groupInfos))
                    {
                        UndoViolationExternalEvent?.Raise();
                        return;
                    }
                    // User confirmed — fall through and enqueue.
                }
            }

            // On-edit enforcement for Unpin — all-or-nothing (can't selectively re-pin a subset).
            if (!bypass && isUnpin && modifiedIds.Count > 0)
            {
                var vsettings = ViolationSettingsCache.Get(docGuid);
                if (vsettings != null && vsettings.ResolveEditStyle(models.ActionType.Unpin) == "dialog")
                {
                    var unpinInfos = BuildViolationElementInfos(vsettings, modifiedIds, docGuid);
                    if (unpinInfos.Any(ei => ei.IsProtected))
                    {
                        var result = ShowViolationWarningForm(unpinInfos, modifiedIds, docGuid, "unpin");
                        if (result != ViolationDialogResult.ProceedAll)
                        {
                            UndoViolationExternalEvent?.Raise();
                            return;
                        }
                    }
                }
            }

            // Track actions for the presync gate (only what actually proceeded — not undone).
            // Collect into a local list first so we can warn immediately if any will block sync.
            if (!bypass)
            {
                var vs = ViolationSettingsCache.Get(docGuid);
                if (vs != null)
                {
                    var newViolations = new List<PresyncViolation>();

                    // ElementDeleted
                    if (deletedIds.Count > 0)
                    {
                        string syncStyle = vs.ResolveSyncStyle(models.ActionType.ElementDeleted);
                        if (syncStyle != "log")
                        {
                            foreach (var id in deletedIds)
                            {
                                long idVal = id.GetIdValue();
                                var info = ElementCategoryCache.Get(docGuid, idVal);
                                if (info == null || !IsProtectedInSettings(vs, idVal, info)) continue;
                                newViolations.Add(new PresyncViolation
                                {
                                    DocGuid = docGuid, ActionType = models.ActionType.ElementDeleted,
                                    ElementId = idVal, CategoryName = info.CategoryName,
                                    ElementName = info.Name, SyncStyle = syncStyle,
                                });
                            }
                        }
                    }

                    // Ungroup — no protection filter; enforce_ungroup_sync is the policy trigger.
                    if (isUngroup && deletedIds.Count > 0)
                    {
                        string syncStyle = vs.ResolveSyncStyle(models.ActionType.Ungroup);
                        if (syncStyle != "log")
                        {
                            bool trackedAny = false;
                            foreach (var id in deletedIds)
                            {
                                long idVal = id.GetIdValue();
                                var info = ElementCategoryCache.Get(docGuid, idVal);
                                if (info == null || info.CategoryName != "Model Groups") continue;
                                trackedAny = true;
                                newViolations.Add(new PresyncViolation
                                {
                                    DocGuid = docGuid, ActionType = models.ActionType.Ungroup,
                                    ElementId = idVal, CategoryName = "Model Groups",
                                    ElementName = info.Name, SyncStyle = syncStyle,
                                });
                            }
                            if (!trackedAny)
                            {
                                newViolations.Add(new PresyncViolation
                                {
                                    DocGuid = docGuid, ActionType = models.ActionType.Ungroup,
                                    ElementId = deletedIds.First().GetIdValue(),
                                    CategoryName = "Model Groups", ElementName = "(ungrouped)",
                                    SyncStyle = syncStyle,
                                });
                            }
                        }
                    }

                    // Unpin — modified (now-unpinned) elements
                    if (isUnpin && modifiedIds.Count > 0)
                    {
                        string syncStyle = vs.ResolveSyncStyle(models.ActionType.Unpin);
                        if (syncStyle != "log")
                        {
                            foreach (var id in modifiedIds)
                            {
                                long idVal = id.GetIdValue();
                                var info = ElementCategoryCache.Get(docGuid, idVal);
                                if (info == null || !IsProtectedInSettings(vs, idVal, info)) continue;
                                newViolations.Add(new PresyncViolation
                                {
                                    DocGuid = docGuid, ActionType = models.ActionType.Unpin,
                                    ElementId = idVal, CategoryName = info.CategoryName,
                                    ElementName = info.Name, SyncStyle = syncStyle,
                                });
                            }
                        }
                    }

                    // LinkUnload — track all unloaded links; enforcement is at policy level, not per-element
                    if (isUnload && modifiedIds.Count > 0)
                    {
                        string syncStyle = vs.ResolveSyncStyle(models.ActionType.LinkUnload);
                        if (syncStyle != "log")
                        {
                            foreach (var id in modifiedIds)
                            {
                                var el = doc.GetElement(id);
                                if (!(el is RevitLinkType)) continue;
                                newViolations.Add(new PresyncViolation
                                {
                                    DocGuid = docGuid, ActionType = models.ActionType.LinkUnload,
                                    ElementId = id.GetIdValue(), CategoryName = "Revit Links",
                                    ElementName = el.Name, SyncStyle = syncStyle,
                                });
                            }
                        }
                    }

                    foreach (var v in newViolations)
                        AddPendingViolation(v);

                    // Warn immediately if any action from this transaction will block the next sync.
                    var blocking = newViolations.Where(v => v.SyncStyle == "block").ToList();
                    if (blocking.Count > 0)
                    {
                        string itemList = string.Join("\n", blocking.Select(v =>
                            $"• {v.CategoryName}: {(string.IsNullOrEmpty(v.ElementName) ? $"ID {v.ElementId}" : v.ElementName)}"));
                        var dlg = new TaskDialog("Perseus — Sync Will Be Blocked")
                        {
                            MainIcon        = TaskDialogIcon.TaskDialogIconError,
                            MainInstruction = "Your next sync will be blocked",
                            MainContent     = $"The following protected element(s) will prevent syncing until this action is undone:\n\n{itemList}",
                            FooterText      = "Undo this action now to restore your ability to sync.",
                            CommonButtons   = TaskDialogCommonButtons.Ok,
                        };
                        dlg.Show();
                    }
                }
            }

            // 1. Element deletions.
            foreach (var id in deletedIds)
            {
                Enqueue(new ActionDto
                {
                    ActionType      = models.ActionType.ElementDeleted,
                    ElementId       = id.GetIdValue(),
                    TransactionName = txnJoined,
                    RevitUser       = user,
                    DocGuid         = docGuid,
                    TimestampUtc    = now,
                });
            }

            // 2. Ungroup — one action per deleted group element.
            if (isUngroup)
            {
                foreach (var id in deletedIds)
                {
                    Enqueue(new ActionDto
                    {
                        ActionType      = models.ActionType.Ungroup,
                        ElementId       = id.GetIdValue(),
                        TransactionName = txnJoined,
                        RevitUser       = user,
                        DocGuid         = docGuid,
                        TimestampUtc    = now,
                    });
                }
            }

            // 3. Unpin.
            if (isUnpin)
            {
                foreach (var id in modifiedIds)
                {
                    Enqueue(new ActionDto
                    {
                        ActionType      = models.ActionType.Unpin,
                        ElementId       = id.GetIdValue(),
                        TransactionName = txnJoined,
                        RevitUser       = user,
                        DocGuid         = docGuid,
                        TimestampUtc    = now,
                    });
                }
            }

            // 4. Link unload — provisional.
            if (isUnload)
            {
                foreach (var id in modifiedIds)
                {
                    var el = doc.GetElement(id);
                    if (el is RevitLinkType)
                    {
                        Enqueue(new ActionDto
                        {
                            ActionType      = models.ActionType.LinkUnload,
                            ElementId       = id.GetIdValue(),
                            ElementName     = el.Name,
                            TransactionName = txnJoined,
                            RevitUser       = user,
                            DocGuid         = docGuid,
                            TimestampUtc    = now,
                        });
                    }
                }
            }

            // 5. Sheet / view edits — always log-only.
            foreach (var id in modifiedIds)
            {
                var el = doc.GetElement(id);
                long? catId = el?.Category?.Id?.GetIdValue();
                if (catId == (int)BuiltInCategory.OST_Sheets ||
                    catId == (int)BuiltInCategory.OST_Views)
                {
                    Enqueue(new ActionDto
                    {
                        ActionType      = models.ActionType.SheetViewEdit,
                        ElementId       = id.GetIdValue(),
                        ElementName     = el.Name,
                        TransactionName = txnJoined,
                        RevitUser       = user,
                        DocGuid         = docGuid,
                        TimestampUtc    = now,
                    });
                }
            }

            if (!_queue.IsEmpty)
                Task.Run(() => FlushToServer());
        }

        private static void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            var fa  = e.GetFailuresAccessor();
            var doc = fa.GetDocument();
            if (doc == null) return;

            string user    = doc.Application.Username;
            string docGuid = TryGetDocGuid(doc);
            string now     = DateTime.UtcNow.ToString("o");

            foreach (var msg in fa.GetFailureMessages(FailureSeverity.Warning))
            {
                foreach (var id in msg.GetFailingElementIds())
                {
                    Enqueue(new ActionDto
                    {
                        ActionType   = models.ActionType.WarningDismissed,
                        ElementId    = id.GetIdValue(),
                        Description  = msg.GetDescriptionText(),
                        RevitUser    = user,
                        DocGuid      = docGuid,
                        TimestampUtc = now,
                    });
                }
            }
        }

        private enum ViolationDialogResult { ProceedAll, UndoAll, UndoThenReDelete }

        private static ViolationDialogResult ShowViolationWarningForm(
            List<ViolationElementInfo> elementInfos,
            ICollection<ElementId> deletedIds,
            string docGuid,
            string actionVerb = "delete")
        {
            using (var form = new ui.ViolationWarningForm(elementInfos, actionVerb))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                    form.ApprovedElementIds.Count == 0)
                    return ViolationDialogResult.UndoAll;

                var allIds = new HashSet<long>(deletedIds.Select(id => id.GetIdValue()));
                if (form.ApprovedElementIds.IsSupersetOf(allIds))
                    return ViolationDialogResult.ProceedAll;

                // Partial approval: undo everything, then re-delete just the approved subset.
                queue.UndoViolationEvent.IdsToReDeleteAfterUndo = form.ApprovedElementIds.ToList();
                return ViolationDialogResult.UndoThenReDelete;
            }
        }

        // Returns true if the user cancelled (undo should fire), false if they confirmed.
        private static bool ShowUngroupWarningForm(List<ViolationElementInfo> groupInfos)
        {
            using (var form = new ui.ViolationWarningForm(groupInfos, "ungroup"))
            {
                return form.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
                       form.ApprovedElementIds.Count == 0;
            }
        }

        private static List<ViolationElementInfo> BuildUngroupElementInfos(
            ICollection<ElementId> deletedIds, string docGuid)
        {
            var result = new List<ViolationElementInfo>();
            foreach (var id in deletedIds)
            {
                long idVal = id.GetIdValue();
                var info = ElementCategoryCache.Get(docGuid, idVal);
                if (info == null || info.CategoryName != "Model Groups") continue;
                result.Add(new ViolationElementInfo
                {
                    ElementId      = idVal,
                    UniqueId       = info.UniqueId,
                    CategoryName   = info.CategoryName,
                    FamilyTypeName = info.FamilyTypeName,
                    ElementName    = info.Name,
                    IsProtected    = true,
                });
            }
            if (result.Count == 0)
            {
                result.Add(new ViolationElementInfo
                {
                    CategoryName = "Model Groups",
                    ElementName  = "(ungrouped)",
                    IsProtected  = true,
                });
            }
            return result;
        }

        private static List<ViolationElementInfo> BuildViolationElementInfos(
            ViolationSettings settings, ICollection<ElementId> deletedIds, string docGuid)
        {
            var result = new List<ViolationElementInfo>();
            foreach (var id in deletedIds)
            {
                long idVal = id.GetIdValue();
                var info   = ElementCategoryCache.Get(docGuid, idVal);
                if (info == null) continue;

                bool isProtected =
                    settings.ProtectedElementIds.Contains(idVal.ToString()) ||
                    settings.ProtectedElementIds.Contains(info.UniqueId)    ||
                    settings.ProtectedCategories.Contains(info.CategoryName);

                result.Add(new ViolationElementInfo
                {
                    ElementId      = idVal,
                    UniqueId       = info.UniqueId,
                    CategoryName   = info.CategoryName,
                    FamilyTypeName = info.FamilyTypeName,
                    ElementName    = info.Name,
                    IsProtected    = isProtected,
                });
            }
            return result;
        }

        private static bool IsProtectedInSettings(ViolationSettings vs, long idVal, ElementInfo info)
        {
            return vs.ProtectedElementIds.Contains(idVal.ToString()) ||
                   vs.ProtectedElementIds.Contains(info.UniqueId)    ||
                   vs.ProtectedCategories.Contains(info.CategoryName);
        }

        private static void FlushToServer()
        {
            if (string.IsNullOrEmpty(_baseUrl)) return;
            var actions = DrainQueue();
            if (actions.Count == 0) return;
            try
            {
                ProjectPerseusWeb.SubmitActions(_baseUrl, actions);
                foreach (var docGuid in actions.Select(a => a.DocGuid).Where(g => g != null).Distinct())
                    ViolationSettingsCache.LoadAsyncIfStale(docGuid, _baseUrl);
            }
            catch (Exception ex) { Log.Warn($"[ViolationDetector] real-time flush failed: {ex.Message}"); }
        }

        private static string TryGetDocGuid(Document doc)
        {
            try   { return ModelGuidStorage.GetOrCreate(doc); }
            catch { return null; }
        }

        private static void Enqueue(ActionDto dto)
        {
            dto.CorrelationId = Guid.NewGuid().ToString("N");
            dto.SessionId     = SessionId;
            Log.Info($"[ViolationDetector] {dto.ActionType} el={dto.ElementId} txn=\"{dto.TransactionName}\"");
            _queue.Enqueue(dto);
        }
    }

    internal class ViolationElementInfo
    {
        public long   ElementId      { get; set; }
        public string UniqueId       { get; set; }
        public string CategoryName   { get; set; }
        public string FamilyTypeName { get; set; }
        public string ElementName    { get; set; }
        public bool   IsProtected    { get; set; }
    }

    internal class PresyncViolation
    {
        public string DocGuid      { get; set; }
        public string ActionType   { get; set; }
        public long   ElementId    { get; set; }
        public string CategoryName { get; set; }
        public string ElementName  { get; set; }
        public string SyncStyle    { get; set; }
    }
}
