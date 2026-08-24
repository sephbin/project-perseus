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
    public class ViolationListForm : System.Windows.Forms.Form
    {
        public static ViolationListForm Instance { get; private set; }

        private readonly UIDocument _uidoc;
        private readonly Document   _doc;

        private DataGridView _allGrid;
        private DataGridView _typeGrid;
        private DataGridView _selGrid;
        private Label        _typeLabel;
        private Label        _selLabel;

        public ViolationListForm(UIDocument uidoc)
        {
            _uidoc   = uidoc;
            _doc     = uidoc.Document;
            Instance = this;
            BuildUI();
        }

        private void BuildUI()
        {
            Text            = "Perseus — Violation List";
            Size            = new Size(860, 700);
            MinimumSize     = new Size(600, 480);
            StartPosition   = FormStartPosition.Manual;
            Location        = new System.Drawing.Point(100, 100);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            var layout = new TableLayoutPanel
            {
                Dock        = DockStyle.Fill,
                RowCount    = 6,
                ColumnCount = 1,
                Padding     = new Padding(6),
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // "Element Violations" label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,  50));   // instance violations grid
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // "Family / Type Violations" label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,  22));   // type violations grid
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // selected label
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,  28));   // selection grid
            Controls.Add(layout);

            var allLabel = new Label { Text = "Element Violations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(allLabel, 0, 0);

            _allGrid = CreateGrid();
            layout.Controls.Add(_allGrid, 0, 1);

            _typeLabel = new Label { Text = "Family / Type Violations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(_typeLabel, 0, 2);

            _typeGrid = CreateGrid();
            layout.Controls.Add(_typeGrid, 0, 3);

            _selLabel = new Label { Text = "Selected Element Violations", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            layout.Controls.Add(_selLabel, 0, 4);

            _selGrid = CreateGrid();
            layout.Controls.Add(_selGrid, 0, 5);

            _allGrid.CellDoubleClick  += OnInstanceGridDoubleClick;
            _typeGrid.CellDoubleClick += OnTypeGridDoubleClick;
            _selGrid.CellDoubleClick  += OnInstanceGridDoubleClick;

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

            return grid;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PopulateAllGrid();
            PopulateTypeGrid();
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

        private void PopulateTypeGrid()
        {
            _typeGrid.Rows.Clear();
            var typeViolations = ViolationHighlightController.CurrentTypeViolations;
            foreach (var v in typeViolations)
                AddRow(_typeGrid, v);

            // Dim the label when empty so it doesn't draw attention unnecessarily.
            _typeLabel.Text = typeViolations.Count > 0
                ? $"Family / Type Violations ({typeViolations.Count})"
                : "Family / Type Violations";
            _typeLabel.ForeColor = typeViolations.Count > 0
                ? SystemColors.ControlText
                : SystemColors.GrayText;
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

            System.Drawing.Color bg = System.Drawing.Color.Transparent;
            switch (v.Severity?.ToLower())
            {
                case "error":   bg = System.Drawing.Color.FromArgb(255, 220, 220); break;
                case "warning": bg = System.Drawing.Color.FromArgb(255, 240, 200); break;
            }
            if (bg != System.Drawing.Color.Transparent)
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

        // Double-click on an instance violation: select + zoom to element in Revit.
        private void OnInstanceGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            var dto  = grid.Rows[e.RowIndex].Tag as ViolationHighlightDto;
            if (dto == null) return;

            var el = _doc.GetElement(dto.ElementUniqueId);
            if (el == null) return;
            var ids = new List<ElementId> { el.Id };

            try { _uidoc.Selection.SetElementIds(ids); }
            catch (Exception ex) { Log.Warn($"[ViolationListForm] Select failed: {ex.Message}"); }

            try { _uidoc.ShowElements(ids); }
            catch { /* no suitable view — selection still set above */ }
        }

        // Double-click on a family/type violation: selection only — types have no view location.
        private void OnTypeGridDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var dto = _typeGrid.Rows[e.RowIndex].Tag as ViolationHighlightDto;
            if (dto == null) return;

            var el = _doc.GetElement(dto.ElementUniqueId);
            if (el == null) return;

            try { _uidoc.Selection.SetElementIds(new List<ElementId> { el.Id }); }
            catch { /* types may not be selectable in the active view */ }
        }
    }
}
