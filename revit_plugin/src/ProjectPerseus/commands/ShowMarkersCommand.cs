using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_Markers. Opens the modeless Parameter Markers panel (ui/MarkerSetsWindow): rings + icons around
    // elements by parameter value, e.g. doors with mag locks vs drop bolts.
    [Transaction(TransactionMode.ReadOnly)]
    public class ShowMarkersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.MarkerSetsWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowMarkersCommand] {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
