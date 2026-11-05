using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

namespace ProjectPerseus.queue
{
    // Raised by SyncOrchestrator when the pre-sync ID check finds work: elements that need an ID written (other
    // users' new elements, Django's "assign_id" requests) and/or values another user already holds. The sync was
    // cancelled (modifying the model inside DocumentSynchronizingWithCentral races with Revit's central
    // communication), so here, on the main thread, the IDs are written and clashes renumbered, then the sync is
    // re-triggered with the user's own options. Writing just before the sync means the elements Perseus had to
    // check out are released by that same sync instead of staying owned until the user next syncs.
    // IsResyncAfterIdFix skips the ID check on that one pass; the queue check still runs.
    public class IdPreSyncFixEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                SyncStatusToast.Show("Writing element IDs before your sync…");
                IdAutoAssignController.RunPreSyncFill(doc);

                if (!IdClaims.EnsureNoClashes(doc, out string summary))
                {
                    SyncStatusToast.Hide();
                    TaskDialog.Show("Perseus — ID clash", summary + "\n\nThe sync was cancelled so the clash is not pushed to the other users. Press Sync again once it is resolved.");
                    return;
                }
                if (!string.IsNullOrEmpty(summary))
                {
                    SyncStatusToast.Hide();
                    TaskDialog.Show("Perseus — ID clash", summary + "\n\nSyncing now.");
                }

                Log.Info("Triggering re-sync after the pre-sync ID fill / clash fix.");
                SyncStatusToast.Show("IDs written. Restarting your sync now…");
                SyncOrchestrator.IsResyncAfterIdFix = true;
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(), SyncOrchestrator.LastUserSyncOptions.Build());
            }
            catch (Exception ex)
            {
                SyncOrchestrator.IsResyncAfterIdFix = false;
                Log.Error($"Pre-sync ID fix / re-sync failed: {ex.Message}");
                SyncStatusToast.Hide();
                TaskDialog.Show("Perseus — Sync", "Perseus paused your sync to write element IDs but could not restart it:\n" +
                                ex.Message + "\n\nPlease press Sync with Central again.");
            }
        }

        public string GetName() => "Perseus Pre-Sync ID Fix";
    }
}
