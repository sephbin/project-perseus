using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using ProjectPerseus.logging;
using ProjectPerseus.revit;
using ProjectPerseus.violations;

namespace ProjectPerseus.queue
{
    // Raised by ViolationDetector when the user cancels or partially approves a violation dialog.
    // Posts Undo to reverse the deletion. If IdsToReDeleteAfterUndo is set, registers a one-shot
    // Idling handler to re-delete the approved subset once the Undo has been processed.
    internal class UndoViolationEvent : IExternalEventHandler
    {
        // Set by ViolationDetector before raising this event for partial approvals.
        internal static List<long> IdsToReDeleteAfterUndo { get; set; }

        public void Execute(UIApplication app)
        {
            var idsToReDelete = IdsToReDeleteAfterUndo;
            IdsToReDeleteAfterUndo = null;

            var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (!app.CanPostCommand(cmdId)) return;

            if (idsToReDelete != null && idsToReDelete.Count > 0)
            {
                // Register a one-shot Idling handler. Idling fires AFTER PostCommand(Undo)
                // has been processed, so elements will be restored by the time we re-delete.
                var captured = idsToReDelete.ToList();
                EventHandler<IdlingEventArgs> handler = null;
                handler = (s, e) =>
                {
                    app.Idling -= handler;
                    ReDeleteAfterUndo(app, captured);
                };
                app.Idling += handler;
            }

            app.PostCommand(cmdId);
        }

        private static void ReDeleteAfterUndo(UIApplication app, List<long> idsToDelete)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;
            try
            {
                // Suppress the violation dialog for this programmatic re-delete.
                ViolationDetector.BypassNextViolationCheck = true;
                using (var txn = new Transaction(doc, "Perseus: Delete approved elements"))
                {
                    txn.Start();
                    foreach (var idVal in idsToDelete)
                    {
                        var eid = RevitExtensions.CreateId(idVal);
                        if (doc.GetElement(eid) != null)
                            doc.Delete(eid);
                    }
                    txn.Commit();
                }
                Log.Info($"[UndoViolationEvent] Re-deleted {idsToDelete.Count} approved element(s) after undo.");
            }
            catch (Exception ex)
            {
                ViolationDetector.BypassNextViolationCheck = false;
                Log.Warn($"[UndoViolationEvent] Re-delete after undo failed: {ex.Message}");
            }
        }

        public string GetName() => "Perseus Undo Violation";
    }
}
