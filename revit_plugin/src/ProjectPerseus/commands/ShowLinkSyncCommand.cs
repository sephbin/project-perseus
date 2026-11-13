using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;

namespace ProjectPerseus.commands
{
    // Ribbon: Button_LinkSync (Federator panel). Opens the modeless Link Sync window (ui/LinkSyncWindow): keep our mapped
    // families matching the consultant links, every change approved or rejected (mapping/LinkSync).
    [Transaction(TransactionMode.Manual)]
    public class ShowLinkSyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                ui.LinkSyncWindow.Open(commandData.Application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ShowLinkSyncCommand] {ex}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
