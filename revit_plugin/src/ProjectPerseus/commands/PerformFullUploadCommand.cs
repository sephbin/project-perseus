using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.revit;
using ProjectPerseus.sync;

using ProjectPerseus.logging;
namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class PerformFullUploadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument.Document;

                if (doc == null)
                {
                    Log.Info("PerformFullUploadCommand failed: No active document.");
                    return Result.Failed;
                }

                var revit = new RevitFacade(doc);
                FullSyncRunner.PerformFullSync(revit);

                TaskDialog.Show("Perseus", "Full upload complete.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Info($"Manual Full Sync failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
