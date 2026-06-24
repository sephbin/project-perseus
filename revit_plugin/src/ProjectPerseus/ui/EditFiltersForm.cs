using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    public class EditFiltersForm : Form
    {
        private DataGridView _grid;
        private Button _addBtn;
        private Button _removeBtn;
        private Button _okBtn;
        private Button _cancelBtn;

        private const int ColAction     = 0;
        private const int ColExpression = 1;

        public List<KeyScheduleFilter> Result { get; private set; }

        public EditFiltersForm(List<KeyScheduleFilter> existing, string scheduleName)
        {
            InitializeComponent(scheduleName);
            LoadRows(existing);
        }

        private void InitializeComponent(string scheduleName)
        {
            Text = $"Filters — {scheduleName}";
            Width = 660;
            Height = 420;
            MinimumSize = new Size(520, 300);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            // ── Hint panel ─────────────────────────────────────────────────
            var hint = new Label
            {
                Text = "Rules apply top-to-bottom. Each rule whose expression returns truthy sets the row's state (Exclude/Include). "
                     + "Later rules override earlier ones.\r\n"
                     + "Available: row[\"Col\"]  row.get(\"Col\",\"\")  \"x\" in row[\"Col\"]  "
                     + "row[\"Col\"].startswith(\"x\")  .endswith  .lower()  .upper()  .strip()  .replace(a,b)  "
                     + "re.search(r\"pat\",row[\"Col\"])  re.match  re.findall  "
                     + "math.sqrt/floor/ceil/pow/isnan  len()  float()  int()  abs()  round()  and / or / not",
                Dock = DockStyle.Top,
                Height = 58,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(4, 4, 4, 0),
                ForeColor = SystemColors.GrayText,
                Font = new Font("Segoe UI", 8f)
            };

            // ── Grid ───────────────────────────────────────────────────────
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersWidth = 24,
                BackgroundColor = SystemColors.Window
            };

            // Col 0 — Action (Exclude / Include)
            var actionCol = new DataGridViewComboBoxColumn
            {
                Name = "Action",
                HeaderText = "Action",
                FillWeight = 15,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox
            };
            actionCol.Items.Add(FilterAction.Exclude.ToString());
            actionCol.Items.Add(FilterAction.Include.ToString());
            _grid.Columns.Add(actionCol);

            // Col 1 — Python expression (wide text box)
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Expression",
                HeaderText = "Python Expression  (returns True to apply the action)",
                FillWeight = 85,
                DefaultCellStyle = { Font = new Font("Consolas", 9f) },
                ToolTipText = "e.g.  \"-\" in row[\"Key Name\"]   or   re.search(r\"-TBY\", row[\"Key Name\"])"
            });

            _grid.DataError += (s, e) => e.Cancel = true;

            // ── Buttons ────────────────────────────────────────────────────
            _addBtn    = new Button { Text = "Add Rule",    Width = 90,  Height = 28 };
            _removeBtn = new Button { Text = "Remove Rule", Width = 100, Height = 28 };
            _okBtn     = new Button { Text = "OK",          Width = 80,  Height = 28, DialogResult = DialogResult.OK };
            _cancelBtn = new Button { Text = "Cancel",      Width = 80,  Height = 28, DialogResult = DialogResult.Cancel };

            _addBtn.Click    += (s, e) => AddEmptyRow();
            _removeBtn.Click += OnRemoveRow;
            _okBtn.Click     += OnOkClick;

            var leftPanel  = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            leftPanel.Controls.Add(_addBtn);
            leftPanel.Controls.Add(_removeBtn);

            var rightPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            rightPanel.Controls.Add(_okBtn);
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
            Controls.Add(hint);

            AcceptButton = _okBtn;
            CancelButton = _cancelBtn;
        }

        private void AddEmptyRow()
        {
            int idx = _grid.Rows.Add();
            _grid.Rows[idx].Cells[ColAction].Value     = FilterAction.Exclude.ToString();
            _grid.Rows[idx].Cells[ColExpression].Value = "";
        }

        private void LoadRows(List<KeyScheduleFilter> filters)
        {
            _grid.Rows.Clear();
            if (filters == null) return;
            foreach (var f in filters)
            {
                int idx = _grid.Rows.Add();
                _grid.Rows[idx].Cells[ColAction].Value     = f.Action.ToString();
                _grid.Rows[idx].Cells[ColExpression].Value = f.Expression ?? "";
            }
        }

        private void OnRemoveRow(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (!row.IsNewRow)
                    _grid.Rows.Remove(row);
            }
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            _grid.EndEdit();
            Result = ExtractFilters();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (DialogResult == DialogResult.OK && Result == null)
                Result = ExtractFilters();
        }

        private List<KeyScheduleFilter> ExtractFilters()
        {
            var list = new List<KeyScheduleFilter>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                string expr = row.Cells[ColExpression].Value?.ToString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(expr)) continue;

                if (!Enum.TryParse(row.Cells[ColAction].Value?.ToString(), out FilterAction action))
                    action = FilterAction.Exclude;

                list.Add(new KeyScheduleFilter { Action = action, Expression = expr });
            }
            return list;
        }
    }
}
