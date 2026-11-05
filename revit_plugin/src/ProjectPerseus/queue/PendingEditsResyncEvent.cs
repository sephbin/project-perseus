using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

namespace ProjectPerseus.queue
{
    // Raised by SyncOrchestrator when the user chooses to review pending web edits before syncing. The sync was
    // cancelled first: the model can't be modified inside DocumentSynchronizingWithCentral (Apply failed there with
    // "An internal error has occurred"). Here, on the main thread and outside any sync event, the review form is
    // shown, the chosen edits are applied, and the sync is re-triggered with the user's own options.
    //
    // IsAutoSyncing is intentionally NOT set — the queue check still runs in doOnPriorToSync
    // so we don't jump the line if someone else started syncing while the user reviewed edits.
    // IsResyncAfterPendingEdits IS set so the web edits check is skipped on this one pass
    // (the edits were just applied; re-checking would re-prompt for the same items).
    public class PendingEditsResyncEvent : IExternalEventHandler
    {
        private static readonly object _lock = new object();
        private static string _docGuid;
        private static List<PendingEditItem> _edits;

        internal static void Queue(string docGuid, List<PendingEditItem> edits)
        {
            lock (_lock) { _docGuid = docGuid; _edits = edits; }
        }

        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            string docGuid;
            List<PendingEditItem> edits;
            lock (_lock) { docGuid = _docGuid; edits = _edits; _docGuid = null; _edits = null; }
            if (doc == null || !doc.IsWorkshared || edits == null) return;

            try
            {
                PendingEditsApplier.EnrichWithRevitValues(edits, doc);
                List<PendingEditItem> selected;
                using (var form = new PendingEditsReviewForm(edits))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    {
                        Log.Info("Pre-sync web edits: review closed without applying; sync stays cancelled.");
                        return;
                    }
                    selected = form.SelectedEdits;
                }

                var result = PendingEditsApplier.Apply(doc, docGuid, selected);
                Log.Info($"Pre-sync web edits: applied {result.Applied}, skipped {result.Skipped}.");

                if (result.Skipped > 0)
                {
                    var dlg = new TaskDialog("Perseus — Web Edits")
                    {
                        MainIcon        = TaskDialogIcon.TaskDialogIconWarning,
                        MainInstruction = $"Applied {result.Applied} of {result.Total} web edit(s)",
                        MainContent     = $"{result.Skipped} could not be applied and stay pending:\n\n" +
                                          string.Join("\n", result.Problems.GetRange(0, Math.Min(10, result.Problems.Count))) +
                                          (result.Problems.Count > 10 ? $"\n… and {result.Problems.Count - 10} more (see the Perseus log)" : ""),
                    };
                    dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Continue the sync");
                    dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Cancel the sync");
                    if (dlg.Show() != TaskDialogResult.CommandLink1) return;
                }

                Log.Info("Triggering re-sync after pending web edits applied.");
                SyncStatusToast.Show("Web edits applied. Restarting your sync now…");
                SyncOrchestrator.IsResyncAfterPendingEdits = true;
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), SyncOrchestrator.LastUserSyncOptions.Build());
                Log.Info("Post-edits re-sync completed.");
            }
            catch (Exception ex)
            {
                SyncOrchestrator.IsResyncAfterPendingEdits = false;
                SyncStatusToast.Hide();
                Log.Error($"Pre-sync web edits / re-sync failed: {ex.Message}");
                TaskDialog.Show("Perseus — Sync", "Perseus paused your sync to apply web edits but could not finish:\n" +
                                ex.Message + "\n\nPlease press Sync with Central again.");
            }
        }

        public string GetName() => "Perseus Pending Edits Re-Sync";
    }
}
