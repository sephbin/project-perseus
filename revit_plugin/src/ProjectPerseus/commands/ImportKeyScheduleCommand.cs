using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.sync;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class ImportKeyScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "No active document.";
                return Result.Failed;
            }

            var service = new KeyScheduleService();
            List<KeyScheduleConfig> configs = service.LoadConfig(doc);

            if (configs.Count == 0)
            {
                TaskDialog.Show("Perseus: Import Schedules",
                    "No key schedules configured for this project.\n\nUse the 'Manage Schedules' button to add mappings.");
                return Result.Succeeded;
            }

            var previews = new List<(KeyScheduleConfig cfg, KeyScheduleData data)>();
            var skipped = new List<string>();

            foreach (var cfg in configs)
            {
                if (!File.Exists(cfg.ExcelFilePath))
                {
                    skipped.Add($"'{cfg.RevitScheduleName}': file not found at {cfg.ExcelFilePath}");
                    continue;
                }
                try
                {
                    KeyScheduleData data = service.ReadFromExcel(cfg.ExcelFilePath, cfg.ExcelSheetName);
                    if (data != null)
                        previews.Add((cfg, data));
                    else
                        skipped.Add($"'{cfg.RevitScheduleName}': sheet '{cfg.ExcelSheetName}' not found in Excel");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ImportKeyScheduleCommand] Read Excel '{cfg.ExcelFilePath}': {ex.Message}");
                    Log.Exception(ex);
                    skipped.Add($"'{cfg.RevitScheduleName}': {ex.Message}");
                }
            }

            if (previews.Count == 0)
            {
                string body = skipped.Count > 0
                    ? "Nothing to import:\n  " + string.Join("\n  ", skipped)
                    : "No schedule data could be read.";
                TaskDialog.Show("Perseus: Import Schedules", body);
                return Result.Succeeded;
            }

            // Confirmation
            var confirmText = new StringBuilder("The following will be imported into Revit:\n\n");
            foreach (var (cfg, data) in previews)
                confirmText.AppendLine($"  {cfg.RevitScheduleName}: {data.Rows.Count} rows  ({Path.GetFileName(cfg.ExcelFilePath)})");

            TaskDialog confirm = new TaskDialog("Perseus: Import Schedules")
            {
                MainContent = confirmText.ToString(),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok
            };
            if (confirm.Show() == TaskDialogResult.Cancel)
                return Result.Cancelled;

            var summary = new StringBuilder();
            foreach (var (cfg, data) in previews)
            {
                try
                {
                    int updated = service.WriteToRevit(doc, data, cfg.RevitScheduleName);
                    summary.AppendLine($"  {cfg.RevitScheduleName}: {updated}/{data.Rows.Count} rows updated");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ImportKeyScheduleCommand] WriteToRevit '{cfg.RevitScheduleName}': {ex.Message}");
                    Log.Exception(ex);
                    summary.AppendLine($"  {cfg.RevitScheduleName}: ERROR — {ex.Message}");
                }
            }

            if (skipped.Count > 0)
                summary.AppendLine("\nSkipped:\n  " + string.Join("\n  ", skipped));

            TaskDialog.Show("Perseus: Import Schedules", summary.ToString().Trim());
            return Result.Succeeded;
        }
    }
}
