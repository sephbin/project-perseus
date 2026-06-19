using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.sync;

namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportKeyScheduleCommand : IExternalCommand
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
                TaskDialog.Show("Perseus: Export Schedules",
                    "No key schedules configured for this project.\n\nUse the 'Manage Schedules' button to add mappings.");
                return Result.Succeeded;
            }

            var summary = new StringBuilder();
            int exported = 0;
            var errors = new List<string>();

            foreach (var cfg in configs)
            {
                try
                {
                    KeyScheduleData data = service.ReadFromRevit(doc, cfg.RevitScheduleName);
                    if (data == null)
                    {
                        errors.Add($"'{cfg.RevitScheduleName}': not found in model");
                        continue;
                    }

                    service.WriteToExcel(data, cfg.ExcelFilePath, cfg.ExcelSheetName);
                    summary.AppendLine($"  {cfg.RevitScheduleName}: {data.Rows.Count} rows → {cfg.ExcelFilePath}");

                    if (!string.IsNullOrEmpty(cfg.DjangoEndpoint))
                        service.PushToDjango(data, cfg.DjangoEndpoint);

                    exported++;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ExportKeyScheduleCommand] '{cfg.RevitScheduleName}': {ex.Message}");
                    Log.Exception(ex);
                    errors.Add($"'{cfg.RevitScheduleName}': {ex.Message}");
                }
            }

            var resultText = new StringBuilder();
            if (exported > 0)
                resultText.AppendLine($"Exported {exported} schedule(s):\n").Append(summary);
            if (errors.Count > 0)
                resultText.AppendLine($"\nErrors:\n  " + string.Join("\n  ", errors));

            TaskDialog.Show("Perseus: Export Schedules", resultText.ToString().Trim());
            return Result.Succeeded;
        }
    }
}
