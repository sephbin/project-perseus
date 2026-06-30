using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.queue
{
    // Raised by SyncOrchestrator after pending web edits are applied inside a cancelled
    // DocumentSynchronizingWithCentral handler. Fires on the main Revit thread and
    // immediately re-triggers SynchronizeWithCentral so the applied changes reach central
    // without requiring the user to sync again manually.
    //
    // IsAutoSyncing is intentionally NOT set here — the full queue check still runs in
    // doOnPriorToSync so we don't jump the line if someone else started syncing while
    // the user was reviewing the pending edits dialog.
    public class PendingEditsResyncEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                Log.Info("Triggering re-sync after pending web edits applied.");

                var transOpts = new TransactWithCentralOptions();
                var syncOpts = new SynchronizeWithCentralOptions
                {
                    Comment = "Perseus: sync after web edits applied",
                    SaveLocalAfter = true
                };

                doc.SynchronizeWithCentral(transOpts, syncOpts);
                Log.Info("Post-edits re-sync completed.");
            }
            catch (Exception ex)
            {
                Log.Error($"Post-edits re-sync failed: {ex.Message}");
            }
        }

        public string GetName() => "Perseus Pending Edits Re-Sync";
    }
}
