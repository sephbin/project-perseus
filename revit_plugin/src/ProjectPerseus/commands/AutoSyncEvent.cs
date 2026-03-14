using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ProjectPerseus.Commands
{
    /// <summary>
    /// This runs on the Main Revit Thread when triggered by our background poller.
    /// </summary>
    public class AutoSyncEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                Utl.WriteLog("Auto-Sync Event Triggered! Starting SynchronizeWithCentral...");

                // Setup Sync Options
                var transOpts = new TransactWithCentralOptions();
                var syncOpts = new SynchronizeWithCentralOptions();

                // Optional: You can set a comment for the history
                syncOpts.Comment = "Auto-Synced by Perseus Queue";
                syncOpts.SaveLocalAfter = true;

                // Relinquish everything so the next person isn't blocked
                var relinquishOpts = new RelinquishOptions(true);
                syncOpts.SetRelinquishOptions(relinquishOpts);

                // FIRE THE SYNC
                doc.SynchronizeWithCentral(transOpts, syncOpts);

                Utl.WriteLog("Auto-Sync command dispatched successfully.");
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Failed to execute Auto-Sync: {ex.Message}");
            }
        }

        public string GetName() => "Perseus Auto-Sync Event";
    }
}