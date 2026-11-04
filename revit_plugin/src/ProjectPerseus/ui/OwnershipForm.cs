using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// Modeless ownership panel, laid out as a narrow column (like Revit's Properties palette) so it reads well at a
    /// quarter or a fifth of the screen width. Top to bottom: who you are and what you hold, Refresh / Relinquish,
    /// a compact per-user summary, then a tree of the selected user's worksets and elements grouped by category.
    /// Double-click an element (or right-click any group) to select and zoom to it in Revit. Kept current by
    /// OwnershipTracker (Push). All Revit API work goes through OwnershipUiEvent because the form has no API context.
    /// </summary>
    public class OwnershipForm : System.Windows.Forms.Form
    {
        public static OwnershipForm Instance { get; private set; }

        private const string AllUsers = "All users";
        private static readonly System.Drawing.Color Cyan = System.Drawing.Color.FromArgb(0x00, 0xA0, 0xE0);
        private static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(0x6B, 0x75, 0x85);
        private const int AutoExpandLimit = 40; // expand every group when the tree holds this few elements

        // Size and position survive closing and reopening within the session.
        private static System.Drawing.Rectangle? _lastBounds;

        private OwnershipSnapshot _snap;
        private string _signature;

        private Label _title;
        private Label _summary;
        private DataGridView _owners;
        private Label _listHeading;
        private TreeView _tree;
        private Label _footnote;
        private TableLayoutPanel _layout;
        private Font _leafFont;

        public OwnershipForm(OwnershipSnapshot snapshot)
        {
            Instance = this;
            BuildUI();
            ShowSnapshot(snapshot);
            FormClosing += (s, e) => _lastBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
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
            form.PlaceBesideRevit(app.MainWindowHandle);
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
        /// lists are only rebuilt when ownership actually changed, keeping selection, expanded groups and scroll.
        /// </summary>
        internal static void Push(OwnershipSnapshot snap)
        {
            var form = Instance;
            if (snap == null || form == null || form.IsDisposed) return;
            try { form.ShowSnapshot(snap); }
            catch (Exception ex) { Log.Warn($"[OwnershipForm] refresh failed: {ex.Message}"); }
        }

        // ── Layout ──────────────────────────────────────────────────────────────

        private void BuildUI()
        {
            Text = "Perseus — Ownership";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(340, 760);
            MinimumSize = new Size(240, 420);
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;

            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(8, 6, 8, 6) };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // title
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // your summary
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); // users (resized to fit its rows)
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // list heading
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // tree
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // footnote
            Controls.Add(_layout);

            _title = WrapLabel(new Font(Font.FontFamily, 10f, FontStyle.Bold), SystemColors.ControlText);
            _summary = WrapLabel(Font, Cyan);
            _summary.Font = new Font(Font, FontStyle.Bold);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 6) };
            var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            var relinquish = new Button { Text = "Relinquish mine", AutoSize = true, Margin = new Padding(0) };
            new ToolTip().SetToolTip(relinquish,
                "Give back the elements and worksets you own that have no unsynced changes.\n" +
                "Elements you have edited stay yours until you sync.");
            refresh.Click += (s, e) => OwnershipUiEvent.Run(app => RefreshIfOpen(app));
            relinquish.Click += (s, e) => OwnershipUiEvent.Run(RelinquishMine);
            buttons.Controls.Add(refresh);
            buttons.Controls.Add(relinquish);

            _owners = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackgroundColor = SystemColors.Window,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 22,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                Margin = new Padding(0, 0, 0, 8),
            };
            _owners.RowTemplate.Height = 22;
            _owners.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Owner", HeaderText = "User", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            });
            _owners.Columns.Add(NumberColumn("Elements", "Elements"));
            _owners.Columns.Add(NumberColumn("Worksets", "Worksets"));
            _owners.SelectionChanged += (s, e) => FillTree();

            _listHeading = WrapLabel(new Font(Font, FontStyle.Bold), SystemColors.ControlText);

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowNodeToolTips = true,
                FullRowSelect = true,
                ShowLines = false,
                ItemHeight = 20,
                BorderStyle = BorderStyle.FixedSingle,
                // Bold tree font: WinForms clips node text drawn wider than the tree font, so group nodes use the
                // tree's bold font and leaves get the narrower regular LeafFont.
                Font = new Font(Font, FontStyle.Bold),
            };
            _leafFont = new Font(Font, FontStyle.Regular);
            _tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is OwnershipSnapshot.ElementRow) SelectInRevit(e.Node); };
            _tree.NodeMouseClick += (s, e) => { if (e.Button == MouseButtons.Right) _tree.SelectedNode = e.Node; };
            _tree.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && _tree.SelectedNode != null) SelectInRevit(_tree.SelectedNode); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Select and zoom in Revit", null, (s, e) => { if (_tree.SelectedNode != null) SelectInRevit(_tree.SelectedNode); });
            _tree.ContextMenuStrip = menu;

            _footnote = WrapLabel(new Font(Font.FontFamily, 8f), Muted);
            _footnote.Margin = new Padding(0, 6, 0, 0);

            _layout.Controls.Add(_title, 0, 0);
            _layout.Controls.Add(_summary, 0, 1);
            _layout.Controls.Add(buttons, 0, 2);
            _layout.Controls.Add(_owners, 0, 3);
            _layout.Controls.Add(_listHeading, 0, 4);
            _layout.Controls.Add(_tree, 0, 5);
            _layout.Controls.Add(_footnote, 0, 6);

            // Wrapping labels need a maximum width that follows the panel width.
            Resize += (s, e) => FitLabels();
            FitLabels();
        }

        private static DataGridViewTextBoxColumn NumberColumn(string name, string header) => new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = 62,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight },
            HeaderCell = { Style = { Alignment = DataGridViewContentAlignment.MiddleRight } },
        };

        private static Label WrapLabel(Font font, System.Drawing.Color color) => new Label
        {
            AutoSize = true,
            Font = font,
            ForeColor = color,
            Margin = new Padding(0, 0, 0, 4),
        };

        private void FitLabels()
        {
            int width = Math.Max(120, ClientSize.Width - _layout.Padding.Horizontal - 4);
            foreach (var l in new[] { _title, _summary, _listHeading, _footnote })
                l.MaximumSize = new Size(width, 0);
        }

        // First open: a column down the right-hand side of the Revit window, below the ribbon, a fifth of its width.
        private void PlaceBesideRevit(IntPtr revit)
        {
            if (_lastBounds.HasValue) { Bounds = _lastBounds.Value; return; }
            if (!GetWindowRect(revit, out RECT r)) { Location = new System.Drawing.Point(120, 120); return; }
            int width = Math.Max(300, Math.Min(460, (r.Right - r.Left) / 5));
            int top = r.Top + 170;
            int height = Math.Max(MinimumSize.Height, r.Bottom - top - 60);
            Bounds = new System.Drawing.Rectangle(r.Right - width - 24, top, width, height);
        }

        // ── Content ─────────────────────────────────────────────────────────────

        private void ShowSnapshot(OwnershipSnapshot snap)
        {
            int mine = snap.Elements.Count(e => OwnershipScanner.SameUser(e.Owner, snap.CurrentUser));
            int myWorksets = snap.Worksets.Count(w => OwnershipScanner.SameUser(w.Owner, snap.CurrentUser));
            _title.Text = snap.DocumentTitle;
            _summary.Text = $"You ({snap.CurrentUser}) own {Plural(mine, "element")}" +
                            (myWorksets > 0 ? $" and {Plural(myWorksets, "workset")}" : "");
            _footnote.Text = $"Updated {snap.TakenAt:HH:mm:ss}. Other users' ownership is as of your last sync or Reload Latest.";

            // Nothing changed since the last scan: leave the lists alone (no flicker, selection untouched).
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
            foreach (var u in users)
            {
                AddOwnerRow(u,
                    snap.Elements.Count(e => OwnershipScanner.SameUser(e.Owner, u)),
                    snap.Worksets.Count(w => OwnershipScanner.SameUser(w.Owner, u)),
                    OwnershipScanner.SameUser(u, snap.CurrentUser));
            }
            if (users.Count > 1) AddOwnerRow(AllUsers, snap.Elements.Count, snap.Worksets.Count, false, muted: true);

            // Show every user row up to 6, then scroll.
            int rows = Math.Max(1, Math.Min(6, _owners.Rows.Count));
            _layout.RowStyles[3].Height = _owners.ColumnHeadersHeight + rows * _owners.RowTemplate.Height + 3 + _owners.Margin.Vertical;

            // Keep the previous selection; default to the current user.
            string target = selected ?? (users.Any(u => OwnershipScanner.SameUser(u, snap.CurrentUser)) ? snap.CurrentUser : users.FirstOrDefault());
            _owners.ClearSelection();
            foreach (DataGridViewRow row in _owners.Rows)
                if (OwnershipScanner.SameUser((string)row.Tag, target)) { row.Selected = true; break; }
            if (_owners.SelectedRows.Count == 0 && _owners.Rows.Count > 0) _owners.Rows[0].Selected = true;
            FillTree();
        }

        private void AddOwnerRow(string owner, int elements, int worksets, bool isMe, bool muted = false)
        {
            int i = _owners.Rows.Add(owner, elements, worksets);
            var row = _owners.Rows[i];
            row.Tag = owner;
            if (isMe)
            {
                row.DefaultCellStyle.Font = new Font(_owners.Font, FontStyle.Bold);
                row.Cells["Owner"].Style.ForeColor = Cyan;
                row.Cells["Owner"].ToolTipText = "You";
            }
            else if (muted)
            {
                row.DefaultCellStyle.ForeColor = Muted;
            }
        }

        private string SelectedOwner() =>
            _owners.SelectedRows.Count > 0 ? _owners.SelectedRows[0].Tag as string : null;

        // Tree for the selected user: Worksets, then one group per category. "All users" adds a user level on top.
        private void FillTree()
        {
            if (_snap == null) return;
            string owner = SelectedOwner();
            bool all = owner == null || owner == AllUsers;

            var elements = _snap.Elements.Where(e => all || OwnershipScanner.SameUser(e.Owner, owner)).ToList();
            var worksets = _snap.Worksets.Where(w => all || OwnershipScanner.SameUser(w.Owner, owner)).ToList();
            _listHeading.Text = all
                ? $"Everyone: {Plural(elements.Count, "element")}, {Plural(worksets.Count, "workset")}"
                : $"{(OwnershipScanner.SameUser(owner, _snap.CurrentUser) ? "Yours" : owner)}: " +
                  $"{Plural(elements.Count, "element")}, {Plural(worksets.Count, "workset")}";

            // Remember what was open, selected and scrolled to.
            var expanded = new HashSet<string>(AllNodes(_tree.Nodes).Where(n => n.IsExpanded).Select(n => n.Name));
            string selectedKey = _tree.SelectedNode?.Name;
            string topKey = _tree.TopNode?.Name;
            bool firstFill = _tree.Nodes.Count == 0 && expanded.Count == 0;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                if (elements.Count == 0 && worksets.Count == 0)
                {
                    _tree.Nodes.Add(new TreeNode("Nothing owned") { ForeColor = Muted, NodeFont = _leafFont });
                }
                else if (all)
                {
                    var users = elements.Select(e => e.Owner).Concat(worksets.Select(w => w.Owner))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(u => OwnershipScanner.SameUser(u, _snap.CurrentUser) ? 0 : 1).ThenBy(u => u, StringComparer.OrdinalIgnoreCase);
                    foreach (var u in users)
                    {
                        var mineE = elements.Where(e => OwnershipScanner.SameUser(e.Owner, u)).ToList();
                        var mineW = worksets.Where(w => OwnershipScanner.SameUser(w.Owner, u)).ToList();
                        bool isMe = OwnershipScanner.SameUser(u, _snap.CurrentUser);
                        var userNode = new TreeNode($"{u}{(isMe ? " (you)" : "")}   {mineE.Count}")
                        {
                            Name = "u:" + u.ToLowerInvariant(),
                            ForeColor = isMe ? Cyan : SystemColors.ControlText,
                        };
                        AddGroups(userNode.Nodes, userNode.Name, mineE, mineW);
                        _tree.Nodes.Add(userNode);
                    }
                }
                else
                {
                    AddGroups(_tree.Nodes, "u:" + owner.ToLowerInvariant(), elements, worksets);
                }

                bool expandAll = firstFill && elements.Count <= AutoExpandLimit;
                foreach (var n in AllNodes(_tree.Nodes))
                {
                    if (n.Nodes.Count == 0) continue;
                    bool isUser = n.Name.StartsWith("u:", StringComparison.Ordinal) && n.Parent == null;
                    if (expanded.Contains(n.Name) || expandAll || (firstFill && isUser)) n.Expand();
                }
                var sel = Find(selectedKey);
                if (sel != null) _tree.SelectedNode = sel;
                var top = Find(topKey);
                if (top != null) _tree.TopNode = top;
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void AddGroups(TreeNodeCollection into, string prefix, List<OwnershipSnapshot.ElementRow> elements,
                               List<OwnershipSnapshot.WorksetRow> worksets)
        {
            if (worksets.Count > 0)
            {
                var group = new TreeNode($"Worksets   {worksets.Count}") { Name = prefix + "|ws" };
                foreach (var w in worksets.OrderBy(w => w.Name))
                    group.Nodes.Add(new TreeNode(w.Name)
                    {
                        Name = prefix + "|ws|" + w.Name,
                        NodeFont = _leafFont,
                        ToolTipText = $"{w.Kind} workset, owned by {w.Owner}.\nEvery element on it is editable only by {w.Owner}.",
                    });
                into.Add(group);
            }

            foreach (var cat in elements.GroupBy(e => e.Category).OrderBy(g => g.Key))
            {
                var group = new TreeNode($"{cat.Key}   {cat.Count()}")
                {
                    Name = prefix + "|c|" + cat.Key,
                    Tag = cat.ToList(),
                    ToolTipText = "Right-click to select all of these in Revit.",
                };
                foreach (var e in cat.OrderBy(x => ShortName(x)).ThenBy(x => x.Id))
                {
                    string name = string.IsNullOrEmpty(e.Name) ? e.Category : e.Name;
                    group.Nodes.Add(new TreeNode($"{ShortName(e)}  ·  {e.Id}")
                    {
                        Name = "e:" + e.Id,
                        Tag = e,
                        NodeFont = _leafFont,
                        ToolTipText = $"{name}\nElement Id {e.Id}\nWorkset: {(string.IsNullOrEmpty(e.Workset) ? "—" : e.Workset)}\n" +
                                      $"Owner: {e.Owner}\nDouble-click to select and zoom.",
                    });
                }
                into.Add(group);
            }
        }

        // "Basic Wall: Interior - 110 Partition" -> "Interior - 110 Partition"; the full name is in the tooltip.
        private static string ShortName(OwnershipSnapshot.ElementRow e)
        {
            string name = string.IsNullOrEmpty(e.Name) ? e.Category : e.Name;
            int colon = name.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 && colon + 2 < name.Length ? name.Substring(colon + 2) : name;
        }

        private TreeNode Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var hits = _tree.Nodes.Find(key, true);
            return hits.Length > 0 ? hits[0] : null;
        }

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                yield return n;
                foreach (var c in AllNodes(n.Nodes)) yield return c;
            }
        }

        private static string Plural(int n, string word) => $"{n:N0} {word}{(n == 1 ? "" : "s")}";

        // Identifies who owns what, so a rescan that found the same ownership can skip redrawing.
        private static string Signature(OwnershipSnapshot snap)
        {
            var sb = new System.Text.StringBuilder(snap.DocumentTitle).Append('|');
            foreach (var e in snap.Elements.OrderBy(x => x.Id)) sb.Append(e.Id).Append(':').Append(e.Owner).Append(';');
            sb.Append('|');
            foreach (var w in snap.Worksets.OrderBy(x => x.Name)) sb.Append(w.Name).Append(':').Append(w.Owner).Append(';');
            return sb.ToString();
        }

        // ── Revit actions ───────────────────────────────────────────────────────

        // Selects (and zooms to) the element under the node, or every element in the group under it.
        private static void SelectInRevit(TreeNode node)
        {
            var rows = new List<OwnershipSnapshot.ElementRow>();
            if (node.Tag is OwnershipSnapshot.ElementRow one) rows.Add(one);
            foreach (var n in AllNodes(node.Nodes))
                if (n.Tag is OwnershipSnapshot.ElementRow r) rows.Add(r);
            var ids = rows.Select(r => r.UniqueId).Distinct().ToList();
            if (ids.Count == 0) return;

            OwnershipUiEvent.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                var elementIds = ids.Select(u => uidoc.Document.GetElement(u)).Where(x => x != null).Select(x => x.Id).ToList();
                if (elementIds.Count == 0) return;
                uidoc.Selection.SetElementIds(elementIds);
                try { uidoc.ShowElements(elementIds); }
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

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

        private sealed class RevitWindow : IWin32Window
        {
            public RevitWindow(IntPtr hwnd) { Handle = hwnd; }
            public IntPtr Handle { get; }
        }
    }
}
