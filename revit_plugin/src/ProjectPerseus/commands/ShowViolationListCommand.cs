using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowViolationListCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                if (uidoc == null) return Result.Failed;

                var existing = ui.ViolationListForm.Instance;
                if (existing != null && !existing.IsDisposed)
                {
                    existing.BringToFront();
                    return Result.Succeeded;
                }

                var form = new ui.ViolationListForm(uidoc);
                form.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowViolationListCommand] {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
