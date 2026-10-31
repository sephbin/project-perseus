using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.config;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.sync;

namespace ProjectPerseus.queue
{
    // Raised by SyncOrchestrator when the pre-sync ID check finds values another user already holds. The sync
    // was cancelled (modifying the model inside DocumentSynchronizingWithCentral races with Revit's central
    // communication), so here, on the main thread, the clashing elements are renumbered and the sync is
    // re-triggered. IsResyncAfterIdFix skips the check on that one pass; the queue check still runs.
    public class IdClashFixEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                if (!IdClaims.EnsureNoClashes(doc, out string summary))
                {
                    TaskDialog.Show("Perseus — ID clash", summary + "\n\nThe sync was cancelled so the clash is not pushed to the other users.");
                    return;
                }
                if (!string.IsNullOrEmpty(summary)) TaskDialog.Show("Perseus — ID clash", summary + "\n\nSyncing now.");

                Log.Info("Triggering re-sync after ID clashes were fixed.");
                SyncOrchestrator.IsResyncAfterIdFix = true;
                doc.SynchronizeWithCentral(new TransactWithCentralOptions(),
                    new SynchronizeWithCentralOptions { Comment = "Perseus: sync after ID clash fix", SaveLocalAfter = true });
            }
            catch (Exception ex)
            {
                Log.Error($"ID clash fix / re-sync failed: {ex.Message}");
            }
        }

        public string GetName() => "Perseus ID Clash Fix";
    }
}
