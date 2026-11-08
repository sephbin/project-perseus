using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_Federator. Opens the modeless Federator panel (ui/FederatorWindow): compare a consultant link
    // against an earlier version of it and review / comment / assign the changes. Reviews are stored in the model.
    [Transaction(TransactionMode.Manual)]
    public class ShowFederatorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.FederatorWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowFederatorCommand] {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
