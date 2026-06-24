using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OfficeOpenXml;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    public class ManageSchedulesForm : Form
    {
        private DataGridView _grid;
        private Button _addBtn;
        private Button _removeBtn;
        private Button _saveBtn;
        private Button _cancelBtn;

        private readonly List<string> _availableSchedules;

        // Column indices — kept as constants so they stay in sync with InitializeComponent
        private const int ColSchedule = 0;
        private const int ColSource   = 1;
        private const int ColBrowse   = 2;
        private const int ColSheet    = 3;
        private const int ColFilters  = 4;

        public List<KeyScheduleConfig> Result { get; private set; }

        public ManageSchedulesForm(List<KeyScheduleConfig> existing, List<string> availableSchedules)
        {
            _availableSchedules = availableSchedules ?? new List<string>();
            InitializeComponent();
            LoadRows(existing);
        }

        private void InitializeComponent()
        {
            Text = "Perseus: Key Schedule Mappings";
            Width = 960;
            Height = 420;
            MinimumSize = new Size(700, 320);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                RowHeadersWidth = 24,
                BackgroundColor = SystemColors.Window
            };

            // Col 0 — schedule name dropdown
            var scheduleCol = new DataGridViewComboBoxColumn
            {
                Name = "RevitScheduleName",
                HeaderText = "Revit Schedule Name",
                FillWeight = 24,
                ToolTipText = "Select a key schedule from this document",
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                AutoComplete = true
            };
            foreach (string s in _availableSchedules)
                scheduleCol.Items.Add(s);
            _grid.Columns.Add(scheduleCol);

            // Col 1 — source (file path or URL)
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Source",
                HeaderText = "Source (Path or URL)",
                FillWeight = 42,
                ToolTipText = "Full path to an .xlsx file, or a URL (Django endpoint / SharePoint). If a URL, Sheet Name is not required."
            });

            // Col 2 — browse button (narrow)
            _grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "Browse",
                HeaderText = "",
                Text = "...",
                UseColumnTextForButtonValue = true,
                Width = 32,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Browse for an Excel file"
            });

            // Col 3 — sheet name (combo; items populated per-row when a valid file path is entered)
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "SheetName",
                HeaderText = "Sheet Name",
                FillWeight = 20,
                ToolTipText = "Worksheet name inside the Excel file. Not required if Source is a URL.",
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                AutoComplete = true
            });

            // Col 4 — filter rules button (label updates to show active rule count)
            _grid.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "Filters",
                HeaderText = "Filters",
                UseColumnTextForButtonValue = false,
                Width = 90,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
                FillWeight = 1,
                ToolTipText = "Edit cascading import filter rules for this schedule"
            });

            // Disable built-in clipboard copy (copies whole row including headers and button column)
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            _grid.KeyDown += OnGridKeyDown;

            // Suppress DataGridView's built-in error dialog (e.g. combo value not in list)
            _grid.DataError += (s, e) => e.Cancel = true;

            // Allow free-text entry in the schedule and sheet combo boxes
            _grid.EditingControlShowing += OnEditingControlShowing;

            // Populate sheet dropdown when source path changes
            _grid.CellEndEdit += OnCellEndEdit;

            // Browse button click
            _grid.CellContentClick += OnCellContentClick;

            _addBtn    = new Button { Text = "Add Row",    Width = 90,  Height = 28 };
            _removeBtn = new Button { Text = "Remove Row", Width = 100, Height = 28 };
            _saveBtn   = new Button { Text = "Save",       Width = 80,  Height = 28, DialogResult = DialogResult.OK };
            _cancelBtn = new Button { Text = "Cancel",     Width = 80,  Height = 28, DialogResult = DialogResult.Cancel };

            _addBtn.Click    += (s, e) => AddEmptyRow();
            _removeBtn.Click += OnRemoveRow;
            _saveBtn.Click   += OnSaveClick;

            var leftPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true
            };
            leftPanel.Controls.Add(_addBtn);
            leftPanel.Controls.Add(_removeBtn);

            var rightPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true
            };
            rightPanel.Controls.Add(_saveBtn);
            rightPanel.Controls.Add(_cancelBtn);

            var buttonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                ColumnCount = 2,
                Padding = new Padding(6, 6, 6, 6)
            };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.Controls.Add(leftPanel,  0, 0);
            buttonRow.Controls.Add(rightPanel, 1, 0);

            Controls.Add(_grid);
            Controls.Add(buttonRow);

            AcceptButton = _saveBtn;
            CancelButton = _cancelBtn;
        }

        private void AddEmptyRow()
        {
            int idx = _grid.Rows.Add();
            // combo cell needs the value explicitly set (empty string is fine)
            _grid.Rows[idx].Cells[ColSchedule].Value = "";
            _grid.Rows[idx].Cells[ColSource].Value   = "";
            _grid.Rows[idx].Cells[ColSheet].Value    = "";
            _grid.Rows[idx].Tag = new List<KeyScheduleFilter>();
            UpdateFilterButtonText(idx);
        }

        private void UpdateFilterButtonText(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;
            var filters = _grid.Rows[rowIndex].Tag as List<KeyScheduleFilter>;
            int n = filters?.Count ?? 0;
            _grid.Rows[rowIndex].Cells[ColFilters].Value = n == 0 ? "Filters..." : $"Filters ({n})";
        }

        private void LoadRows(List<KeyScheduleConfig> configs)
        {
            _grid.Rows.Clear();
            foreach (var cfg in configs)
            {
                // Ensure the existing schedule name is in the combo list
                EnsureScheduleInList(cfg.RevitScheduleName);

                // Source: file path takes priority; fall back to Django endpoint
                string source = !string.IsNullOrEmpty(cfg.ExcelFilePath)
                    ? cfg.ExcelFilePath
                    : cfg.DjangoEndpoint ?? "";

                int idx = _grid.Rows.Add();
                _grid.Rows[idx].Cells[ColSchedule].Value = cfg.RevitScheduleName ?? "";
                _grid.Rows[idx].Cells[ColSource].Value   = source;
                PopulateSheetNames(idx);
                _grid.Rows[idx].Cells[ColSheet].Value    = cfg.ExcelSheetName ?? "";
                _grid.Rows[idx].Tag = cfg.Filters ?? new List<KeyScheduleFilter>();
                UpdateFilterButtonText(idx);
            }
        }

        private void EnsureScheduleInList(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var col = (DataGridViewComboBoxColumn)_grid.Columns[ColSchedule];
            if (!col.Items.Contains(name))
                col.Items.Add(name);
        }

        private void OnEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            int col = _grid.CurrentCell?.ColumnIndex ?? -1;
            if ((col == ColSchedule || col == ColSheet) && e.Control is ComboBox cb)
                cb.DropDownStyle = ComboBoxStyle.DropDown;
        }

        private void OnCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == ColSource)
                PopulateSheetNames(e.RowIndex);
        }

        private void PopulateSheetNames(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return;

            string source = _grid.Rows[rowIndex].Cells[ColSource].Value?.ToString()?.Trim() ?? "";
            var cell = (DataGridViewComboBoxCell)_grid.Rows[rowIndex].Cells[ColSheet];

            string current = cell.Value?.ToString() ?? "";
            cell.Items.Clear();

            if (!IsUrl(source) && File.Exists(source))
            {
                List<string> sheets = GetSheetNames(source);
                foreach (string s in sheets)
                    cell.Items.Add(s);

                if (sheets.Contains(current))
                    cell.Value = current;
                else if (sheets.Count == 1)
                    cell.Value = sheets[0];
                else
                    cell.Value = "";
            }
            else
            {
                cell.Value = current; // preserve whatever was typed
            }
        }

        private static List<string> GetSheetNames(string filePath)
        {
            try
            {
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                    return package.Workbook.Worksheets.Select(ws => ws.Name).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private void OnCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            if (e.ColumnIndex == ColBrowse)
            {
                string current = _grid.Rows[e.RowIndex].Cells[ColSource].Value?.ToString() ?? "";
                if (IsUrl(current)) return;

                using (var dlg = new OpenFileDialog
                {
                    Title  = "Select Excel File",
                    Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                    FileName = File.Exists(current) ? current : ""
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _grid.Rows[e.RowIndex].Cells[ColSource].Value = dlg.FileName;
                        PopulateSheetNames(e.RowIndex);
                    }
                }
            }
            else if (e.ColumnIndex == ColFilters)
            {
                string scheduleName = _grid.Rows[e.RowIndex].Cells[ColSchedule].Value?.ToString()?.Trim();
                if (string.IsNullOrEmpty(scheduleName)) scheduleName = "this schedule";
                var current = _grid.Rows[e.RowIndex].Tag as List<KeyScheduleFilter> ?? new List<KeyScheduleFilter>();
                using (var form = new EditFiltersForm(current, scheduleName))
                {
                    if (form.ShowDialog(this) == DialogResult.OK)
                    {
                        _grid.Rows[e.RowIndex].Tag = form.Result;
                        UpdateFilterButtonText(e.RowIndex);
                    }
                }
            }
        }

        private void OnGridKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.C) return;
            e.Handled = true;

            // If the cell is in edit mode, let the editing control handle copy normally
            if (_grid.IsCurrentCellInEditMode) return;

            object val = _grid.CurrentCell?.Value;
            if (val != null && !(val is DBNull))
                Clipboard.SetText(val.ToString());
        }

        private void OnRemoveRow(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!row.IsNewRow)
                    _grid.Rows.Remove(row);
            }
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            // End any active edit so current cell value is committed
            _grid.EndEdit();

            var warnings = new List<string>();
            int rowNum = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                rowNum++;
                string name   = row.Cells[ColSchedule].Value?.ToString()?.Trim() ?? "";
                string source = row.Cells[ColSource].Value?.ToString()?.Trim()   ?? "";
                string sheet  = row.Cells[ColSheet].Value?.ToString()?.Trim()    ?? "";

                if (string.IsNullOrEmpty(name)) continue;

                if (string.IsNullOrEmpty(source))
                {
                    warnings.Add($"Row {rowNum} ({name}): Source is empty.");
                    continue;
                }

                if (!IsUrl(source))
                {
                    if (string.IsNullOrEmpty(sheet))
                        warnings.Add($"Row {rowNum} ({name}): Sheet Name is required when Source is a file path.");
                    if (!File.Exists(source))
                        warnings.Add($"Row {rowNum} ({name}): File not found — {source}");
                }
            }

            if (warnings.Count > 0)
            {
                string msg = "The following issues were found:\n\n  • " +
                             string.Join("\n  • ", warnings) +
                             "\n\nSave anyway?";
                if (MessageBox.Show(msg, "Perseus: Validation Warnings",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                {
                    DialogResult = DialogResult.None; // keep form open
                    return;
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (DialogResult == DialogResult.OK)
                Result = ExtractConfigs();
        }

        private List<KeyScheduleConfig> ExtractConfigs()
        {
            var list = new List<KeyScheduleConfig>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string name   = row.Cells[ColSchedule].Value?.ToString()?.Trim() ?? "";
                string source = row.Cells[ColSource].Value?.ToString()?.Trim()   ?? "";
                string sheet  = row.Cells[ColSheet].Value?.ToString()?.Trim()    ?? "";

                if (string.IsNullOrEmpty(name)) continue;

                var filters = row.Tag as List<KeyScheduleFilter> ?? new List<KeyScheduleFilter>();

                if (IsUrl(source))
                {
                    list.Add(new KeyScheduleConfig
                    {
                        RevitScheduleName = name,
                        ExcelFilePath     = "",
                        ExcelSheetName    = "",
                        DjangoEndpoint    = source,
                        Filters           = filters
                    });
                }
                else
                {
                    list.Add(new KeyScheduleConfig
                    {
                        RevitScheduleName = name,
                        ExcelFilePath     = source,
                        ExcelSheetName    = sheet,
                        DjangoEndpoint    = "",
                        Filters           = filters
                    });
                }
            }
            return list;
        }

        private static bool IsUrl(string value) =>
            !string.IsNullOrEmpty(value) &&
            (value.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("ftp://",   StringComparison.OrdinalIgnoreCase));
    }
}
