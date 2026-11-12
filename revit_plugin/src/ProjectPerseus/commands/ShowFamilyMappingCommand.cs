using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_FamilyMapping (Federator panel). Opens the modeless Family mapping window (ui/FamilyMappingWindow):
    // place our families at the consultants' family locations or link existing ones (mapping/).
    [Transaction(TransactionMode.Manual)]
    public class ShowFamilyMappingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.FamilyMappingWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowFamilyMappingCommand] {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
