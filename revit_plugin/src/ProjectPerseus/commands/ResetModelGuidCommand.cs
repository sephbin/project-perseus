using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.revit;

using ProjectPerseus.logging;
namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class ResetModelGuidCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            if (doc == null)
            {
                Log.Info("ResetModelGuidCommand failed: No active document.");
                return Result.Failed;
            }

            TaskDialogResult warningResult = TaskDialog.Show(
                "Perseus - Reset Model Identity",
                "WARNING: This will generate a new database identity for this model. " +
                "Only do this if this file was copied from an existing project.\n\n" +
                "Are you sure you want to proceed?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                TaskDialogResult.No);

            if (warningResult != TaskDialogResult.Yes)
            {
                return Result.Cancelled;
            }

            try
            {
                string newGuid = ModelGuidStorage.ForceNewInternalGuid(doc);

                TaskDialog.Show("Perseus", $"Model Identity successfully reset.\n\nNew GUID: {newGuid}\n\nPlease Save or Sync to Central to lock in this change.");
                Log.Info($"Model GUID manually reset to: {newGuid}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Info($"Manual GUID Reset failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
