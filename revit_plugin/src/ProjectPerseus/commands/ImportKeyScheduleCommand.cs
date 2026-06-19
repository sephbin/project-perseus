using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.sync;
using RevitElement = Autodesk.Revit.DB.Element;

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

            // ── Phase 1: Load config ──────────────────────────────────────────
            Log.Info("[ImportKeySchedule] Phase 1: Loading config");
            List<KeyScheduleConfig> configs = service.LoadConfig(doc);
            if (configs.Count == 0)
            {
                TaskDialog.Show("Perseus: Import Schedules",
                    "No key schedules configured for this project.\n\nUse the 'Manage Schedules' button to add mappings.");
                return Result.Succeeded;
            }
            Log.Info($"[ImportKeySchedule] {configs.Count} schedule(s) configured");

            // ── Phase 2: Read Excel ───────────────────────────────────────────
            Log.Info("[ImportKeySchedule] Phase 2: Reading Excel");
            var workItems = new List<(KeyScheduleConfig cfg, KeyScheduleData data)>();
            var readErrors = new List<string>();

            foreach (var cfg in configs)
            {
                Log.Info($"[ImportKeySchedule]   '{cfg.RevitScheduleName}' ← {cfg.ExcelFilePath} [{cfg.ExcelSheetName}]");
                if (!File.Exists(cfg.ExcelFilePath))
                {
                    readErrors.Add($"'{cfg.RevitScheduleName}': file not found at {cfg.ExcelFilePath}");
                    Log.Warn($"[ImportKeySchedule]   File not found: {cfg.ExcelFilePath}");
                    continue;
                }
                try
                {
                    KeyScheduleData data = service.ReadFromExcel(cfg.ExcelFilePath, cfg.ExcelSheetName);
                    if (data == null)
                    {
                        readErrors.Add($"'{cfg.RevitScheduleName}': sheet '{cfg.ExcelSheetName}' not found in Excel");
                        continue;
                    }

                    var keys = data.Rows
                        .Select(r => r.TryGetValue(KeyScheduleService.KeyParamName, out string k) ? k : "?")
                        .ToList();
                    Log.Info($"[ImportKeySchedule]   {data.Rows.Count} row(s): {string.Join(", ", keys)}");
                    workItems.Add((cfg, data));
                }
                catch (Exception ex)
                {
                    Log.Error($"[ImportKeySchedule]   Read Excel '{cfg.ExcelFilePath}': {ex.Message}");
                    Log.Exception(ex);
                    readErrors.Add($"'{cfg.RevitScheduleName}': {ex.Message}");
                }
            }

            if (workItems.Count == 0)
            {
                string body = readErrors.Count > 0
                    ? "Nothing to import:\n  " + string.Join("\n  ", readErrors)
                    : "No schedule data could be read.";
                TaskDialog.Show("Perseus: Import Schedules", body);
                return Result.Succeeded;
            }

            // ── Phase 3: Import (Create / Update) ────────────────────────────
            Log.Info("[ImportKeySchedule] Phase 3: Importing from Excel");
            var importSummary = new List<(string schedule, int created, int updated)>();

            foreach (var (cfg, data) in workItems)
            {
                Log.Info($"[ImportKeySchedule]   Importing '{cfg.RevitScheduleName}'");
                var (created, updated) = service.ImportRows(doc, data, cfg.RevitScheduleName);
                importSummary.Add((cfg.RevitScheduleName, created, updated));
                Log.Info($"[ImportKeySchedule]   '{cfg.RevitScheduleName}': Created={created} Updated={updated}");

                if (!string.IsNullOrEmpty(cfg.DjangoEndpoint))
                    service.PushToDjango(data, cfg.DjangoEndpoint);
            }

            // ── Phase 4: Post-import state check ─────────────────────────────
            Log.Info("[ImportKeySchedule] Phase 4: Checking schedule state after import");

            // (scheduleName, displayLabel, elementId) for everything flagged for deletion
            var deleteQueue = new List<(string scheduleName, string displayLabel, ElementId elementId)>();

            foreach (var (cfg, data) in workItems)
            {
                var excelKeys = new HashSet<string>(
                    data.Rows
                        .Select(r => r.TryGetValue(KeyScheduleService.KeyParamName, out string k) ? k : "")
                        .Where(k => !string.IsNullOrEmpty(k)),
                    StringComparer.OrdinalIgnoreCase);

                Log.Info($"[ImportKeySchedule]   Schedule '{cfg.RevitScheduleName}' current elements:");
                List<(string key, RevitElement el)> scheduleElements =
                    service.GetScheduleElements(doc, cfg.RevitScheduleName);

                foreach (var (key, el) in scheduleElements)
                {
                    if (string.IsNullOrEmpty(key) || !excelKeys.Contains(key))
                    {
                        string label = string.IsNullOrEmpty(key)
                            ? $"[no key name] (id:{el.Id})"
                            : key;
                        Log.Info($"[ImportKeySchedule]   Extra (not in Excel): '{label}'");
                        deleteQueue.Add((cfg.RevitScheduleName, label, el.Id));
                    }
                    else
                    {
                        Log.Info($"[ImportKeySchedule]   OK: '{key}'");
                    }
                }
            }

            if (deleteQueue.Count == 0)
                Log.Info("[ImportKeySchedule] Phase 4: No extras found — schedule is clean");

            // ── Phase 5: Confirm and delete extras ───────────────────────────
            if (deleteQueue.Count > 0)
            {
                Log.Info($"[ImportKeySchedule] Phase 5: {deleteQueue.Count} extra item(s) — prompting user");

                var confirmText = new StringBuilder(
                    "The following items exist in the Revit schedule but are not in Excel:\n\n");

                foreach (var grp in deleteQueue.GroupBy(d => d.scheduleName))
                {
                    confirmText.AppendLine($"  {grp.Key}");
                    foreach (var (_, label, _) in grp)
                        confirmText.AppendLine($"    • {label}");
                }
                confirmText.AppendLine("\nDelete these items? This cannot be undone.");

                TaskDialog confirm = new TaskDialog("Perseus: Import Schedules — Extra Items Found")
                {
                    MainContent = confirmText.ToString(),
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Cancel
                };

                if (confirm.Show() == TaskDialogResult.Ok)
                {
                    Log.Info("[ImportKeySchedule] Phase 5: User confirmed — deleting extras");
                    int deleted = 0;

                    using (Transaction t = new Transaction(doc, "Perseus: Delete Extra Key Schedule Items"))
                    {
                        t.Start();
                        foreach (var (scheduleName, label, elementId) in deleteQueue)
                        {
                            try
                            {
                                doc.Delete(elementId);
                                Log.Info($"[ImportKeySchedule]   Deleted '{label}' from '{scheduleName}'");
                                deleted++;
                            }
                            catch (Exception ex)
                            {
                                Log.Error($"[ImportKeySchedule]   Delete '{label}': {ex.Message}");
                                Log.Exception(ex);
                            }
                        }
                        t.Commit();
                    }

                    Log.Info($"[ImportKeySchedule] Phase 5: Deleted {deleted}/{deleteQueue.Count} item(s)");
                }
                else
                {
                    Log.Info("[ImportKeySchedule] Phase 5: User cancelled — extras retained");
                }
            }

            // ── Summary dialog ────────────────────────────────────────────────
            var summary = new StringBuilder();
            foreach (var (sched, cr, up) in importSummary)
                summary.AppendLine($"  {sched}: +{cr} created  ~{up} updated");

            if (deleteQueue.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"  {deleteQueue.Count} extra item(s) found after import");
            }

            if (readErrors.Count > 0)
                summary.AppendLine("\nSkipped:\n  " + string.Join("\n  ", readErrors));

            TaskDialog.Show("Perseus: Import Schedules", summary.ToString().Trim());
            return Result.Succeeded;
        }
    }
}
