using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.ownership;
using ProjectPerseus.queue;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modeless panel showing who owns what in the active workshared model: a summary per user (elements borrowed,
    /// worksets owned), and below it the elements and worksets of the selected user. Double-click an element to
    /// select and zoom to it. All Revit API work goes through OwnershipUiEvent because the form has no API context.
    /// </summary>
    public class OwnershipForm : System.Windows.Forms.Form
    {
        public static OwnershipForm Instance { get; private set; }

        private const string AllUsers = "(All users)";
        private static readonly System.Drawing.Color Cyan = System.Drawing.Color.FromArgb(0x00, 0xA0, 0xE0);

        private OwnershipSnapshot _snap;
        private string _signature;
        private Label _info;
        private DataGridView _owners;
        private DataGridView _elements;
        private DataGridView _worksets;
        private TabPage _elementsTab;
        private TabPage _worksetsTab;
        private Button _refresh;
        private Button _relinquish;

        public OwnershipForm(OwnershipSnapshot snapshot)
        {
            Instance = this;
            BuildUI();
            ShowSnapshot(snapshot);
            FormClosed += (s, e) => { if (Instance == this) Instance = null; };
        }

        /// <summary>Opens the panel (or brings it forward and refreshes it). Call from an API context.</summary>
        internal static void Open(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;
            if (!doc.IsWorkshared)
            {
                TaskDialog.Show("Perseus — Ownership", "This model is not workshared, so nobody owns any elements in it.");
                return;
            }

            var snap = OwnershipScanner.Scan(doc);
            var existing = Instance;
            if (existing != null && !existing.IsDisposed)
            {
                existing.ShowSnapshot(snap);
                existing.BringToFront();
                return;
            }
            var form = new OwnershipForm(snap);
            form.Show(new RevitWindow(app.MainWindowHandle));
        }

        internal static bool IsOpen => Instance != null && !Instance.IsDisposed;

        /// <summary>Re-scans the active model if the panel is open. Call from an API context.</summary>
        internal static void RefreshIfOpen(UIApplication app)
        {
            var doc = app?.ActiveUIDocument?.Document;
            if (!IsOpen || doc == null || !doc.IsWorkshared) return;
            Push(OwnershipScanner.Scan(doc));
        }

        /// <summary>
        /// Shows a fresh scan if the panel is open (OwnershipTracker calls this on every recount while it is). The
        /// grids are only rebuilt when ownership actually changed, keeping the user's selection and scroll position.
        /// </summary>
        internal static void Push(OwnershipSnapshot snap)
        {
            var form = Instance;
            if (snap == null || form == null || form.IsDisposed) return;
            try { form.ShowSnapshot(snap); }
            catch (Exception ex) { Log.Warn($"[OwnershipForm] refresh failed: {ex.Message}"); }
        }

        private void BuildUI()
        {
            Text = "Perseus — Element Ownership";
            Size = new Size(900, 680);
            MinimumSize = new Size(620, 460);
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(120, 120);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(6) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            Controls.Add(layout);

            // Header: info text + buttons.
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _info = new Label { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
            _refresh = new Button { Text = "Refresh", AutoSize = true };
            _relinquish = new Button { Text = "Relinquish mine", AutoSize = true };
            new ToolTip().SetToolTip(_relinquish,
                "Give back the elements and worksets you own that have no unsynced changes. Elements you have edited " +
                "stay yours until you sync.");
            _refresh.Click += (s, e) => OwnershipUiEvent.Run(app => RefreshIfOpen(app));
            _relinquish.Click += (s, e) => OwnershipUiEvent.Run(RelinquishMine);
            header.Controls.Add(_info, 0, 0);
            header.Controls.Add(_refresh, 1, 0);
            header.Controls.Add(_relinquish, 2, 0);
            layout.Controls.Add(header, 0, 0);

            _owners = CreateGrid();
            _owners.Columns.Add(new DataGridViewTextBoxColumn { Name = "Owner", HeaderText = "User", FillWeight = 50 });
            _owners.Columns.Add(new DataGridViewTextBoxColumn { Name = "Elements", HeaderText = "Elements borrowed", FillWeight = 25 });
            _owners.Columns.Add(new DataGridViewTextBoxColumn { Name = "Worksets", HeaderText = "Worksets owned", FillWeight = 25 });
            _owners.SelectionChanged += (s, e) => FillDetail();
            layout.Controls.Add(_owners, 0, 1);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            _elementsTab = new TabPage("Elements");
            _elements = CreateGrid();
            _elements.Columns.Add(new DataGridViewTextBoxColumn { Name = "Owner", HeaderText = "Owner", FillWeight = 16 });
            _elements.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "Element Id", FillWeight = 10 });
            _elements.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "Category", FillWeight = 18 });
            _elements.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 36 });
            _elements.Columns.Add(new DataGridViewTextBoxColumn { Name = "Workset", HeaderText = "Workset", FillWeight = 20 });
            _elements.MultiSelect = true;
            _elements.CellDoubleClick += OnElementDoubleClick;
            _elementsTab.Controls.Add(_elements);
            tabs.TabPages.Add(_elementsTab);

            _worksetsTab = new TabPage("Worksets");
            _worksets = CreateGrid();
            _worksets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Owner", HeaderText = "Owner", FillWeight = 25 });
            _worksets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Workset", HeaderText = "Workset", FillWeight = 50 });
            _worksets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Kind", FillWeight = 25 });
            _worksetsTab.Controls.Add(_worksets);
            tabs.TabPages.Add(_worksetsTab);
            layout.Controls.Add(tabs, 0, 2);
        }

        private static DataGridView CreateGrid() => new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };

        private void ShowSnapshot(OwnershipSnapshot snap)
        {
            int mine = snap.Elements.Count(e => OwnershipScanner.SameUser(e.Owner, snap.CurrentUser));
            _info.Text = $"{snap.DocumentTitle} — you are {snap.CurrentUser}, borrowing {mine} element(s).  " +
                         $"As of {snap.TakenAt:HH:mm:ss}; other users' ownership is what Revit knew at your last sync or Reload Latest.";

            // Nothing changed since the last scan: leave the grids alone (no flicker, selection untouched).
            string signature = Signature(snap);
            if (_snap != null && signature == _signature) { _snap = snap; return; }
            _signature = signature;
            _snap = snap;
            string selected = SelectedOwner();

            var users = snap.Elements.Select(e => e.Owner).Concat(snap.Worksets.Select(w => w.Owner))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => OwnershipScanner.SameUser(u, snap.CurrentUser) ? 0 : 1).ThenBy(u => u, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _owners.Rows.Clear();
            AddOwnerRow(AllUsers, snap.Elements.Count, snap.Worksets.Count, false);
            foreach (var u in users)
            {
                AddOwnerRow(u,
                    snap.Elements.Count(e => OwnershipScanner.SameUser(e.Owner, u)),
                    snap.Worksets.Count(w => OwnershipScanner.SameUser(w.Owner, u)),
                    OwnershipScanner.SameUser(u, snap.CurrentUser));
            }

            // Keep the previous selection; default to the current user.
            string target = selected ?? (users.Any(u => OwnershipScanner.SameUser(u, snap.CurrentUser)) ? snap.CurrentUser : AllUsers);
            foreach (DataGridViewRow row in _owners.Rows)
                row.Selected = OwnershipScanner.SameUser((string)row.Tag, target);
            if (_owners.SelectedRows.Count == 0 && _owners.Rows.Count > 0) _owners.Rows[0].Selected = true;
            FillDetail();
        }

        private void AddOwnerRow(string owner, int elements, int worksets, bool isMe)
        {
            int i = _owners.Rows.Add(isMe ? owner + "  (you)" : owner, elements, worksets);
            var row = _owners.Rows[i];
            row.Tag = owner;
            if (isMe)
            {
                row.DefaultCellStyle.Font = new Font(_owners.Font, FontStyle.Bold);
                row.Cells["Owner"].Style.ForeColor = Cyan;
            }
        }

        // Identifies who owns what, so a rescan that found the same ownership can skip redrawing.
        private static string Signature(OwnershipSnapshot snap)
        {
            var sb = new System.Text.StringBuilder(snap.DocumentTitle).Append('|');
            foreach (var e in snap.Elements.OrderBy(x => x.Id)) sb.Append(e.Id).Append(':').Append(e.Owner).Append(';');
            sb.Append('|');
            foreach (var w in snap.Worksets.OrderBy(x => x.Name)) sb.Append(w.Name).Append(':').Append(w.Owner).Append(';');
            return sb.ToString();
        }

        private string SelectedOwner() =>
            _owners.SelectedRows.Count > 0 ? _owners.SelectedRows[0].Tag as string : null;

        private void FillDetail()
        {
            if (_snap == null) return;
            string owner = SelectedOwner();
            bool all = owner == null || owner == AllUsers;

            // Keep the selected elements and scroll position across a refresh.
            var keep = new HashSet<long>(_elements.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as OwnershipSnapshot.ElementRow).Where(r => r != null).Select(r => r.Id));
            int scroll = _elements.FirstDisplayedScrollingRowIndex;

            var elements = _snap.Elements.Where(e => all || OwnershipScanner.SameUser(e.Owner, owner))
                .OrderBy(e => e.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Category).ThenBy(e => e.Id).ToList();
            _elements.SuspendLayout();
            _elements.Rows.Clear();
            foreach (var e in elements)
            {
                int i = _elements.Rows.Add(e.Owner, e.Id, e.Category, e.Name, e.Workset);
                _elements.Rows[i].Tag = e;
            }
            _elements.ClearSelection();
            foreach (DataGridViewRow row in _elements.Rows)
                if (row.Tag is OwnershipSnapshot.ElementRow er && keep.Contains(er.Id)) row.Selected = true;
            if (scroll >= 0 && _elements.Rows.Count > 0)
                _elements.FirstDisplayedScrollingRowIndex = Math.Min(scroll, _elements.Rows.Count - 1);
            _elements.ResumeLayout();
            _elementsTab.Text = $"Elements ({elements.Count})";

            var worksets = _snap.Worksets.Where(w => all || OwnershipScanner.SameUser(w.Owner, owner))
                .OrderBy(w => w.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(w => w.Name).ToList();
            _worksets.Rows.Clear();
            foreach (var w in worksets) _worksets.Rows.Add(w.Owner, w.Name, w.Kind);
            _worksetsTab.Text = $"Worksets ({worksets.Count})";
        }

        private void OnElementDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var uids = _elements.SelectedRows.Cast<DataGridViewRow>()
                .Select(r => r.Tag as OwnershipSnapshot.ElementRow).Where(r => r != null).Select(r => r.UniqueId).ToList();
            if (uids.Count == 0) return;

            OwnershipUiEvent.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                var ids = uids.Select(u => uidoc.Document.GetElement(u)).Where(x => x != null).Select(x => x.Id).ToList();
                if (ids.Count == 0) return;
                uidoc.Selection.SetElementIds(ids);
                try { uidoc.ShowElements(ids); }
                catch { /* types and some elements have no view to show them in; the selection is still set */ }
            });
        }

        private static void RelinquishMine(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared || doc.IsModifiable || doc.IsReadOnly) return;

            var opts = new RelinquishOptions(false)
            {
                CheckedOutElements = true,
                UserWorksets = true,
                StandardWorksets = true,
                ViewWorksets = true,
                FamilyWorksets = true,
            };
            try
            {
                var result = WorksharingUtils.RelinquishOwnership(doc, opts, new TransactWithCentralOptions());
                int elements = result.GetRelinquishedElements().Count;
                int worksets = result.GetRelinquishedWorksets().Count;
                Log.Info($"[Ownership] relinquished {elements} element(s) and {worksets} workset(s)");
                int kept = OwnershipScanner.CountMine(doc, out int keptWorksets);
                TaskDialog.Show("Perseus — Ownership",
                    $"Relinquished {elements} element(s) and {worksets} workset(s)." +
                    (kept > 0 ? $"\n\nYou still own {kept} element(s): they have changes that only a sync can give back." : ""));
            }
            catch (Exception ex)
            {
                Log.Warn($"[Ownership] relinquish failed: {ex.Message}");
                TaskDialog.Show("Perseus — Ownership", "Could not relinquish: " + ex.Message);
            }
            OwnershipTracker.MarkDirty();
            RefreshIfOpen(app);
        }

        private sealed class RevitWindow : IWin32Window
        {
            public RevitWindow(IntPtr hwnd) { Handle = hwnd; }
            public IntPtr Handle { get; }
        }
    }
}
