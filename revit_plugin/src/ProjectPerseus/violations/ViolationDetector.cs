using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
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

        // DocGuid of the currently active Revit document. Updated by OnViewActivated.
        // Overlay only shows violations belonging to this document.
        private static volatile string _activeDocGuid;

        internal static readonly string SessionId = Guid.NewGuid().ToString("N");

        private static string _baseUrl;

        // Set by SyncOrchestrator.Subscribe(); raised when the user cancels a violation dialog.
        internal static ExternalEvent UndoViolationExternalEvent { get; set; }

        // Set to true before a programmatic re-delete so OnDocumentChanged skips the dialog for that transaction.
        internal static bool BypassNextViolationCheck { get; set; }

        // True between DocumentSynchronizingWithCentral and DocumentSynchronizedWithCentral
        // (set by SyncOrchestrator). Suppresses violation handling for the Reload Latest phase.
        internal static volatile bool IsSyncInProgress;

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
        {
            lock (_pendingLock) _pendingViolations.RemoveAll(v => v.DocGuid == docGuid);
            RefreshOverlay();
        }

        internal static void Subscribe(UIControlledApplication application, string baseUrl)
        {
            _baseUrl = baseUrl;
            application.ControlledApplication.DocumentChanged    += OnDocumentChanged;
            application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnLinksChangedByOthers;
            application.ControlledApplication.DocumentReloadedLatest          += OnLinksChangedByOthers;
            application.ControlledApplication.DocumentClosing                 += OnLinkDocClosing;
            application.ViewActivated += OnViewActivated;
            application.Idling        += OnIdlingCheckLinks;
        }

        internal static void Unsubscribe(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentChanged    -= OnDocumentChanged;
            application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing;
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnLinksChangedByOthers;
            application.ControlledApplication.DocumentReloadedLatest          -= OnLinksChangedByOthers;
            application.ControlledApplication.DocumentClosing                 -= OnLinkDocClosing;
            application.ViewActivated -= OnViewActivated;
            application.Idling        -= OnIdlingCheckLinks;
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            _activeDocGuid = TryGetDocGuid(e.Document);
            RefreshOverlay();
        }

        // Called from SyncOrchestrator.OnDocumentClosing. Clears violations for the closed
        // document and resets the active-doc tracker if it was the document being closed.
        internal static void HandleDocumentClosing(string docGuid)
        {
            lock (_pendingLock) _pendingViolations.RemoveAll(v => v.DocGuid == docGuid);
            if (_activeDocGuid == docGuid)
                _activeDocGuid = null;
            RefreshOverlay();
        }

        // ---- Link unload detection ------------------------------------------------------------------------------
        // We keep each document's link load states plus the set of open user worksets and diff them on every
        // DocumentChanged and, as a fallback, at idle (throttled). A link going from Loaded to anything else is an
        // unload, EXCEPT when a workset holding its instance closed in that same step: closing a workset is also
        // reported as "Unload Link" with status Unloaded, so the status alone can't tell them apart. Do NOT use
        // "all instances are on closed worksets": that read true for a real unload (log 2026-11-06 00:39) and
        // dropped it. Changes from other users (sync / Reload Latest) only re-baseline, never report.
        private class LinkSnapshot
        {
            public Dictionary<long, LinkedFileStatus> Status = new Dictionary<long, LinkedFileStatus>();
            public HashSet<int> OpenWorksets = new HashSet<int>();
        }

        private static readonly Dictionary<string, LinkSnapshot> _linkStates = new Dictionary<string, LinkSnapshot>();
        private static DateTime _lastIdleLinkCheck = DateTime.MinValue;
        private const int IdleLinkCheckMs = 1000;

        private static string LinkDocKey(Document doc) => (doc.PathName ?? "") + "|" + doc.Title;

        private static HashSet<int> OpenUserWorksets(Document doc, out Dictionary<int, string> names)
        {
            var open = new HashSet<int>();
            names = new Dictionary<int, string>();
            if (!doc.IsWorkshared) return open;
            foreach (var ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                names[ws.Id.IntegerValue] = ws.Name;
                if (ws.IsOpen) open.Add(ws.Id.IntegerValue);
            }
            return open;
        }

        // Returns link types unloaded since the last snapshot (empty on the first call for a document, which only
        // records the baseline). report=false just refreshes the snapshot.
        private static List<RevitLinkType> DetectLinkUnloads(Document doc, bool report)
        {
            var result = new List<RevitLinkType>();
            if (doc == null || doc.IsFamilyDocument || doc.IsLinked) return result;

            string key = LinkDocKey(doc);
            bool hasBaseline = _linkStates.TryGetValue(key, out var prev);
            var now = new LinkSnapshot { OpenWorksets = OpenUserWorksets(doc, out var wsNames) };

            foreach (var lt in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
            {
                LinkedFileStatus st;
                try { st = lt.GetLinkedFileStatus(); }
                catch (Exception ex)
                {
                    Log.Warn($"[ViolationDetector] could not read link status for '{lt.Name}': {ex.Message}");
                    continue;
                }
                long idVal = lt.Id.GetIdValue();
                now.Status[idVal] = st;

                if (!report || !hasBaseline) continue;
                if (!prev.Status.TryGetValue(idVal, out var before) || before == st) continue;
                // A link on a closed workset reports InClosedWorkset and can still be unloaded (Manage Links); that
                // InClosedWorkset -> Unloaded is a real unload (log 2026-11-06 12:47). Only a move INTO an unloaded
                // state counts; NotFound etc. (missing file) is not an unload.
                bool wasAvailable = before == LinkedFileStatus.Loaded || before == LinkedFileStatus.InClosedWorkset;
                bool isUnloaded   = st == LinkedFileStatus.Unloaded || st == LinkedFileStatus.LocallyUnloaded;
                if (!wasAvailable || !isUnloaded)
                {
                    Log.Info($"[ViolationDetector] link '{lt.Name}' status {before} -> {st}; not an unload.");
                    continue;
                }

                // Worksets of this link's instances, and which of them closed since the last snapshot.
                var instWs = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                    .Where(i => i.GetTypeId() == lt.Id).Select(i => i.WorksetId.IntegerValue).Distinct().ToList();
                var closedNow = instWs.Where(w => prev.OpenWorksets.Contains(w) && !now.OpenWorksets.Contains(w)).ToList();
                string Describe(int w) => (wsNames.TryGetValue(w, out var n) ? n : "#" + w) +
                                          (now.OpenWorksets.Contains(w) ? " (open)" : " (closed)");
                string wsInfo = instWs.Count == 0 ? "no instances" : string.Join(", ", instWs.Select(Describe));

                string skip = null;
                if (st == LinkedFileStatus.InClosedWorkset) skip = "its status is InClosedWorkset";
                else if (closedNow.Count > 0)
                    skip = "workset " + string.Join(", ", closedNow.Select(w => wsNames.TryGetValue(w, out var n) ? n : "#" + w)) + " was just closed";

                if (skip == null)
                {
                    result.Add(lt);
                    Log.Info($"[ViolationDetector] '{lt.Name}' unloaded: status {before} -> {st}; instance worksets: {wsInfo}; counted as a link unload.");
                }
                else Log.Info($"[ViolationDetector] '{lt.Name}' status {before} -> {st} but {skip} (instance worksets: {wsInfo}); not a link unload.");
            }

            _linkStates[key] = now;
            return result;
        }

        private static void OnIdlingCheckLinks(object sender, IdlingEventArgs e)
        {
            try
            {
                if (IsSyncInProgress) return;
                if ((DateTime.UtcNow - _lastIdleLinkCheck).TotalMilliseconds < IdleLinkCheckMs) return;
                _lastIdleLinkCheck = DateTime.UtcNow;

                var uiapp = sender as UIApplication;
                if (uiapp == null) return;
                foreach (Document doc in uiapp.Application.Documents)
                {
                    if (doc.IsLinked || doc.IsFamilyDocument || !doc.IsWorkshared) continue;
                    var unloaded = DetectLinkUnloads(doc, report: true);
                    if (unloaded.Count > 0)
                        HandleLinkUnloads(doc, unloaded, "(detected at idle)", skipEnforcement: FreefallMode.IsActive);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
        }

        // Sync / Reload Latest bring in other users' link changes: refresh the baseline without reporting.
        private static void OnLinksChangedByOthers(object sender, RevitAPIPostDocEventArgs e)
        {
            try { DetectLinkUnloads(e.Document, report: false); }
            catch (Exception ex) { Log.Exception(ex); }
        }

        private static void OnLinkDocClosing(object sender, DocumentClosingEventArgs e)
        {
            try { if (e.Document != null) _linkStates.Remove(LinkDocKey(e.Document)); }
            catch (Exception ex) { Log.Exception(ex); }
        }

        // Records unloaded links as pending sync violations (grey overlay + "sync will be blocked" warning) and
        // enqueues the audit actions.
        private static void HandleLinkUnloads(Document doc, List<RevitLinkType> links, string txnJoined, bool skipEnforcement)
        {
            string docGuid = TryGetDocGuid(doc);
            string user    = doc.Application.Username;
            string now     = DateTime.UtcNow.ToString("o");

            foreach (var lt in links)
            {
                Enqueue(new ActionDto
                {
                    ActionType      = models.ActionType.LinkUnload,
                    ElementId       = lt.Id.GetIdValue(),
                    ElementName     = lt.Name,
                    TransactionName = txnJoined,
                    RevitUser       = user,
                    DocGuid         = docGuid,
                    TimestampUtc    = now,
                });
            }

            if (skipEnforcement) return;
            var vs = ViolationSettingsCache.Get(docGuid);
            if (vs == null)
            {
                Log.Warn($"[ViolationDetector] link unload detected but no violation settings are cached for {docGuid}; not enforced.");
                return;
            }
            string syncStyle = vs.ResolveSyncStyle(models.ActionType.LinkUnload);
            Log.Info($"[ViolationDetector] link unload sync style = {syncStyle} ({links.Count} link(s)).");
            if (syncStyle == "log") return;

            foreach (var lt in links)
            {
                AddPendingViolation(new PresyncViolation
                {
                    DocGuid = docGuid, ActionType = models.ActionType.LinkUnload,
                    ElementId = lt.Id.GetIdValue(), CategoryName = "Revit Links",
                    ElementName = lt.Name, SyncStyle = syncStyle,
                });
            }
            RefreshOverlay();

            if (syncStyle != "block") return;
            string itemList = string.Join("\n", links.Select(lt => $"• Revit Links: {lt.Name}"));
            new TaskDialog("Perseus — Sync Will Be Blocked")
            {
                MainIcon        = TaskDialogIcon.TaskDialogIconError,
                MainInstruction = "Your next sync will be blocked",
                MainContent     = $"The following protected element(s) will prevent syncing:\n\n{itemList}",
                FooterText      = "Link unload cannot be undone in Revit. Contact your BIM manager.",
                CommonButtons   = TaskDialogCommonButtons.Ok,
            }.Show();
        }

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            Document doc = e.GetDocument();
            if (doc == null || !doc.IsWorkshared) return;

            IList<string> txnNames    = e.GetTransactionNames();
            ICollection<ElementId> deletedIds  = e.GetDeletedElementIds();
            ICollection<ElementId> modifiedIds = e.GetModifiedElementIds();

            // Perseus's own ID write touches every element but is not a user action; walking tens of
            // thousands of modified elements here is what made large audits slow.
            // The same goes for Perseus's parameter setup ("Perseus: Add ID Parameters" replaces the shared
            // parameter elements, which Revit reports as deletions).
            if (txnNames.Count > 0 && txnNames.All(n => n.StartsWith("Perseus:", StringComparison.Ordinal)))
            {
                if (deletedIds.Count > 0)
                    Log.Warn($"[ViolationDetector] A Perseus transaction ({string.Join("|", txnNames)}) deleted {deletedIds.Count} element(s) as a side effect " +
                             $"(ids {string.Join(", ", deletedIds.Take(10).Select(i => i.GetIdValue()))}); not treated as a user violation.");
                return;
            }

            string txnJoined = string.Join("|", txnNames);
            string user      = doc.Application.Username;
            string docGuid   = TryGetDocGuid(doc);
            string now       = DateTime.UtcNow.ToString("o");

            bool isUngroup = txnNames.Any(t => t.IndexOf("Ungroup", StringComparison.OrdinalIgnoreCase) >= 0);
            // CAUTION: "Unpin" and "Ungroup" are language-pack-dependent UI strings.
            bool isUnpin   = txnNames.Any(t => t.IndexOf("Unpin",   StringComparison.OrdinalIgnoreCase) >= 0);
            // Link unloads come from diffing link load states (see DetectLinkUnloads), not transaction names.
            // During sync the diff only re-baselines (other users' changes).
            var unloadedLinks = DetectLinkUnloads(doc, report: !IsSyncInProgress);

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

            // Changes arriving via Reload Latest during Sync with Central are other users' edits,
            // not this user's actions — no dialogs, no tracking.
            if (IsSyncInProgress) return;

            // Rooms are "deleted" by Revit by becoming unplaced (Area == 0) rather than truly
            // deleted — they appear in modifiedIds, not deletedIds. Merge them in so enforcement
            // and tracking treat them identically to genuinely deleted elements.
            var effectiveDeletedIds = new List<ElementId>(deletedIds);
            foreach (var id in modifiedIds)
            {
                var el = doc.GetElement(id);
                if (el?.Category?.Id?.GetIdValue() != (long)BuiltInCategory.OST_Rooms) continue;
                var areaParam = el.get_Parameter(BuiltInParameter.ROOM_AREA);
                if (areaParam == null || areaParam.AsDouble() < 1e-6)
                    effectiveDeletedIds.Add(id);
            }

            // On-edit enforcement: check before enqueuing so a Cancel discards all actions.
            // Bypassed for our own programmatic re-delete transactions.
            bool bypass   = BypassNextViolationCheck;
            bool freefall = FreefallMode.IsActive;
            BypassNextViolationCheck = false;

            if (unloadedLinks.Count > 0)
                HandleLinkUnloads(doc, unloadedLinks, txnJoined, skipEnforcement: bypass || freefall);

            if (!bypass && !freefall && effectiveDeletedIds.Count > 0)
            {
                var vsettings = ViolationSettingsCache.Get(docGuid);
                if (vsettings != null && vsettings.ResolveEditStyle(models.ActionType.ElementDeleted) == "dialog")
                {
                    var elementInfos = BuildViolationElementInfos(vsettings, effectiveDeletedIds, docGuid);
                    if (elementInfos.Any(ei => ei.IsProtected))
                    {
                        var result = ShowViolationWarningForm(elementInfos, effectiveDeletedIds, docGuid);
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
            if (!bypass && !freefall && isUnpin && modifiedIds.Count > 0)
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
            // Skipped in Freefall mode — violations still enqueue for the audit trail but won't gate sync.
            if (!bypass && !freefall)
            {
                var vs = ViolationSettingsCache.Get(docGuid);
                if (vs != null)
                {
                    var newViolations = new List<PresyncViolation>();

                    // ElementDeleted (includes rooms that became unplaced)
                    if (effectiveDeletedIds.Count > 0)
                    {
                        string syncStyle = vs.ResolveSyncStyle(models.ActionType.ElementDeleted);
                        if (syncStyle != "log")
                        {
                            foreach (var id in effectiveDeletedIds)
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

                    // LinkUnload is handled by HandleLinkUnloads (also reached from the idle check).

                    foreach (var v in newViolations)
                        AddPendingViolation(v);

                    if (newViolations.Count > 0)
                        RefreshOverlay();

                    // Warn immediately if any action from this transaction will block the next sync.
                    var blocking = newViolations.Where(v => v.SyncStyle == "block").ToList();
                    if (blocking.Count > 0)
                    {
                        string itemList = string.Join("\n", blocking.Select(v =>
                            $"• {v.CategoryName}: {(string.IsNullOrEmpty(v.ElementName) ? $"ID {v.ElementId}" : v.ElementName)}"));
                        bool hasLinkUnload = blocking.Any(v => v.ActionType == models.ActionType.LinkUnload);
                        bool hasOthers     = blocking.Any(v => v.ActionType != models.ActionType.LinkUnload);
                        string footer =
                            hasLinkUnload && hasOthers ? "Other actions can be undone now. Link unload cannot be undone in Revit — contact your BIM manager." :
                            hasLinkUnload              ? "Link unload cannot be undone in Revit. Contact your BIM manager." :
                                                         "Undo this action now to restore your ability to sync.";
                        var dlg = new TaskDialog("Perseus — Sync Will Be Blocked")
                        {
                            MainIcon        = TaskDialogIcon.TaskDialogIconError,
                            MainInstruction = "Your next sync will be blocked",
                            MainContent     = $"The following protected element(s) will prevent syncing:\n\n{itemList}",
                            FooterText      = footer,
                            CommonButtons   = TaskDialogCommonButtons.Ok,
                        };
                        dlg.Show();
                    }
                }
            }

            // 1. Element deletions (includes rooms that became unplaced).
            foreach (var id in effectiveDeletedIds)
            {
                long idVal = id.GetIdValue();
                var cached = ElementCategoryCache.Get(docGuid, idVal);
                Enqueue(new ActionDto
                {
                    ActionType      = models.ActionType.ElementDeleted,
                    ElementId       = idVal,
                    ElementUniqueId = cached?.UniqueId,
                    ElementName     = cached?.Name,
                    ElementCategory = cached?.CategoryName,
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
                    long idVal = id.GetIdValue();
                    var cached = ElementCategoryCache.Get(docGuid, idVal);
                    Enqueue(new ActionDto
                    {
                        ActionType      = models.ActionType.Unpin,
                        ElementId       = idVal,
                        ElementUniqueId = cached?.UniqueId,
                        ElementName     = cached?.Name,
                        ElementCategory = cached?.CategoryName,
                        TransactionName = txnJoined,
                        RevitUser       = user,
                        DocGuid         = docGuid,
                        TimestampUtc    = now,
                    });
                }
            }

            // 4. Link unload — enqueued by HandleLinkUnloads.

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
            {
                Log.Info($"[ViolationDetector] {_queue.Count} action(s) pending after \"{txnJoined}\"");
                ScheduleFlush();
            }
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

        internal static void RefreshOverlay()
        {
            string activeGuid = _activeDocGuid;
            if (FreefallMode.IsActive || activeGuid == null)
            {
                ui.ViolationOverlayController.Update(0);
                return;
            }
            int count;
            lock (_pendingLock)
                count = _pendingViolations.Count(v => v.SyncStyle == "block" && v.DocGuid == activeGuid);
            ui.ViolationOverlayController.Update(count);
        }

        // Actions are posted a few seconds after the last one is queued, so a burst (deleting 900 elements, a run of
        // edits) becomes one request instead of one per transaction.
        private const int FlushDelayMs = 3000;
        private const int MaxPerRequest = 500;
        private static System.Threading.Timer _flushTimer;
        private static readonly object _flushLock = new object();

        private static void ScheduleFlush()
        {
            lock (_flushLock)
            {
                if (_flushTimer == null) _flushTimer = new System.Threading.Timer(_ => OnFlushTimer(), null, FlushDelayMs, System.Threading.Timeout.Infinite);
                else _flushTimer.Change(FlushDelayMs, System.Threading.Timeout.Infinite);
            }
        }

        // ThreadPool thread: nothing may escape (an exception in a Timer callback terminates Revit).
        private static void OnFlushTimer()
        {
            try { FlushToServer(); }
            catch (Exception ex) { try { Log.Warn($"[ViolationDetector] flush failed: {ex.Message}"); } catch { } }
        }

        private static void FlushToServer()
        {
            if (string.IsNullOrEmpty(_baseUrl)) return;
            var actions = DrainQueue();
            if (actions.Count == 0) return;
            try
            {
                for (int i = 0; i < actions.Count; i += MaxPerRequest)
                    ProjectPerseusWeb.SubmitActions(_baseUrl, actions.Skip(i).Take(MaxPerRequest).ToList());
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
