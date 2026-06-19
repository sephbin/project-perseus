using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
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

        public List<KeyScheduleConfig> Result { get; private set; }

        public ManageSchedulesForm(List<KeyScheduleConfig> existing)
        {
            InitializeComponent();
            LoadRows(existing);
        }

        private void InitializeComponent()
        {
            Text = "Perseus: Key Schedule Mappings";
            Width = 920;
            Height = 460;
            MinimumSize = new Size(700, 350);
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

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "RevitScheduleName",
                HeaderText = "Revit Schedule Name",
                FillWeight = 22
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ExcelFilePath",
                HeaderText = "Excel File Path",
                FillWeight = 38,
                ToolTipText = "Full path to the .xlsx file"
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "ExcelSheetName",
                HeaderText = "Sheet Name",
                FillWeight = 18
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "DjangoEndpoint",
                HeaderText = "Django Endpoint (optional)",
                FillWeight = 22,
                ToolTipText = "Leave blank to skip Django sync for this schedule"
            });

            _addBtn    = new Button { Text = "Add Row",    Width = 90,  Height = 28 };
            _removeBtn = new Button { Text = "Remove Row", Width = 100, Height = 28 };
            _saveBtn   = new Button { Text = "Save",       Width = 80,  Height = 28, DialogResult = DialogResult.OK };
            _cancelBtn = new Button { Text = "Cancel",     Width = 80,  Height = 28, DialogResult = DialogResult.Cancel };

            _addBtn.Click    += (s, e) => _grid.Rows.Add("", "", "", "");
            _removeBtn.Click += OnRemoveRow;

            var hint = new Label
            {
                Text = "Changes are saved to the Revit model (Extensible Storage) and travel with the project file.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 8.5f)
            };

            var leftPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            leftPanel.Controls.Add(_addBtn);
            leftPanel.Controls.Add(_removeBtn);

            var rightPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            rightPanel.Controls.Add(_saveBtn);
            rightPanel.Controls.Add(_cancelBtn);

            var buttonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                ColumnCount = 3,
                Padding = new Padding(6, 6, 6, 6)
            };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.Controls.Add(leftPanel, 0, 0);
            buttonRow.Controls.Add(hint,      1, 0);
            buttonRow.Controls.Add(rightPanel, 2, 0);

            Controls.Add(_grid);
            Controls.Add(buttonRow);

            AcceptButton = _saveBtn;
            CancelButton = _cancelBtn;
        }

        private void LoadRows(List<KeyScheduleConfig> configs)
        {
            _grid.Rows.Clear();
            foreach (var cfg in configs)
                _grid.Rows.Add(
                    cfg.RevitScheduleName ?? "",
                    cfg.ExcelFilePath    ?? "",
                    cfg.ExcelSheetName   ?? "",
                    cfg.DjangoEndpoint   ?? "");
        }

        private void OnRemoveRow(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!row.IsNewRow)
                    _grid.Rows.Remove(row);
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
                string name = row.Cells["RevitScheduleName"].Value?.ToString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new KeyScheduleConfig
                {
                    RevitScheduleName = name,
                    ExcelFilePath     = row.Cells["ExcelFilePath"].Value?.ToString()?.Trim() ?? "",
                    ExcelSheetName    = row.Cells["ExcelSheetName"].Value?.ToString()?.Trim() ?? "",
                    DjangoEndpoint    = row.Cells["DjangoEndpoint"].Value?.ToString()?.Trim() ?? ""
                });
            }
            return list;
        }
    }
}
