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
    // Subscribes to DocumentChanged and FailuresProcessing to detect all tracked action types.
    // Accumulates ActionDto entries in a ConcurrentQueue for later ingest (Step 3).
    // No enforcement — detection and logging only.
    internal static class ViolationDetector
    {
        private static readonly ConcurrentQueue<ActionDto> _queue = new ConcurrentQueue<ActionDto>();

        // Single UUID stamped on all actions from this plugin session.
        internal static readonly string SessionId = Guid.NewGuid().ToString("N");

        private static string _baseUrl;

        // Drains and returns all queued actions; called by the ingest path (Step 3).
        internal static List<ActionDto> DrainQueue()
        {
            var result = new List<ActionDto>();
            while (_queue.TryDequeue(out var item))
                result.Add(item);
            return result;
        }

        internal static int QueueCount => _queue.Count;

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

            IList<string> txnNames  = e.GetTransactionNames();
            ICollection<ElementId> deletedIds  = e.GetDeletedElementIds();
            ICollection<ElementId> modifiedIds = e.GetModifiedElementIds();

            // Joined for the audit log; language-pack-dependent strings preserved verbatim.
            string txnJoined = string.Join("|", txnNames);
            string user      = doc.Application.Username;
            string docGuid   = TryGetDocGuid(doc);
            string now       = DateTime.UtcNow.ToString("o");

            bool isUngroup = txnNames.Any(t => t.IndexOf("Ungroup", StringComparison.OrdinalIgnoreCase) >= 0);
            // CAUTION: "Unpin" and "Ungroup" are language-pack-dependent UI strings.
            // TransactionName is always logged in full so raw strings can be audited across locales.
            bool isUnpin   = txnNames.Any(t => t.IndexOf("Unpin",   StringComparison.OrdinalIgnoreCase) >= 0);
            // CAUTION: "Unload" for link unload is a provisional assumption — unverified.
            bool isUnload  = txnNames.Any(t => t.IndexOf("Unload",  StringComparison.OrdinalIgnoreCase) >= 0);

            // Update element cache before processing deletions — deleted elements return null after this point.
            var addedIds = e.GetAddedElementIds();
            foreach (var id in addedIds.Concat(modifiedIds))
            {
                var el = doc.GetElement(id);
                if (el?.Category != null)
                    ElementCategoryCache.Track(docGuid, id.GetIdValue(), el.Category.Name, el.UniqueId, el.Name ?? "");
            }

            // 1. Element deletions — every deleted element ID is logged.
            //    When a group is ungrouped this also fires; the Ungroup action below is logged
            //    in addition (overlap is intentional — Step 4 server re-derivation filters).
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

            // 3. Unpin — one action per modified element in an Unpin transaction.
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

            // 4. Link unload — provisional: RevitLinkType modified in an Unload transaction.
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

            // 5. Sheet / view edits — always log-only per spec; no enforcement.
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

            // Classify deletions for on-edit enforcement (dialog style only).
            var violationNotices = new List<string>();
            var vsettings = ViolationSettingsCache.Get(docGuid);
            if (vsettings != null && deletedIds.Count > 0 &&
                vsettings.ResolveEditStyle(models.ActionType.ElementDeleted) == "dialog")
            {
                foreach (var id in deletedIds)
                {
                    long idVal = id.GetIdValue();
                    var info   = ElementCategoryCache.Get(docGuid, idVal);
                    if (info == null) continue;

                    bool isProtected =
                        vsettings.ProtectedElementIds.Contains(idVal.ToString()) ||
                        vsettings.ProtectedElementIds.Contains(info.UniqueId)    ||
                        vsettings.ProtectedCategories.Contains(info.CategoryName);

                    if (isProtected)
                    {
                        string label = string.IsNullOrEmpty(info.Name) ? $"ID {idVal}" : info.Name;
                        violationNotices.Add($"• {info.CategoryName}: {label}");
                    }
                }
            }
            if (violationNotices.Count > 0)
                ShowViolationDialog(violationNotices);

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

        private static void ShowViolationDialog(List<string> notices)
        {
            var dlg = new TaskDialog("Perseus — Protected Elements Deleted")
            {
                MainInstruction = $"{notices.Count} protected element(s) deleted",
                MainContent     = string.Join("\n", notices),
                FooterText      = "Consider undoing (Ctrl+Z) to restore these elements.",
                CommonButtons   = TaskDialogCommonButtons.Ok,
            };
            dlg.Show();
        }

        private static void FlushToServer()
        {
            if (string.IsNullOrEmpty(_baseUrl)) return;
            var actions = DrainQueue();
            if (actions.Count == 0) return;
            try   { ProjectPerseusWeb.SubmitActions(_baseUrl, actions); }
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
}
