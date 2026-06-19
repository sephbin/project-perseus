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
        private const string KeyParamName = "Key Name";

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
            TableSectionData section = vs.GetTableData().GetSectionData(SectionType.Body);
            int rowCount = section.NumberOfRows;
            int colCount = section.NumberOfColumns;

            var data = new KeyScheduleData { ScheduleName = scheduleName };
            data.ColumnNames = fields.Select(f => f.GetName()).ToList();

            // Row 0 is the header; data starts at row 1
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

        public KeyScheduleImportPlan AnalyzeImport(Document doc, KeyScheduleData excelData, string scheduleName)
        {
            var plan = new KeyScheduleImportPlan
            {
                ScheduleName = scheduleName,
                ColumnNames = new List<string>(excelData.ColumnNames)
            };

            ViewSchedule vs = FindKeySchedule(doc, scheduleName);
            if (vs == null)
            {
                plan.ScheduleNotFound = true;
                Log.Warn($"[KeyScheduleService] Schedule '{scheduleName}' not found for analysis");
                return plan;
            }

            TableSectionData section = vs.GetTableData().GetSectionData(SectionType.Body);

            // Read existing Revit keys from the first column (always "Key Name")
            var revitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < section.NumberOfRows; r++)
            {
                string k = section.GetCellText(r, 0);
                if (!string.IsNullOrEmpty(k))
                    revitKeys.Add(k);
            }

            var excelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in excelData.Rows)
            {
                if (!row.TryGetValue(KeyParamName, out string k) || string.IsNullOrEmpty(k)) continue;
                excelKeys.Add(k);

                if (revitKeys.Contains(k))
                    plan.RowsToUpdate.Add(row);
                else
                    plan.RowsToCreate.Add(row);
            }

            foreach (string k in revitKeys)
            {
                if (!excelKeys.Contains(k))
                    plan.KeysToDelete.Add(k);
            }

            Log.Info($"[KeyScheduleService] Analyze '{scheduleName}': " +
                     $"Create={plan.RowsToCreate.Count} Update={plan.RowsToUpdate.Count} Delete={plan.KeysToDelete.Count}");
            return plan;
        }

        public (int created, int updated, int deleted) ExecuteImport(Document doc, KeyScheduleImportPlan plan)
        {
            ViewSchedule vs = FindKeySchedule(doc, plan.ScheduleName);
            if (vs == null)
            {
                Log.Warn($"[KeyScheduleService] Schedule '{plan.ScheduleName}' not found for execute");
                return (0, 0, 0);
            }

            int created = 0, updated = 0, deleted = 0;

            using (Transaction t = new Transaction(doc, "Perseus: Import Key Schedule"))
            {
                t.Start();

                TableData tableData = vs.GetTableData();
                TableSectionData section = tableData.GetSectionData(SectionType.Body);

                // UPDATE — find each element by its "Key Name" parameter, set all other params
                var currentElements = new FilteredElementCollector(doc, vs.Id)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .ToList();

                foreach (var row in plan.RowsToUpdate)
                {
                    if (!row.TryGetValue(KeyParamName, out string keyName)) continue;

                    RevitElement match = currentElements.FirstOrDefault(e =>
                        string.Equals(e.LookupParameter(KeyParamName)?.AsString(), keyName,
                                      StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Log.Info($"[KeyScheduleService] Update: no element for key '{keyName}'");
                        continue;
                    }

                    SetElementParams(match, row);
                    updated++;
                }

                // CREATE — InsertRow creates the element; capture its ID by diffing the collector,
                //           then set "Key Name" and all other parameters directly
                foreach (var row in plan.RowsToCreate)
                {
                    if (!row.TryGetValue(KeyParamName, out string keyName) || string.IsNullOrEmpty(keyName))
                        continue;
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
                            Log.Warn($"[KeyScheduleService] Create '{keyName}': new element not found after InsertRow");
                            continue;
                        }

                        RevitElement newEl = doc.GetElement(newId);
                        SetElementParams(newEl, row); // includes "Key Name"
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[KeyScheduleService] Create '{keyName}': {ex.Message}");
                        Log.Exception(ex);
                    }
                }

                // DELETE — re-find row index each iteration (section shifts after each removal)
                foreach (string key in plan.KeysToDelete)
                {
                    int idx = FindRowIndex(section, key);
                    if (idx < 0) continue;
                    try
                    {
                        section.RemoveRow(idx);
                        section = tableData.GetSectionData(SectionType.Body);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[KeyScheduleService] Delete '{key}': {ex.Message}");
                        Log.Exception(ex);
                    }
                }

                t.Commit();
            }

            Log.Info($"[KeyScheduleService] ExecuteImport '{plan.ScheduleName}': " +
                     $"Created={created} Updated={updated} Deleted={deleted}");
            return (created, updated, deleted);
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

        private static int FindRowIndex(TableSectionData section, string keyName)
        {
            for (int r = 1; r < section.NumberOfRows; r++)
            {
                if (string.Equals(section.GetCellText(r, 0), keyName, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            return -1;
        }

        // Sets all parameters on an element from a row dictionary (column header = parameter name).
        // "Key Name" is included so new elements get their PK set in the same pass.
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
