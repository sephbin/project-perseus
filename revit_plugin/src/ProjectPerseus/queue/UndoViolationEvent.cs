using Autodesk.Revit.UI;

namespace ProjectPerseus.queue
{
    // Raised by ViolationDetector when the user clicks Cancel on a protected-deletion dialog.
    // Posts the Undo command so the deletion is reversed on the next Revit idle cycle.
    internal class UndoViolationEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (app.CanPostCommand(cmdId))
                app.PostCommand(cmdId);
        }

        public string GetName() => "Perseus Undo Violation";
    }
}
