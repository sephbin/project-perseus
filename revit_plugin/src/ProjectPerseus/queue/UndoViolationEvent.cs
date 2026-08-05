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
                var captured   = idsToReDelete.ToList();
                int waitCycles = 0;

                // Idling can fire before PostCommand(Undo) is fully processed, so we cannot
                // unregister on the first fire unconditionally. Instead, check whether the
                // undo has settled by testing whether any approved element has been restored
                // to the document. If not yet, stay registered and retry next idle cycle.
                EventHandler<IdlingEventArgs> handler = null;
                handler = (s, e) =>
                {
                    var doc = app.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        app.Idling -= handler;
                        return;
                    }

                    bool undoSettled = captured.Any(id => doc.GetElement(RevitExtensions.CreateId(id)) != null);
                    if (!undoSettled)
                    {
                        if (++waitCycles >= 10)
                        {
                            app.Idling -= handler;
                            Log.Warn("[UndoViolationEvent] Undo did not restore approved elements within 10 idle cycles; re-delete aborted.");
                        }
                        return;
                    }

                    app.Idling -= handler;
                    ReDeleteAfterUndo(app, captured);
                };
                app.Idling += handler;
            }

            app.PostCommand(cmdId);
        }

        private static void ReDeleteAfterUndo(UIApplication app, List<long> idsToDelete)
        {
            var uidoc = app.ActiveUIDocument;
            var doc   = uidoc?.Document;
            if (uidoc == null || doc == null) return;
            try
            {
                var validIds = idsToDelete
                    .Select(id => RevitExtensions.CreateId(id))
                    .Where(eid => doc.GetElement(eid) != null)
                    .ToList();

                if (validIds.Count == 0)
                {
                    Log.Warn("[UndoViolationEvent] No approved elements found in document after undo; re-delete skipped.");
                    return;
                }

                // Select the approved elements and post Revit's native Delete command so that
                // worksharing checkout, dependent-element warnings, and all other edge cases
                // are handled exactly as they would be if the user performed the action manually.
                uidoc.Selection.SetElementIds(validIds);
                ViolationDetector.BypassNextViolationCheck = true;

                var deleteCmd = RevitCommandId.LookupPostableCommandId(PostableCommand.Delete);
                if (app.CanPostCommand(deleteCmd))
                {
                    app.PostCommand(deleteCmd);
                    Log.Info($"[UndoViolationEvent] Queued Delete for {validIds.Count} approved element(s) via selection after undo.");
                }
                else
                {
                    ViolationDetector.BypassNextViolationCheck = false;
                    Log.Warn("[UndoViolationEvent] Cannot post Delete command; re-delete aborted.");
                }
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
