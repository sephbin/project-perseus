using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using ProjectPerseus;

using ProjectPerseus.models;
using ProjectPerseus.revit;
using System.IO;
using static System.Net.Mime.MediaTypeNames;
using System.Reflection;
using Autodesk.Revit.Attributes;

namespace ProjectPerseus.Commands
{
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class PerformFullUploadCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            try
            {
                // 1. Create a RevitFacade from the active document
                var doc = commandData.Application.ActiveUIDocument.Document;

                if (doc == null)
                {
                    Utl.WriteLog("PerformFullUploadCommand failed: No active document.");
                    return Result.Failed;
                }

                var revit = new RevitFacade(doc);

                // 2. Call the STATIC method directly. No more Reflection needed!
                Plugin.PerformFullSync(revit);

                TaskDialog.Show("Perseus", "Full upload complete.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Manual Full Sync failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class OpenSettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var window = new ProjectPerseus.SettingsForm();
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
    [Transaction(TransactionMode.Manual)]
    public class ResetModelGuidCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            if (doc == null)
            {
                Utl.WriteLog("ResetModelGuidCommand failed: No active document.");
                return Result.Failed;
            }

            // Standard warning to prevent accidental resets
            TaskDialogResult warningResult = TaskDialog.Show("Perseus - Reset Model Identity",
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
                // We will add this method to ModelGuidStorage in Step 3
                string newGuid = revit.ModelGuidStorage.ForceNewInternalGuid(doc);

                TaskDialog.Show("Perseus", $"Model Identity successfully reset.\n\nNew GUID: {newGuid}\n\nPlease Save or Sync to Central to lock in this change.");
                Utl.WriteLog($"Model GUID manually reset to: {newGuid}");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"Manual GUID Reset failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}