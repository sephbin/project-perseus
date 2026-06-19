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
        public List<KeyScheduleConfig> LoadConfig(Document doc)
        {
            return KeyScheduleStorage.Load(doc);
        }

        public KeyScheduleData ReadFromRevit(Document doc, string scheduleName)
        {
            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] Key schedule '{scheduleName}' not found in document");
                return null;
            }

            List<ScheduleField> fields = GetVisibleFields(vs.Definition);
            TableSectionData sectionData = vs.GetTableData().GetSectionData(SectionType.Body);
            int rowCount = sectionData.NumberOfRows;
            int colCount = sectionData.NumberOfColumns;

            var data = new KeyScheduleData { ScheduleName = scheduleName };
            data.ColumnNames = fields.Select(f => f.GetName()).ToList();

            // Row 0 is the header; data starts at row 1
            for (int r = 1; r < rowCount; r++)
            {
                var row = new Dictionary<string, string>();
                for (int c = 0; c < Math.Min(colCount, fields.Count); c++)
                    row[fields[c].GetName()] = sectionData.GetCellText(r, c);
                data.Rows.Add(row);
            }

            Log.Info($"[KeyScheduleService] Read {data.Rows.Count} rows from '{scheduleName}'");
            return data;
        }

        public int WriteToRevit(Document doc, KeyScheduleData data, string scheduleName)
        {
            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] Key schedule '{scheduleName}' not found for write");
                return 0;
            }

            List<ScheduleField> fields = GetVisibleFields(vs.Definition);
            if (fields.Count == 0) return 0;

            ScheduleField keyField = fields[0];
            List<RevitElement> keyElements = new FilteredElementCollector(doc, vs.Id).ToElements().Cast<RevitElement>().ToList();

            int updated = 0;
            using (Transaction t = new Transaction(doc, "Perseus: Import Key Schedule"))
            {
                t.Start();
                foreach (var row in data.Rows)
                {
                    if (!row.TryGetValue(keyField.GetName(), out string keyName) || string.IsNullOrEmpty(keyName))
                        continue;

                    RevitElement match = keyElements.FirstOrDefault(e => MatchesKeyName(e, keyField, keyName));
                    if (match == null)
                    {
                        Log.Info($"[KeyScheduleService] No element for key '{keyName}' — skipping");
                        continue;
                    }

                    foreach (ScheduleField field in fields.Skip(1))
                    {
                        if (!row.TryGetValue(field.GetName(), out string cellValue)) continue;
                        Parameter param = match.LookupParameter(field.GetName());
                        if (param == null || param.IsReadOnly) continue;
                        TrySetParameter(param, cellValue);
                    }
                    updated++;
                }
                t.Commit();
            }

            Log.Info($"[KeyScheduleService] Updated {updated}/{data.Rows.Count} rows in '{scheduleName}'");
            return updated;
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

        private static bool MatchesKeyName(RevitElement e, ScheduleField keyField, string keyName)
        {
            Parameter p = e.LookupParameter(keyField.GetName());
            string val = p?.AsString() ?? e.Name;
            return string.Equals(val, keyName, StringComparison.OrdinalIgnoreCase);
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
