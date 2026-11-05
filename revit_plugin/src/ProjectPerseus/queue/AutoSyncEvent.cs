using System;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.sync;
using ProjectPerseus.ui;

using ProjectPerseus.logging;
namespace ProjectPerseus.queue
{
    // Runs on the main Revit thread when QueuePoller raises the ExternalEvent that this
    // class is registered against. Shows the auto-sync countdown and then triggers Revit's
    // SynchronizeWithCentral while flipping SyncOrchestrator.IsAutoSyncing so the queue
    // check in doOnPriorToSync doesn't re-enter.
    public class AutoSyncEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            try
            {
                using (var form = new AutoSyncCountdownForm())
                {
                    var result = form.ShowDialog();

                    if (result == DialogResult.Cancel)
                    {
                        Log.Info("User canceled Auto-Sync during the countdown.");
                        return;
                    }
                }

                // Auto-sync skips the pre-sync gate, so write pending IDs and clear ID clashes here (outside any sync
                // event). This sync relinquishes everything, so whatever the ID writes check out is released with it.
                try
                {
                    ProjectPerseus.ids.IdAutoAssignController.RunPreSyncFill(doc);
                    var clashes = ProjectPerseus.ids.IdClaims.EnsureNoClashes(doc);
                    if (clashes.Blocking && !IdPreSyncFixEvent.AskAboutClashes(app, clashes)) return;
                    string idNote = ProjectPerseus.ids.IdClaims.Describe(clashes);
                    if (!string.IsNullOrEmpty(idNote)) Log.Info("[IdClaims] " + idNote.Replace("\n", " "));
                }
                catch (Exception idEx)
                {
                    Log.Warn($"[IdClaims] auto-sync clash check failed (non-fatal): {idEx.Message}");
                }

                Log.Info("Auto-Sync Event Triggered! Starting SynchronizeWithCentral...");

                var transOpts = new TransactWithCentralOptions();
                var syncOpts = new SynchronizeWithCentralOptions
                {
                    Comment = "Auto-Synced by Perseus Queue",
                    SaveLocalAfter = true
                };

                // Relinquish everything so the next person isn't blocked.
                var relinquishOpts = new RelinquishOptions(true);
                syncOpts.SetRelinquishOptions(relinquishOpts);

                SyncOrchestrator.IsAutoSyncing = true;
                try
                {
                    doc.SynchronizeWithCentral(transOpts, syncOpts);
                }
                finally
                {
                    SyncOrchestrator.IsAutoSyncing = false;
                }

                Log.Info("Auto-Sync command dispatched successfully.");
            }
            catch (Exception ex)
            {
                Log.Info($"Failed to execute Auto-Sync: {ex.Message}");
            }
        }

        public string GetName() => "Perseus Auto-Sync Event";
    }
}
