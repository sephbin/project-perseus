using Autodesk.Revit.UI;
using ProjectPerseus.ids;

namespace ProjectPerseus.queue
{
    // Raised by IdAutoAssignController after DocumentChanged; runs the ID assignment on the main thread
    // where a transaction can be opened.
    internal class AssignIdsEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app) => IdAutoAssignController.ProcessPending(app);

        public string GetName() => "Perseus Assign IDs";
    }
}
