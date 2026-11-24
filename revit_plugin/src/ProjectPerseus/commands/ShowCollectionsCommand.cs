using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_Collections (Collections panel). Opens the modeless Collections panel (ui/CollectionsWindow): the
    // members of reference collections (sign packs -> sign types) and instance overrides; the collection parameters are
    // locked, so this is where they are edited.
    [Transaction(TransactionMode.Manual)]
    public class ShowCollectionsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.CollectionsWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowCollectionsCommand] {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
