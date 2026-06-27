using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ProjectPerseus.sync;

namespace ProjectPerseus.ui
{
    internal class PendingEditsReviewForm : Form
    {
        private readonly List<PendingEditItem> _allEdits;

        private TextBox      _searchBox;
        private ComboBox     _statusFilter;
        private Label        _countLabel;
        private DataGridView _grid;
        private Button       _btnApply;

        public List<PendingEditItem> SelectedEdits { get; private set; } = new List<PendingEditItem>();

        public PendingEditsReviewForm(List<PendingEditItem> edits)
        {
            _allEdits = edits;
            Build();
            PopulateGrid();
        }

        private void Build()
        {
            Text          = "Perseus — Review Pending Web Edits";
            Size          = new Size(1060, 580);
            MinimumSize   = new Size(720, 380);
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Segoe UI", 9f);

            // ── Filter bar ──────────────────────────────────────────────────
            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 2, 0, 0) };

            AddLabel(top, "Search:", 10, 13);
            _searchBox = AddTextBox(top, 60, 9, 220);
            _searchBox.TextChanged += (s, e) => ApplyFilter();

            AddLabel(top, "Status:", 294, 13);
            _statusFilter = new ComboBox { Left = 344, Top = 9, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            _statusFilter.Items.AddRange(new object[] { "All", "Pending", "Stale" });
            _statusFilter.SelectedIndex = 0;
            _statusFilter.SelectedIndexChanged += (s, e) => ApplyFilter();
            top.Controls.Add(_statusFilter);

            var btnAll  = AddButton(top, "Select All",   456, 8, 90);
            var btnNone = AddButton(top, "Deselect All", 554, 8, 90);
            btnAll.Click  += (s, e) => SetAllChecked(true);
            btnNone.Click += (s, e) => SetAllChecked(false);

            _countLabel = new Label { Left = 654, Top = 13, Width = 280, AutoSize = true, ForeColor = Color.DimGray };
            top.Controls.Add(_countLabel);

            // ── Grid ────────────────────────────────────────────────────────
            _grid = new DataGridView
            {
                Dock                  = DockStyle.Fill,
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                ReadOnly              = false,
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible     = false,
                BackgroundColor       = SystemColors.Window,
                BorderStyle           = BorderStyle.None,
                MultiSelect           = true,
            };

            _grid.Columns.AddRange(
                new DataGridViewCheckBoxColumn { Name = "chk", HeaderText = "",               Width = 36,  AutoSizeMode = DataGridViewAutoSizeColumnMode.None },
                new DataGridViewTextBoxColumn  { Name = "el",  HeaderText = "Element",        FillWeight = 20, ReadOnly = true },
                new DataGridViewTextBoxColumn  { Name = "par", HeaderText = "Parameter",      FillWeight = 16, ReadOnly = true },
                new DataGridViewTextBoxColumn  { Name = "cur", HeaderText = "Current Value",  FillWeight = 14, ReadOnly = true },
                new DataGridViewTextBoxColumn  { Name = "nv",  HeaderText = "New Value",      FillWeight = 14, ReadOnly = true },
                new DataGridViewTextBoxColumn  { Name = "by",  HeaderText = "Edited By",      FillWeight = 12, ReadOnly = true },
                new DataGridViewTextBoxColumn  { Name = "st",  HeaderText = "Status",         FillWeight = 8,  ReadOnly = true }
            );

            // Commit checkbox clicks immediately so the count stays in sync
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.CellValueChanged += (s, e) =>
            {
                if (e.ColumnIndex == _grid.Columns["chk"].Index) UpdateCountLabel();
            };
            _grid.CellFormatting += (s, e) =>
            {
                if (e.ColumnIndex != _grid.Columns["st"].Index || e.Value == null) return;
                switch (e.Value.ToString())
                {
                    case "stale":   e.CellStyle.ForeColor = Color.OrangeRed; break;
                    case "pending": e.CellStyle.ForeColor = Color.FromArgb(160, 100, 0); break;
                }
            };

            // ── Bottom buttons ──────────────────────────────────────────────
            var bottom = new FlowLayoutPanel
            {
                Dock          = DockStyle.Bottom,
                Height        = 46,
                FlowDirection = FlowDirection.RightToLeft,
                Padding       = new Padding(8, 7, 8, 7),
            };
            var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            _btnApply = new Button
            {
                Text      = "Apply Selected",
                Width     = 140,
                Height    = 30,
                BackColor = Color.FromArgb(26, 26, 46),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _btnApply.Click += BtnApply_Click;
            bottom.Controls.AddRange(new Control[] { btnCancel, _btnApply });

            Controls.AddRange(new Control[] { _grid, top, bottom });
            CancelButton = btnCancel;
        }

        private void PopulateGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var edit in _allEdits)
            {
                int i = _grid.Rows.Add(
                    true,
                    edit.ElementName ?? edit.ElementUniqueId,
                    edit.ParamName,
                    edit.CurrentRevitValue ?? "",
                    edit.NewValue ?? "",
                    edit.EditedBy ?? "",
                    edit.Status ?? ""
                );
                _grid.Rows[i].Tag = edit;
            }
            _grid.ResumeLayout();
            UpdateCountLabel();
        }

        private void ApplyFilter()
        {
            string search = _searchBox.Text.Trim().ToLowerInvariant();
            string status = _statusFilter.SelectedItem?.ToString() ?? "All";

            _grid.ClearSelection();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!(row.Tag is PendingEditItem edit)) continue;

                bool matchSearch = string.IsNullOrEmpty(search)
                    || Hits(edit.ElementName,       search)
                    || Hits(edit.ParamName,         search)
                    || Hits(edit.EditedBy,          search)
                    || Hits(edit.NewValue,          search)
                    || Hits(edit.CurrentRevitValue, search);

                bool matchStatus = status == "All"
                    || string.Equals(edit.Status, status, StringComparison.OrdinalIgnoreCase);

                row.Visible = matchSearch && matchStatus;
            }
            UpdateCountLabel();
        }

        private static bool Hits(string source, string term)
            => !string.IsNullOrEmpty(source) && source.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        private void SetAllChecked(bool value)
        {
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Visible) row.Cells["chk"].Value = value;
            UpdateCountLabel();
        }

        private void UpdateCountLabel()
        {
            int visible  = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);
            int selected = _grid.Rows.Cast<DataGridViewRow>()
                               .Count(r => r.Visible && IsChecked(r));
            _countLabel.Text = $"{selected} of {visible} selected";
            _btnApply.Text   = $"Apply Selected ({selected})";
        }

        private static bool IsChecked(DataGridViewRow row)
            => row.Cells["chk"].Value is bool b && b;

        private void BtnApply_Click(object sender, EventArgs e)
        {
            SelectedEdits = _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is PendingEditItem && IsChecked(r))
                .Select(r => (PendingEditItem)r.Tag)
                .ToList();

            if (SelectedEdits.Count == 0)
            {
                MessageBox.Show("No edits selected — tick at least one row to apply.",
                    "Perseus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        // ── Layout helpers ───────────────────────────────────────────────
        private static void AddLabel(Panel p, string text, int left, int top)
        {
            p.Controls.Add(new Label { Text = text, AutoSize = true, Left = left, Top = top });
        }

        private static TextBox AddTextBox(Panel p, int left, int top, int width)
        {
            var t = new TextBox { Left = left, Top = top, Width = width };
            p.Controls.Add(t);
            return t;
        }

        private static Button AddButton(Panel p, string text, int left, int top, int width)
        {
            var b = new Button { Text = text, Left = left, Top = top, Width = width, Height = 26 };
            p.Controls.Add(b);
            return b;
        }
    }
}
