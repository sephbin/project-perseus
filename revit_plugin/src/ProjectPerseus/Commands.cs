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
}