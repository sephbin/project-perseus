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
                // Create a RevitFacade from the active document
                var doc = commandData.Application.ActiveUIDocument.Document;
                var revit = new RevitFacade(doc);

                // Run your full sync logic (static or instance method)
                var plugin = new Plugin();
                var method = typeof(Plugin).GetMethod("PerformFullSync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                method.Invoke(plugin, new object[] { revit });

                TaskDialog.Show("Perseus", "Full upload complete.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
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