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
                if (commandData.Application.ActiveUIDocument == null) return Result.Failed;
                ui.ViolationListForm.Open(commandData.Application);
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
