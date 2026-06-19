using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using OfficeOpenXml;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
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
            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                ExcelWorksheet sheet = package.Workbook.Worksheets[sheetName];
                if (sheet == null)
                {
                    Log.Warn($"[KeyScheduleService] Sheet '{sheetName}' not found in {filePath}");
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

                Log.Info($"[KeyScheduleService] Read {data.Rows.Count} rows from Excel '{sheetName}'");
                return data;
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

        // --- Private helpers ---

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
