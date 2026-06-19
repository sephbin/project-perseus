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
        // Carries everything needed to delete one element after the confirm dialog.
        private struct DeleteCandidate
        {
            public Document Doc;
            public string ScheduleName;
            public string DisplayLabel;
            public ElementId ElementId;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var service = new KeyScheduleService();
            var importSummary = new List<(string docTitle, string schedule, int created, int updated)>();
            var deleteQueue = new List<DeleteCandidate>();
            var readErrors = new List<string>();
            int docsProcessed = 0;

            foreach (Document doc in commandData.Application.Application.Documents)
            {
                if (doc.IsFamilyDocument) continue;

                // ── Phase 1: Load config for this document ────────────────────
                Log.Info($"[ImportKeySchedule] Phase 1: '{doc.Title}' — loading config");
                List<KeyScheduleConfig> configs = service.LoadConfig(doc);
                if (configs.Count == 0)
                {
                    Log.Info($"[ImportKeySchedule]   No key schedule config — skipping");
                    continue;
                }
                docsProcessed++;
                Log.Info($"[ImportKeySchedule]   {configs.Count} schedule(s) configured");

                // ── Phase 2: Read Excel ───────────────────────────────────────
                Log.Info($"[ImportKeySchedule] Phase 2: '{doc.Title}' — reading Excel");
                var workItems = new List<(KeyScheduleConfig cfg, KeyScheduleData data)>();

                foreach (var cfg in configs)
                {
                    Log.Info($"[ImportKeySchedule]   '{cfg.RevitScheduleName}' ← {cfg.ExcelFilePath} [{cfg.ExcelSheetName}]");
                    if (!File.Exists(cfg.ExcelFilePath))
                    {
                        readErrors.Add($"[{doc.Title}] '{cfg.RevitScheduleName}': file not found at {cfg.ExcelFilePath}");
                        Log.Warn($"[ImportKeySchedule]   File not found: {cfg.ExcelFilePath}");
                        continue;
                    }
                    try
                    {
                        KeyScheduleData data = service.ReadFromExcel(cfg.ExcelFilePath, cfg.ExcelSheetName);
                        if (data == null)
                        {
                            readErrors.Add($"[{doc.Title}] '{cfg.RevitScheduleName}': sheet '{cfg.ExcelSheetName}' not found in Excel");
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
                        readErrors.Add($"[{doc.Title}] '{cfg.RevitScheduleName}': {ex.Message}");
                    }
                }

                if (workItems.Count == 0) continue;

                // ── Phase 3: Import (Create / Update) ────────────────────────
                Log.Info($"[ImportKeySchedule] Phase 3: '{doc.Title}' — importing from Excel");
                foreach (var (cfg, data) in workItems)
                {
                    Log.Info($"[ImportKeySchedule]   Importing '{cfg.RevitScheduleName}'");
                    var (created, updated) = service.ImportRows(doc, data, cfg.RevitScheduleName);
                    importSummary.Add((doc.Title, cfg.RevitScheduleName, created, updated));
                    Log.Info($"[ImportKeySchedule]   '{cfg.RevitScheduleName}': Created={created} Updated={updated}");

                    if (!string.IsNullOrEmpty(cfg.DjangoEndpoint))
                        service.PushToDjango(data, cfg.DjangoEndpoint);
                }

                // ── Phase 4: Post-import state check ─────────────────────────
                Log.Info($"[ImportKeySchedule] Phase 4: '{doc.Title}' — checking schedule state after import");
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
                            deleteQueue.Add(new DeleteCandidate
                            {
                                Doc = doc,
                                ScheduleName = cfg.RevitScheduleName,
                                DisplayLabel = label,
                                ElementId = el.Id
                            });
                        }
                        else
                        {
                            Log.Info($"[ImportKeySchedule]   OK: '{key}'");
                        }
                    }
                }
            }

            if (docsProcessed == 0)
            {
                TaskDialog.Show("Perseus: Import Schedules",
                    "No open models have key schedules configured.\n\nUse the 'Manage Schedules' button to add mappings.");
                return Result.Succeeded;
            }

            // ── Phase 5: Confirm and delete extras ───────────────────────────
            if (deleteQueue.Count > 0)
            {
                Log.Info($"[ImportKeySchedule] Phase 5: {deleteQueue.Count} extra item(s) across all models — prompting user");

                var confirmText = new StringBuilder(
                    "The following items exist in Revit but are not in Excel:\n\n");

                foreach (var docGroup in deleteQueue.GroupBy(d => d.Doc.Title))
                {
                    confirmText.AppendLine($"  {docGroup.Key}");
                    foreach (var candidate in docGroup)
                        confirmText.AppendLine($"    [{candidate.ScheduleName}]  • {candidate.DisplayLabel}");
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

                    // Delete per document (each needs its own transaction)
                    foreach (var docGroup in deleteQueue.GroupBy(d => d.Doc))
                    {
                        using (Transaction t = new Transaction(docGroup.Key, "Perseus: Delete Extra Key Schedule Items"))
                        {
                            t.Start();
                            foreach (var candidate in docGroup)
                            {
                                try
                                {
                                    docGroup.Key.Delete(candidate.ElementId);
                                    Log.Info($"[ImportKeySchedule]   Deleted '{candidate.DisplayLabel}' from '{candidate.ScheduleName}' [{docGroup.Key.Title}]");
                                    deleted++;
                                }
                                catch (Exception ex)
                                {
                                    Log.Error($"[ImportKeySchedule]   Delete '{candidate.DisplayLabel}': {ex.Message}");
                                    Log.Exception(ex);
                                }
                            }
                            t.Commit();
                        }
                    }

                    Log.Info($"[ImportKeySchedule] Phase 5: Deleted {deleted}/{deleteQueue.Count} item(s)");
                }
                else
                {
                    Log.Info("[ImportKeySchedule] Phase 5: User cancelled — extras retained");
                }
            }
            else
            {
                Log.Info("[ImportKeySchedule] Phase 4: No extras found across all models — schedules are clean");
            }

            // ── Summary dialog ────────────────────────────────────────────────
            var summary = new StringBuilder();
            foreach (var grp in importSummary.GroupBy(x => x.docTitle))
            {
                summary.AppendLine(grp.Key + ":");
                foreach (var (_, sched, cr, up) in grp)
                    summary.AppendLine($"  {sched}: +{cr} created  ~{up} updated");
            }

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
