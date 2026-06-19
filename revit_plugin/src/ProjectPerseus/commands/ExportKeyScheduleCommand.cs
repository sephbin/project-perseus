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
    [Transaction(TransactionMode.Manual)]
    public class ExportKeyScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var service = new KeyScheduleService();
            var summary = new StringBuilder();
            var errors = new List<string>();
            int totalExported = 0;
            int docsProcessed = 0;

            foreach (Document doc in commandData.Application.Application.Documents)
            {
                if (doc.IsFamilyDocument) continue;

                List<KeyScheduleConfig> configs = service.LoadConfig(doc);
                if (configs.Count == 0) continue;

                docsProcessed++;
                summary.AppendLine(doc.Title + ":");

                foreach (var cfg in configs)
                {
                    try
                    {
                        KeyScheduleData data = service.ReadFromRevit(doc, cfg.RevitScheduleName);
                        if (data == null)
                        {
                            errors.Add($"[{doc.Title}] '{cfg.RevitScheduleName}': not found in model");
                            continue;
                        }

                        service.WriteToExcel(data, cfg.ExcelFilePath, cfg.ExcelSheetName);
                        summary.AppendLine($"  {cfg.RevitScheduleName}: {data.Rows.Count} rows → {cfg.ExcelFilePath}");

                        if (!string.IsNullOrEmpty(cfg.DjangoEndpoint))
                            service.PushToDjango(data, cfg.DjangoEndpoint);

                        totalExported++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[ExportKeyScheduleCommand] [{doc.Title}] '{cfg.RevitScheduleName}': {ex.Message}");
                        Log.Exception(ex);
                        errors.Add($"[{doc.Title}] '{cfg.RevitScheduleName}': {ex.Message}");
                    }
                }
            }

            if (docsProcessed == 0)
            {
                TaskDialog.Show("Perseus: Export Schedules",
                    "No open models have key schedules configured.\n\nUse the 'Manage Schedules' button to add mappings.");
                return Result.Succeeded;
            }

            var resultText = new StringBuilder();
            if (totalExported > 0)
                resultText.Append(summary);
            if (errors.Count > 0)
                resultText.AppendLine($"\nErrors:\n  " + string.Join("\n  ", errors));

            TaskDialog.Show("Perseus: Export Schedules", resultText.ToString().Trim());
            return Result.Succeeded;
        }
    }
}
