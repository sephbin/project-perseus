using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using OfficeOpenXml;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.util;
using ProjectPerseus.web;
using RevitElement = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.sync
{
    public class KeyScheduleService
    {
        public const string KeyParamName = "Key Name";

        public List<KeyScheduleConfig> LoadConfig(Document doc)
        {
            return KeyScheduleStorage.Load(doc);
        }

        // Read all rows from a Revit key schedule (used by Export).
        public KeyScheduleData ReadFromRevit(Document doc, string scheduleName)
        {
            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] Key schedule '{scheduleName}' not found in document");
                return null;
            }

            List<ScheduleField> fields = GetVisibleFields(vs.Definition);
            TableSectionData section = vs.GetTableData().GetSectionData(SectionType.Body);
            int rowCount = section.NumberOfRows;
            int colCount = section.NumberOfColumns;

            var data = new KeyScheduleData { ScheduleName = scheduleName };
            data.ColumnNames = fields.Select(f => f.GetName()).ToList();

            for (int r = 1; r < rowCount; r++)
            {
                var row = new Dictionary<string, string>();
                for (int c = 0; c < Math.Min(colCount, fields.Count); c++)
                    row[fields[c].GetName()] = section.GetCellText(r, c);
                data.Rows.Add(row);
            }

            Log.Info($"[KeyScheduleService] Read {data.Rows.Count} rows from '{scheduleName}'");
            return data;
        }

        // Import all rows from excelData into the named schedule.
        // Creates new elements for keys not already present; updates existing elements.
        // Returns (created, updated) counts.
        public (int created, int updated) ImportRows(Document doc, KeyScheduleData excelData, string scheduleName)
        {
            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] ImportRows: '{scheduleName}' not found");
                return (0, 0);
            }

            int created = 0, updated = 0;

            using (Transaction t = new Transaction(doc, $"Perseus: Import {scheduleName}"))
            {
                t.Start();

                EnsureScheduleFields(vs, excelData.ColumnNames);

                TableData tableData = vs.GetTableData();
                TableSectionData section = tableData.GetSectionData(SectionType.Body);

                foreach (var row in excelData.Rows)
                {
                    if (!row.TryGetValue(KeyParamName, out string keyName) || string.IsNullOrEmpty(keyName))
                    {
                        Log.Warn($"[KeyScheduleService]   Row has no '{KeyParamName}' — skipped");
                        continue;
                    }

                    RevitElement existing = new FilteredElementCollector(doc, vs.Id)
                        .WhereElementIsNotElementType()
                        .ToElements()
                        .FirstOrDefault(e => string.Equals(
                            e.LookupParameter(KeyParamName)?.AsString(), keyName,
                            StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        Log.Info($"[KeyScheduleService]   UPDATE '{keyName}' (id={existing.Id})");
                        SetElementParams(existing, row);
                        updated++;
                    }
                    else
                    {
                        Log.Info($"[KeyScheduleService]   CREATE '{keyName}'");
                        try
                        {
                            var beforeIds = new FilteredElementCollector(doc, vs.Id)
                                .WhereElementIsNotElementType()
                                .ToElementIds()
                                .ToHashSet();

                            section.InsertRow(section.NumberOfRows);
                            section = tableData.GetSectionData(SectionType.Body);

                            ElementId newId = new FilteredElementCollector(doc, vs.Id)
                                .WhereElementIsNotElementType()
                                .ToElementIds()
                                .FirstOrDefault(id => !beforeIds.Contains(id));

                            if (newId == null || newId == ElementId.InvalidElementId)
                            {
                                Log.Warn($"[KeyScheduleService]   CREATE '{keyName}': element not found after InsertRow");
                                continue;
                            }

                            RevitElement newEl = doc.GetElement(newId);
                            SetElementParams(newEl, row);
                            Log.Info($"[KeyScheduleService]   CREATE '{keyName}' OK (id={newId})");
                            created++;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[KeyScheduleService]   CREATE '{keyName}': {ex.Message}");
                            Log.Exception(ex);
                        }
                    }
                }

                t.Commit();
            }

            return (created, updated);
        }

        // Returns all elements currently in the named schedule with their key name values.
        // Logs each element found (for post-import state verification).
        public List<(string keyName, RevitElement element)> GetScheduleElements(Document doc, string scheduleName)
        {
            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] GetScheduleElements: '{scheduleName}' not found");
                return new List<(string, RevitElement)>();
            }

            var result = new List<(string, RevitElement)>();

            foreach (RevitElement e in new FilteredElementCollector(doc, vs.Id)
                .WhereElementIsNotElementType()
                .ToElements())
            {
                string key = e.LookupParameter(KeyParamName)?.AsString() ?? "";
                Log.Info($"[KeyScheduleService]   Element id={e.Id} '{KeyParamName}'='{key}'");
                result.Add((key, e));
            }

            return result;
        }

        public KeyScheduleData ReadFromExcel(string filePath, string sheetName)
        {
            try
            {
                return ReadFromExcelEpPlus(filePath, sheetName);
            }
            catch (IOException ioEx)
            {
                // File is likely locked by a running Excel instance — try COM fallback
                Log.Warn($"[KeyScheduleService] EPPlus could not open '{filePath}' ({ioEx.Message}) — trying COM fallback");
                KeyScheduleData comResult = ReadFromExcelCom(filePath, sheetName);
                if (comResult != null) return comResult;
                throw; // COM also failed; surface the original IO error
            }
        }

        private static KeyScheduleData ReadFromExcelEpPlus(string filePath, string sheetName)
        {
            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                ExcelWorksheet sheet = package.Workbook.Worksheets[sheetName];
                if (sheet == null)
                {
                    Log.Warn($"[KeyScheduleService] EPPlus: sheet '{sheetName}' not found in {filePath}");
                    return null;
                }

                int totalRows = sheet.Dimension?.Rows ?? 0;
                int totalCols = sheet.Dimension?.Columns ?? 0;
                if (totalRows < 1) return new KeyScheduleData { ScheduleName = sheetName };

                var data = new KeyScheduleData { ScheduleName = sheetName };
                for (int c = 1; c <= totalCols; c++)
                    data.ColumnNames.Add(sheet.Cells[1, c].Text ?? $"Col{c}");

                for (int r = 2; r <= totalRows; r++)
                {
                    var row = new Dictionary<string, string>();
                    for (int c = 1; c <= totalCols; c++)
                        row[data.ColumnNames[c - 1]] = sheet.Cells[r, c].Text ?? "";
                    data.Rows.Add(row);
                }

                Log.Info($"[KeyScheduleService] EPPlus: read {data.Rows.Count} rows from '{sheetName}'");
                return data;
            }
        }

        // Reads from a workbook already open in Excel via COM late-binding.
        // Requires Excel to be running and the file to be open in it.
        // Does NOT close or modify the workbook.
        private static KeyScheduleData ReadFromExcelCom(string filePath, string sheetName)
        {
            object excelObj = null;
            try
            {
                excelObj = Marshal.GetActiveObject("Excel.Application");
            }
            catch (COMException)
            {
                Log.Warn("[KeyScheduleService] COM: Excel is not running — cannot use COM fallback");
                return null;
            }

            dynamic excel = excelObj;
            dynamic targetWb = null;
            dynamic targetWs = null;
            dynamic usedRange = null;

            try
            {
                // Find the workbook by full path
                foreach (dynamic wb in excel.Workbooks)
                {
                    try
                    {
                        if (string.Equals(wb.FullName?.ToString(), filePath,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            targetWb = wb;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetWb == null)
                {
                    Log.Warn($"[KeyScheduleService] COM: '{Path.GetFileName(filePath)}' is not open in Excel");
                    return null;
                }

                // Find the worksheet
                foreach (dynamic ws in targetWb.Worksheets)
                {
                    try
                    {
                        if (string.Equals(ws.Name?.ToString(), sheetName,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            targetWs = ws;
                            break;
                        }
                    }
                    catch { }
                }

                if (targetWs == null)
                {
                    Log.Warn($"[KeyScheduleService] COM: sheet '{sheetName}' not found in open workbook");
                    return null;
                }

                usedRange = targetWs.UsedRange;
                int rows = (int)usedRange.Rows.Count;
                int cols = (int)usedRange.Columns.Count;

                var data = new KeyScheduleData { ScheduleName = sheetName };
                if (rows < 1) return data;

                for (int c = 1; c <= cols; c++)
                {
                    object val = usedRange.Cells[1, c].Value2;
                    data.ColumnNames.Add(val?.ToString() ?? $"Col{c}");
                }

                for (int r = 2; r <= rows; r++)
                {
                    var row = new Dictionary<string, string>();
                    for (int c = 1; c <= cols; c++)
                    {
                        object val = usedRange.Cells[r, c].Value2;
                        row[data.ColumnNames[c - 1]] = val?.ToString() ?? "";
                    }
                    data.Rows.Add(row);
                }

                Log.Info($"[KeyScheduleService] COM: read {data.Rows.Count} rows from '{sheetName}'");
                return data;
            }
            catch (Exception ex)
            {
                Log.Warn($"[KeyScheduleService] COM: read failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (usedRange  != null) try { Marshal.ReleaseComObject(usedRange);  } catch { }
                if (targetWs   != null) try { Marshal.ReleaseComObject(targetWs);   } catch { }
                if (targetWb   != null) try { Marshal.ReleaseComObject(targetWb);   } catch { }
                if (excelObj   != null) try { Marshal.ReleaseComObject(excelObj);   } catch { }
            }
        }

        public void WriteToExcel(KeyScheduleData data, string filePath, string sheetName)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            FileInfo fileInfo = new FileInfo(filePath);
            using (var package = fileInfo.Exists ? new ExcelPackage(fileInfo) : new ExcelPackage())
            {
                ExcelWorksheet existing = package.Workbook.Worksheets[sheetName];
                if (existing != null)
                    package.Workbook.Worksheets.Delete(existing);

                ExcelWorksheet sheet = package.Workbook.Worksheets.Add(sheetName);

                for (int c = 0; c < data.ColumnNames.Count; c++)
                {
                    sheet.Cells[1, c + 1].Value = data.ColumnNames[c];
                    sheet.Cells[1, c + 1].Style.Font.Bold = true;
                }

                for (int r = 0; r < data.Rows.Count; r++)
                {
                    var row = data.Rows[r];
                    for (int c = 0; c < data.ColumnNames.Count; c++)
                    {
                        string col = data.ColumnNames[c];
                        sheet.Cells[r + 2, c + 1].Value = row.TryGetValue(col, out string val) ? val : "";
                    }
                }

                if (sheet.Dimension != null)
                    sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

                package.SaveAs(fileInfo);
            }

            Log.Info($"[KeyScheduleService] Wrote {data.Rows.Count} rows → '{filePath}' [{sheetName}]");
        }

        public void PushToDjango(KeyScheduleData data, string endpoint)
        {
            try
            {
                string json = JsonConvert.SerializeObject(data, Formatting.None);
                string token = Config.Instance.ApiToken ?? "";
                string response = WebHelper.Post(endpoint, token, json);
                int preview = Math.Min(200, response?.Length ?? 0);
                Log.Info($"[KeyScheduleService] Django push '{endpoint}': {response?.Substring(0, preview)}");
            }
            catch (Exception ex)
            {
                Log.Error($"[KeyScheduleService] Django push failed for '{endpoint}': {ex.Message}");
                Log.Exception(ex);
            }
        }

        // Returns a new KeyScheduleData containing only rows that pass the cascading filter rules.
        // Rules apply in order; each matching rule whose Python expression returns truthy sets that
        // row's state (Include/Exclude), so later rules override earlier ones.
        // Empty/null filters returns data unchanged.
        public static KeyScheduleData ApplyFilters(KeyScheduleData data, List<KeyScheduleFilter> filters)
        {
            if (data == null || filters == null || filters.Count == 0) return data;

            var result = new KeyScheduleData { ScheduleName = data.ScheduleName };
            result.ColumnNames.AddRange(data.ColumnNames);

            int excluded = 0;
            foreach (var row in data.Rows)
            {
                bool included = true;
                foreach (var filter in filters)
                {
                    if (!string.IsNullOrWhiteSpace(filter.Expression) &&
                        FilterEvaluator.Evaluate(filter.Expression, row))
                    {
                        included = (filter.Action == FilterAction.Include);
                    }
                }
                if (included) result.Rows.Add(row);
                else excluded++;
            }

            if (excluded > 0)
                Log.Info($"[KeyScheduleService] ApplyFilters: {excluded}/{data.Rows.Count} row(s) filtered out of '{data.ScheduleName}'");
            return result;
        }

        // --- Private helpers ---

        // Adds any Excel column header that matches a schedulable field not yet visible in the schedule.
        // On duplicate names, the field with the lowest ParameterId wins.
        private static void EnsureScheduleFields(ViewSchedule vs, List<string> columnNames)
        {
            ScheduleDefinition def = vs.Definition;

            // Build name → best SchedulableField (lowest ParameterId on ties)
            var schedulable = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
            foreach (SchedulableField sf in def.GetSchedulableFields())
            {
                string name = sf.GetName(vs.Document);
                if (!schedulable.TryGetValue(name, out SchedulableField existing) ||
                    sf.ParameterId.GetIdValue() < existing.ParameterId.GetIdValue())
                {
                    schedulable[name] = sf;
                }
            }

            // Collect names already visible in the schedule
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < def.GetFieldCount(); i++)
                visible.Add(def.GetField(i).GetName());

            // Add any Excel column that is schedulable but not yet visible
            foreach (string col in columnNames)
            {
                if (visible.Contains(col)) continue;
                if (!schedulable.TryGetValue(col, out SchedulableField sf)) continue;

                try
                {
                    def.AddField(sf);
                    Log.Info($"[KeyScheduleService] Added field '{col}' to schedule '{vs.Name}'");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[KeyScheduleService] Could not add field '{col}' to '{vs.Name}': {ex.Message}");
                }
            }
        }

        private static ViewSchedule FindKeySchedule(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<ScheduleField> GetVisibleFields(ScheduleDefinition sdef)
        {
            var fields = new List<ScheduleField>();
            for (int i = 0; i < sdef.GetFieldCount(); i++)
            {
                ScheduleField f = sdef.GetField(i);
                if (!f.IsHidden) fields.Add(f);
            }
            return fields;
        }

        // Sets parameters on an element using column headers from the Excel row as parameter names.
        private static void SetElementParams(RevitElement element, Dictionary<string, string> row)
        {
            foreach (var kvp in row)
            {
                Parameter param = element.LookupParameter(kvp.Key);
                if (param == null || param.IsReadOnly) continue;
                TrySetParameter(param, kvp.Value);
            }
        }

        private static void TrySetParameter(Parameter param, string value)
        {
            try
            {
                switch (param.StorageType)
                {
                    case Autodesk.Revit.DB.StorageType.String:
                        param.Set(value);
                        break;
                    case Autodesk.Revit.DB.StorageType.Integer:
                        if (int.TryParse(value, out int i)) param.Set(i);
                        break;
                    case Autodesk.Revit.DB.StorageType.Double:
                        if (double.TryParse(value, out double d)) param.Set(d);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[KeyScheduleService] Cannot set '{param.Definition.Name}' = '{value}': {ex.Message}");
            }
        }
    }
}
