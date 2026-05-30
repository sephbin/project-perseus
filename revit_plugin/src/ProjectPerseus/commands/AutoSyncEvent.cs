using System;
using System.Windows.Forms; // Required for DialogResult
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
                // 1. Show the countdown dialog
                using (var form = new ProjectPerseus.ui.AutoSyncCountdownForm())
                {
                    // ShowDialog blocks the thread until the form is closed by the user or the timer
                    var result = form.ShowDialog();

                    // 2. Check if the user bailed out
                    if (result == DialogResult.Cancel)
                    {
                        Utl.WriteLog("User canceled Auto-Sync during the countdown.");

                        // Optional: You might want to notify Django here that they left the queue, 
                        // or just let them manually sync later.
                        return;
                    }
                }

                // 3. If we get here, the timer finished or they clicked "Sync Now"
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

                // FIRE THE SYNC — flag tells doOnPriorToSync to skip the queue check re-entry
                ProjectPerseus.sync.SyncOrchestrator.IsAutoSyncing = true;
                try
                {
                    doc.SynchronizeWithCentral(transOpts, syncOpts);
                }
                finally
                {
                    ProjectPerseus.sync.SyncOrchestrator.IsAutoSyncing = false;
                }

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