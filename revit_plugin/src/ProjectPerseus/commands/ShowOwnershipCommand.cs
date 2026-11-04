using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_Ownership. Opens the modeless ownership panel (who owns which elements and worksets);
    // the status-bar badge opens the same panel.
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowOwnershipCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.OwnershipForm.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowOwnershipCommand] {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
