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

            // Phase 1 — read Excel and analyze each schedule (no document writes)
            var plans = new List<KeyScheduleImportPlan>();
            var readErrors = new List<string>();

            foreach (var cfg in configs)
            {
                if (!File.Exists(cfg.ExcelFilePath))
                {
                    readErrors.Add($"'{cfg.RevitScheduleName}': file not found at {cfg.ExcelFilePath}");
                    continue;
                }
                try
                {
                    KeyScheduleData excelData = service.ReadFromExcel(cfg.ExcelFilePath, cfg.ExcelSheetName);
                    if (excelData == null)
                    {
                        readErrors.Add($"'{cfg.RevitScheduleName}': sheet '{cfg.ExcelSheetName}' not found in Excel");
                        continue;
                    }

                    KeyScheduleImportPlan plan = service.AnalyzeImport(doc, excelData, cfg.RevitScheduleName);
                    if (plan.ScheduleNotFound)
                        readErrors.Add($"'{cfg.RevitScheduleName}': schedule not found in model");
                    else
                        plans.Add(plan);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ImportKeyScheduleCommand] Read/analyze '{cfg.RevitScheduleName}': {ex.Message}");
                    Log.Exception(ex);
                    readErrors.Add($"'{cfg.RevitScheduleName}': {ex.Message}");
                }
            }

            if (plans.Count == 0)
            {
                string body = readErrors.Count > 0
                    ? "Nothing to import:\n  " + string.Join("\n  ", readErrors)
                    : "No schedule data could be read.";
                TaskDialog.Show("Perseus: Import Schedules", body);
                return Result.Succeeded;
            }

            // Phase 2 — confirm dialog showing C/U/D with delete rows listed explicitly
            var confirmText = new StringBuilder($"Ready to import {plans.Count} schedule(s):\n\n");
            foreach (var plan in plans)
            {
                confirmText.AppendLine($"  {plan.ScheduleName}");
                if (plan.RowsToCreate.Count > 0)
                    confirmText.AppendLine($"    Create : {plan.RowsToCreate.Count} row(s)");
                if (plan.RowsToUpdate.Count > 0)
                    confirmText.AppendLine($"    Update : {plan.RowsToUpdate.Count} row(s)");
                if (plan.KeysToDelete.Count > 0)
                {
                    confirmText.AppendLine($"    Delete : {plan.KeysToDelete.Count} row(s)");
                    foreach (string key in plan.KeysToDelete)
                        confirmText.AppendLine($"             • {key}");
                }
                if (plan.OrphanRowCount > 0)
                    confirmText.AppendLine($"    Delete : {plan.OrphanRowCount} unnamed row(s) with no key name");
            }

            if (HasDeletes(plans))
                confirmText.AppendLine("\nDeleted rows cannot be recovered. Proceed?");

            if (readErrors.Count > 0)
                confirmText.AppendLine("\nSkipped:\n  " + string.Join("\n  ", readErrors));

            TaskDialog confirm = new TaskDialog("Perseus: Import Schedules")
            {
                MainContent = confirmText.ToString(),
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Ok
            };
            if (confirm.Show() == TaskDialogResult.Cancel)
                return Result.Cancelled;

            // Phase 3 — execute
            var summary = new StringBuilder();
            foreach (var plan in plans)
            {
                try
                {
                    var (created, updated, deleted) = service.ExecuteImport(doc, plan);
                    summary.AppendLine($"  {plan.ScheduleName}: +{created} created, ~{updated} updated, -{deleted} deleted");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ImportKeyScheduleCommand] ExecuteImport '{plan.ScheduleName}': {ex.Message}");
                    Log.Exception(ex);
                    summary.AppendLine($"  {plan.ScheduleName}: ERROR — {ex.Message}");
                }
            }

            if (readErrors.Count > 0)
                summary.AppendLine("\nSkipped:\n  " + string.Join("\n  ", readErrors));

            TaskDialog.Show("Perseus: Import Schedules", summary.ToString().Trim());
            return Result.Succeeded;
        }

        private static bool HasDeletes(List<KeyScheduleImportPlan> plans)
        {
            foreach (var p in plans)
                if (p.KeysToDelete.Count > 0 || p.OrphanRowCount > 0) return true;
            return false;
        }
    }
}
