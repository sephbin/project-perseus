using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.violations;

namespace ProjectPerseus.ui
{
    public class ViolationListForm : Form
    {
        public static ViolationListForm Instance { get; private set; }

        private readonly UIDocument _uidoc;
        private readonly Document   _doc;

        private DataGridView _allGrid;
        private DataGridView _selGrid;
        private Label        _selLabel;

        public ViolationListForm(UIDocument uidoc)
        {
            _uidoc    = uidoc;
            _doc      = uidoc.Document;
            Instance  = this;
            BuildUI();
        }

        private void BuildUI()
        {
            Text            = "Perseus — Violation List";
            Size            = new Size(860, 620);
            MinimumSize     = new Size(600, 400);
            StartPosition   = FormStartPosition.Manual;
            Location        = new Point(100, 100);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            var layout = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                RowCount    = 4,
                ColumnCount = 1,
                Padding     = new Padding(6),
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // "All violations" label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,  60));   // all-violations grid
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // selected label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,  40));   // selection grid
            Controls.Add(layout);

            var allLabel = new Label { Text = "All Violations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(allLabel, 0, 0);

            _allGrid = CreateGrid();
            layout.Controls.Add(_allGrid, 0, 1);

            _selLabel = new Label { Text = "Selected Element Violations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(_selLabel, 0, 2);

            _selGrid = CreateGrid();
            layout.Controls.Add(_selGrid, 0, 3);

            _allGrid.CellDoubleClick += OnGridDoubleClick;
            _selGrid.CellDoubleClick += OnGridDoubleClick;

            FormClosed += OnFormClosed;
        }

        private static DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock                  = DockStyle.Fill,
                ReadOnly              = true,
                AllowUserToAddRows    = false,
                AllowUserToDeleteRows = false,
                SelectionMode         = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect           = false,
                RowHeadersVisible     = false,
                AutoSizeColumnsMode   = DataGridViewAutoSizeColumnsMode.Fill,
            };

            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Severity", HeaderText = "Severity", FillWeight = 12 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rule",     HeaderText = "Rule",     FillWeight = 20 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Element",  HeaderText = "Element",  FillWeight = 20 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message",  HeaderText = "Message",  FillWeight = 48 });

            // Tag each column with the DTO field index for easy population.
            return grid;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PopulateAllGrid();
            try { _uidoc.Application.SelectionChanged += OnSelectionChanged; }
            catch (Exception ex) { Log.Warn($"[ViolationListForm] SelectionChanged subscribe failed: {ex.Message}"); }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            try { _uidoc.Application.SelectionChanged -= OnSelectionChanged; }
            catch { /* ignore */ }
            if (Instance == this) Instance = null;
        }

        private void PopulateAllGrid()
        {
            _allGrid.Rows.Clear();
            foreach (var v in ViolationHighlightController.CurrentViolations)
                AddRow(_allGrid, v);
        }

        private static void AddRow(DataGridView grid, ViolationHighlightDto v)
        {
            int idx = grid.Rows.Add();
            var row  = grid.Rows[idx];
            row.Cells["Severity"].Value = v.Severity;
            row.Cells["Rule"].Value     = v.RuleName;
            row.Cells["Element"].Value  = v.ElementName ?? v.ElementUniqueId;
            row.Cells["Message"].Value  = v.Message;
            row.Tag = v;

            // Colour the severity cell
            Color bg = Color.Transparent;
            switch (v.Severity?.ToLower())
            {
                case "error":   bg = Color.FromArgb(255, 220, 220); break;
                case "warning": bg = Color.FromArgb(255, 240, 200); break;
            }
            if (bg != Color.Transparent)
                row.Cells["Severity"].Style.BackColor = bg;
        }

        private void OnSelectionChanged(object sender, Autodesk.Revit.UI.Events.SelectionChangedEventArgs e)
        {
            try
            {
                var selectedIds = e.GetSelectedElements();
                var uids = new HashSet<string>();
                foreach (var id in selectedIds)
                {
                    var el = _doc.GetElement(id);
                    if (el != null) uids.Add(el.UniqueId);
                }

                var matching = ViolationHighlightController.CurrentViolations
                    .Where(v => uids.Contains(v.ElementUniqueId))
                    .ToList();

                BeginInvoke(new Action(() => RefreshSelectionPanel(matching)));
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationListForm] SelectionChanged handler failed: {ex.Message}");
            }
        }

        private void RefreshSelectionPanel(List<ViolationHighlightDto> violations)
        {
            _selLabel.Text = violations.Count > 0
                ? $"Selected Element Violations ({violations.Count})"
                : "Selected Element Violations";
            _selGrid.Rows.Clear();
            foreach (var v in violations)
                AddRow(_selGrid, v);
        }

        private void OnGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            var dto  = grid.Rows[e.RowIndex].Tag as ViolationHighlightDto;
            if (dto == null) return;

            try
            {
                var el = _doc.GetElement(dto.ElementUniqueId);
                if (el == null) return;
                var ids = new List<ElementId> { el.Id };
                _uidoc.Selection.SetElementIds(ids);
                _uidoc.ShowElements(ids);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationListForm] Navigate to element failed: {ex.Message}");
            }
        }
    }
}
