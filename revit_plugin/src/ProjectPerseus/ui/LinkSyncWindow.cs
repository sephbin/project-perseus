using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UIApplication = Autodesk.Revit.UI.UIApplication;
using ProjectPerseus.federator;
using ProjectPerseus.logging;
using ProjectPerseus.mapping;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modeless "Link Sync" (ribbon: Federator > Link Sync, commands/ShowLinkSyncCommand): keeps our mapped families
    /// matching the consultant links. Lists every change (mapping/LinkSync -> mapping/LinkSyncPlanner) - new, match,
    /// moved, type changed, replaced, orphaned, kept by hand / rejected - one row per change, so a move and a type change
    /// of the same family are approved / rejected separately. Scope: all mapped links / a choice, optionally only what's
    /// in the active view (ours or the consultant's visible; follows view switches). Checking tools: zoom (review views),
    /// 3D, select ours / consultant. Approve / reject selected, approve all shown, write the Perseus_LinkSource /
    /// Perseus_LinkStatus / Perseus_LinkDecision parameters. Who decided (accepted / rejected, when) and who last edited
    /// ours / the consultant element are shown per row; comments and assignments (mapping/LinkSyncLog, append-only) on
    /// one or many rows, also written to Perseus_LinkComment / Perseus_LinkAssignee where ours is editable.
    /// Model work runs through queue/OwnershipUiEvent.
    /// </summary>
    internal class LinkSyncWindow : Window
    {
        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);
        private static readonly Brush Bad = Frozen(0xC0, 0x26, 0x26);

        internal static LinkSyncWindow Instance { get; private set; }

        private readonly ARDB.Document _doc;
        private readonly string _me;
        private List<LinkSync.LinkInfo> _links = new List<LinkSync.LinkInfo>();
        private List<SyncItem> _items = new List<SyncItem>();
        private readonly DataGrid _grid;
        private readonly MultiPick _linkPick, _kindPick;
        private readonly CheckBox _inView;
        private readonly TextBlock _summary, _status, _title, _detail;
        private readonly StackPanel _side, _thread;
        private readonly WrapPanel _checkPanel, _decidePanel;
        private readonly StackPanel _notePanel;
        private readonly TextBox _commentBox;
        private readonly ComboBox _assignBox, _assignFilter;
        private List<LinkSyncEvent> _log = new List<LinkSyncEvent>();
        private Dictionary<string, List<LinkSyncEvent>> _logIndex = new Dictionary<string, List<LinkSyncEvent>>();
        private const string AnyoneFilter = "Anyone", MeFilter = "Assigned to me", UnassignedFilter = "Unassigned";
        private readonly DispatcherTimer _viewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        // Our tracked families (ids) from the last read: a change to one of them (moved, retyped, deleted) re-reads the list.
        private volatile HashSet<long> _watched = new HashSet<long>();
        private readonly DispatcherTimer _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        private bool _linksLoaded;

        private static readonly SyncKind[] DecisionKinds =
            { SyncKind.New, SyncKind.Match, SyncKind.Moved, SyncKind.ConsultantType, SyncKind.MappingType, SyncKind.Replaced, SyncKind.Orphaned };

        private class Row
        {
            public string Change { get; set; }
            public string Link { get; set; }
            public string Category { get; set; }
            public string Consultant { get; set; }
            public string Ours { get; set; }
            public string Detail { get; set; }
            public string Decision { get; set; }
            public string EditedBy { get; set; }
            public string Assignee { get; set; }
            public string Comments { get; set; }
            internal SyncItem Item;
            internal LinkSyncNotes Notes;
        }

        /// <summary>Opens the window on the active model (or brings it to the front). API context.</summary>
        internal static void Open(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Link Sync", "Open a project model with consultant links first.");
                return;
            }
            if (Instance != null && Instance.IsLoaded && doc.Equals(Instance._doc)) { Instance.Activate(); return; }
            Instance?.Close();
            var w = new LinkSyncWindow(doc, app.Application.Username);
            new System.Windows.Interop.WindowInteropHelper(w).Owner = app.MainWindowHandle;
            w.Show();
        }

        private LinkSyncWindow(ARDB.Document doc, string me)
        {
            Instance = this;
            _doc = doc; _me = me;
            Title = "Perseus — Link Sync";
            Width = 1240; Height = 740; MinWidth = 900; MinHeight = 460;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Ink;
            Background = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(12) };
            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8),
                Text = "Keeps our mapped families matching the consultant links (rules from Family Mapping). Every change is listed on its own: approve it to take " +
                       "the consultant's state, reject it to keep ours. Families moved by hand (beyond the mapping's tolerance) are listed as kept: approve to put them " +
                       "back. The list updates when our families change. Use 'Only in the active view' to work your own zone.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _linkPick = new MultiPick(220) { ToolTip = "Mapped links to check" };
            _kindPick = new MultiPick(200) { ToolTip = "Which changes to list" };
            _kindPick.SetItems(Enum.GetValues(typeof(SyncKind)).Cast<SyncKind>().Select(k => new MultiPick.Item(k.ToString(), LinkSyncPlanner.Label(k))));
            foreach (SyncKind k in Enum.GetValues(typeof(SyncKind))) _kindPick.SetTicked(k.ToString(), LinkSyncPlanner.NeedsDecision(k) || k == SyncKind.KeptByHand);
            _kindPick.AddPreset("Changes", "Everything that differs from the consultant: decisions + edited by hand",
                                DecisionKinds.Select(k => k.ToString()).Concat(new[] { SyncKind.KeptByHand.ToString() }).ToArray());
            _kindPick.AddPreset("Needs a decision", "New, match, moved, type changes, replaced, orphaned", DecisionKinds.Select(k => k.ToString()).ToArray());
            _kindPick.AddPreset("Kept", "Kept by hand / rejected", SyncKind.KeptByHand.ToString(), SyncKind.KeptRejected.ToString());
            _inView = new CheckBox { Content = "Only in the active view", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                     ToolTip = "Only our families or consultant elements visible in the active view (follows view switches)" };
            tools.Children.Add(Label("Links "));
            tools.Children.Add(_linkPick);
            tools.Children.Add(Label("Show "));
            tools.Children.Add(_kindPick);
            tools.Children.Add(_inView);
            _assignFilter = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Only changes assigned to someone" };
            _assignFilter.Items.Add(AnyoneFilter); _assignFilter.Items.Add(MeFilter); _assignFilter.Items.Add(UnassignedFilter);
            _assignFilter.SelectedIndex = 0;
            tools.Children.Add(_assignFilter);
            tools.Children.Add(Btn("Refresh", "Read the links and our families again", Refresh));
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            tools.Children.Add(_summary);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var actions = new WrapPanel();
            actions.Children.Add(Btn("Approve selected", "Take the consultant's state for the selected changes (one undo step)", () => DecideSelected(true)));
            actions.Children.Add(Btn("Reject selected", "Keep ours for the selected changes (not proposed again until it changes once more)", () => DecideSelected(false)));
            actions.Children.Add(Btn("Approve all shown…", "Approve every change listed that needs a decision", ApproveAllShown));
            actions.Children.Add(Btn("Write status parameters", "Write Perseus_LinkSource / Status / Decision / Comment / Assignee on our mapped families (for schedules, filters, tags)", WriteStatus));
            actions.Children.Add(Btn("Review views…", "Choose the views used to show changes (also on the ribbon)", OpenReviewViews));
            DockPanel.SetDock(actions, Dock.Top);
            bottom.Children.Add(actions);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star), MinWidth = 520 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            _grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush, Background = Brushes.White,
                RowHeaderWidth = 0, CanUserAddRows = false,
            };
            Col("Change", nameof(Row.Change), 150);
            Col("Link", nameof(Row.Link), 110);
            Col("Category", nameof(Row.Category), 110);
            Col("Consultant element", nameof(Row.Consultant), 0);
            Col("Ours", nameof(Row.Ours), 0);
            Col("Decision", nameof(Row.Decision), 150);
            Col("Edited by", nameof(Row.EditedBy), 95);
            Col("Assignee", nameof(Row.Assignee), 95);
            Col("💬", nameof(Row.Comments), 32);
            Grid.SetColumn(_grid, 0);
            body.Children.Add(_grid);

            _side = new StackPanel();
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            _side.Children.Add(_title);
            _side.Children.Add(_detail);
            _side.Children.Add(Section("Check"));
            var find = _checkPanel = new WrapPanel();
            find.Children.Add(Btn("Zoom to", "Zoom to ours, the consultant element and where the mapping puts ours (this view if it shows them, else a review view, else your federator 3D view)", () => Find(false, false, false)));
            find.Children.Add(Btn("Show in 3D", "Your federator 3D view with a section box around them", () => Find(true, false, false)));
            find.Children.Add(Btn("Select ours", "Select our family in Revit", () => Find(false, true, false)));
            find.Children.Add(Btn("Select consultant", "Select the consultant element in Revit", () => Find(false, true, true)));
            _side.Children.Add(find);
            _side.Children.Add(Section("Decide"));
            var decide = _decidePanel = new WrapPanel();
            decide.Children.Add(Btn("Approve", "Take the consultant's state for this change", () => DecideCurrent(true)));
            decide.Children.Add(Btn("Reject", "Keep ours for this change", () => DecideCurrent(false)));
            _side.Children.Add(decide);

            _notePanel = new StackPanel();
            _notePanel.Children.Add(Section("Comments and assignment"));
            _thread = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            _notePanel.Children.Add(_thread);
            _commentBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _notePanel.Children.Add(_commentBox);
            var noteRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            noteRow.Children.Add(Btn("Add comment", "Add the comment to the selected change(s) (also Perseus_LinkComment on ours where editable)", AddComment));
            _notePanel.Children.Add(noteRow);
            var assignRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            _assignBox = new ComboBox { IsEditable = true, Width = 150, Margin = new Thickness(0, 0, 6, 6), ToolTip = "A Revit username (people seen in this model are listed)" };
            assignRow.Children.Add(_assignBox);
            assignRow.Children.Add(Btn("Assign", "Assign the selected change(s) (also Perseus_LinkAssignee on ours where editable)", () => Assign(_assignBox.Text?.Trim())));
            assignRow.Children.Add(Btn("Unassign", "Clear the assignment of the selected change(s)", () => Assign("")));
            _notePanel.Children.Add(assignRow);
            _side.Children.Add(_notePanel);
            var sideScroll = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(sideScroll, 2);
            body.Children.Add(sideScroll);
            root.Children.Add(body);
            Content = root;
            _side.IsEnabled = false;

            _grid.SelectionChanged += (s, e) => Safe(ShowCurrent);
            _grid.MouseDoubleClick += (s, e) => Safe(() => Find(false, false, false));
            _linkPick.Changed += () => Safe(Refresh);
            _kindPick.Changed += () => Safe(Fill);
            _assignFilter.SelectionChanged += (s, e) => { if (!_peopleLoading) Safe(Fill); };
            _inView.Checked += (s, e) => Safe(Refresh);
            _inView.Unchecked += (s, e) => Safe(Refresh);
            _viewTimer.Tick += (s, e) => Safe(() => { _viewTimer.Stop(); Refresh(); });
            _changeTimer.Tick += (s, e) => Safe(() => { _changeTimer.Stop(); Refresh(); });
            Api(app => { app.ViewActivated += OnViewActivated; app.Application.DocumentChanged += OnDocumentChanged; });
            Closed += (s, e) =>
            {
                _viewTimer.Stop();
                _changeTimer.Stop();
                if (Instance == this) Instance = null;
                OwnershipUiEvent.Run(a => { try { a.ViewActivated -= OnViewActivated; a.Application.DocumentChanged -= OnDocumentChanged; } catch { } });
            };
            Refresh();
        }

        private void Col(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            });
        }

        /// <summary>
        /// Someone moved / retyped / deleted one of our tracked families (or a link was reloaded): re-read the list shortly
        /// after. Our own Link Sync transactions re-read anyway. Revit's DocumentChanged: no model work here.
        /// </summary>
        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.Equals(_doc)) return;
                var names = e.GetTransactionNames();
                if (names.Any(n => n.StartsWith("Perseus", StringComparison.Ordinal))) return;
                var watched = _watched;
                bool ours = e.GetModifiedElementIds().Concat(e.GetDeletedElementIds()).Any(id => watched.Contains(id.GetIdValue()));
                bool linkReload = names.Any(n => n.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Link", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!ours && !linkReload) return;
                Dispatcher.BeginInvoke(new Action(() => { _changeTimer.Stop(); _changeTimer.Start(); }));
            }
            catch (Exception ex) { Log.Info($"[LinkSync] watching changes: {ex.Message}"); }
        }

        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            if (_inView.IsChecked != true) return;
            Dispatcher.BeginInvoke(new Action(() => { _viewTimer.Stop(); _viewTimer.Start(); }));
        }

        // ═══════════════ Reading ═══════════════

        private void Refresh()
        {
            bool inView = _inView.IsChecked == true;
            Status("Reading the links and our families…");
            Api(app =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _links = LinkSync.Links(_doc);
                var mapped = _links.Where(l => l.Mapping.Rules.Count > 0).ToList();
                if (!_linksLoaded)
                {
                    _linkPick.SetItems(mapped.Select(l => new MultiPick.Item(l.Uid, l.Name)));
                    _linksLoaded = true;
                }
                if (mapped.Count == 0) { _items = new List<SyncItem>(); Fill(); Status("No link has a family mapping yet: map families in Federator › Family Mapping first.", true); return; }
                var events = ReviewStore.Load(_doc);
                var records = MappedFamilyStore.All(_doc);
                var items = new List<SyncItem>();
                foreach (var l in mapped.Where(l => _linkPick.IsTicked(l.Uid)))
                    items.AddRange(LinkSync.Collect(_doc, l, events, records));
                _watched = new HashSet<long>(records.Select(r => r.Element.GetIdValue()));
                _log = LinkSyncLog.Load(_doc);
                _logIndex = LinkSyncNotes.Index(_log);
                string scope = "";
                if (inView)
                {
                    var view = app.ActiveUIDocument?.Document?.Equals(_doc) == true ? app.ActiveUIDocument.ActiveView : null;
                    if (view == null) { Status($"Switch to {_doc.Title} to filter by the active view.", true); return; }
                    var ours = new HashSet<long>();
                    try { foreach (var id in new ARDB.FilteredElementCollector(_doc, view.Id).OfClass(typeof(ARDB.FamilyInstance)).ToElementIds()) ours.Add(id.GetIdValue()); }
                    catch { /* views without model elements */ }
                    var theirs = new Dictionary<string, HashSet<long>>();
                    foreach (var l in mapped) theirs[l.Uid] = ReviewNavigator.VisibleLinkedIds(_doc, view, l.Instance) ?? new HashSet<long>();
                    items = items.Where(i => (i.OursId != 0 && ours.Contains(i.OursId)) ||
                                             (theirs.TryGetValue(i.LinkUid, out var t) && (t.Contains(i.SourceId) || (i.NewSourceId != 0 && t.Contains(i.NewSourceId))))).ToList();
                    scope = $" in '{view.Name}'";
                }
                _items = items;
                RefreshPeople();
                Fill();
                int decide = _items.Count(i => i.NeedsDecision);
                Status($"{decide:N0} change(s) to decide{scope} · {_items.Count(i => i.Kind == SyncKind.KeptByHand || i.Kind == SyncKind.KeptRejected):N0} kept · " +
                       $"{_items.Count(i => i.Kind == SyncKind.InSync):N0} in sync ({clock.ElapsedMilliseconds:N0} ms).");
            });
        }

        private void Fill()
        {
            var names = _links.ToDictionary(l => l.Uid, l => l.Name);
            var keep = new HashSet<string>(_grid.SelectedItems.Cast<Row>().Select(r => r.Item.Key));
            string who = _assignFilter.SelectedItem as string ?? AnyoneFilter;
            var rows = _items.Where(i => _kindPick.IsTicked(i.Kind.ToString()))
                .OrderBy(i => Array.IndexOf(Enum.GetValues(typeof(SyncKind)), i.Kind)).ThenBy(i => i.Category).ThenBy(i => i.SourceTypeKey)
                .Select(i => new { i, notes = LinkSyncNotes.For(i, _logIndex) })
                .Where(x => who == AnyoneFilter || (who == MeFilter ? string.Equals(x.notes.Assignee, _me, StringComparison.OrdinalIgnoreCase)
                                                 : who == UnassignedFilter ? string.IsNullOrEmpty(x.notes.Assignee)
                                                 : string.Equals(x.notes.Assignee, who, StringComparison.OrdinalIgnoreCase)))
                .Select(x => (i: x.i, notes: x.notes))
                .Select(t => new Row
                {
                    Notes = t.notes,
                    Decision = t.notes.LastDecision == null ? (t.i.NeedsDecision ? "Pending" : "") : LinkSyncNotes.Describe(t.notes.LastDecision, false),
                    EditedBy = t.i.OursEditedBy ?? "",
                    Assignee = t.notes.Assignee ?? "",
                    Comments = t.notes.Comments.Count > 0 ? t.notes.Comments.Count.ToString() : "",
                    Item = t.i, Change = LinkSyncPlanner.Label(t.i.Kind), Link = names.TryGetValue(t.i.LinkUid ?? "", out var n) ? n : "?",
                    Category = t.i.Category ?? "",
                    Consultant = $"{LinkSyncPlanner.Pretty(t.i.SourceTypeKey)} · {(t.i.Kind == SyncKind.Replaced ? $"{t.i.SourceId} → {t.i.NewSourceId}" : t.i.SourceId.ToString())}",
                    Ours = t.i.OursId == 0 ? (t.i.TargetName != null ? "(none) → " + t.i.TargetName : "(none)") : $"{t.i.OursType} · {t.i.OursId}",
                    Detail = t.i.Detail,
                }).ToList();
            _grid.ItemsSource = rows;
            foreach (var r in rows.Where(r => keep.Contains(r.Item.Key))) _grid.SelectedItems.Add(r);
            var byKind = _items.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
            _summary.Text = $"{rows.Count:N0} shown · " + string.Join(" · ", DecisionKinds.Where(byKind.ContainsKey).Select(k => $"{byKind[k]:N0} {LinkSyncPlanner.Label(k).ToLowerInvariant()}"));
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            var sel = _grid.SelectedItems.Cast<Row>().ToList();
            _side.IsEnabled = sel.Count > 0;
            _checkPanel.IsEnabled = _decidePanel.IsEnabled = sel.Count == 1;
            _thread.Children.Clear();
            if (sel.Count != 1)
            {
                _title.Text = sel.Count == 0 ? "" : $"{sel.Count} changes selected";
                _detail.Text = sel.Count == 0 ? "Select a change to see it."
                    : "Approve / reject them with the buttons below the list. A comment or assignment below goes on all of them.";
                return;
            }
            var row = sel[0];
            var i = row.Item;
            var notes = row.Notes ?? new LinkSyncNotes();
            _title.Text = $"{LinkSyncPlanner.Label(i.Kind)} · {LinkSyncPlanner.Pretty(i.SourceTypeKey)}";
            var lines = new List<string> { i.Detail, "" };
            lines.Add($"Consultant element {i.SourceId}" + (i.NewSourceId != 0 ? $" → {i.NewSourceId}" : "") +
                      (string.IsNullOrEmpty(i.SourceEditedBy) ? "" : $" · last edited by {i.SourceEditedBy} (in the link)"));
            if (i.OursId != 0)
                lines.Add($"Ours {i.OursId} ({i.OursType})" + (string.IsNullOrEmpty(i.OursEditedBy) ? "" : $" · last edited by {i.OursEditedBy} (in this model)"));
            lines.Add("");
            if (notes.Decisions.Count == 0) lines.Add(i.NeedsDecision ? "Not decided yet." : "No decisions recorded.");
            else foreach (var d in notes.Decisions.Take(6)) lines.Add(LinkSyncNotes.Describe(d));
            if (!string.IsNullOrEmpty(notes.Assignee)) lines.Add($"Assigned to {notes.Assignee}");
            _detail.Text = string.Join("\n", lines);
            foreach (var c in notes.Comments)
            {
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                tb.Inlines.Add(new System.Windows.Documents.Run($"{c.Author} · {c.Utc.ToLocalTime():d MMM HH:mm}  ") { Foreground = Muted, FontSize = 11 });
                tb.Inlines.Add(new System.Windows.Documents.Run(c.Text ?? ""));
                _thread.Children.Add(tb);
            }
            if (notes.Comments.Count == 0) _thread.Children.Add(new TextBlock { Text = "No comments yet.", Foreground = Muted });
        }

        /// <summary>People to assign to: this user, people who edited / decided / commented / were assigned in this model.</summary>
        private void RefreshPeople()
        {
            var people = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { _me };
            foreach (var e in _log) { if (!string.IsNullOrEmpty(e.Author)) people.Add(e.Author); if (!string.IsNullOrEmpty(e.Assignee)) people.Add(e.Assignee); }
            foreach (var i in _items) if (!string.IsNullOrEmpty(i.OursEditedBy)) people.Add(i.OursEditedBy);
            string typed = _assignBox.Text;
            _assignBox.Items.Clear();
            foreach (var n in people) _assignBox.Items.Add(n);
            _assignBox.Text = typed;
            var filter = _assignFilter.SelectedItem as string;
            _peopleLoading = true;
            while (_assignFilter.Items.Count > 3) _assignFilter.Items.RemoveAt(3);
            foreach (var n in _log.Where(e => e.Type == LinkSyncEvent.Assign && !string.IsNullOrEmpty(e.Assignee)).Select(e => e.Assignee)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                _assignFilter.Items.Add(n);
            _assignFilter.SelectedItem = filter != null && _assignFilter.Items.Contains(filter) ? filter : AnyoneFilter;
            _peopleLoading = false;
        }

        private bool _peopleLoading;

        // ═══════════════ Comments + assignment ═══════════════

        private void AddComment()
        {
            string text = _commentBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) { Status("Type a comment first."); return; }
            var items = _grid.SelectedItems.Cast<Row>().Select(r => r.Item).ToList();
            if (items.Count == 0) { Status("Select a change first."); return; }
            Note(items, i => new LinkSyncEvent { Type = LinkSyncEvent.Comment, Text = text }, $"Comment added to {items.Count:N0} change(s)", () => _commentBox.Text = "");
        }

        private void Assign(string who)
        {
            var items = _grid.SelectedItems.Cast<Row>().Select(r => r.Item).ToList();
            if (items.Count == 0) { Status("Select a change first."); return; }
            if (who == null) return;
            Note(items, i => new LinkSyncEvent { Type = LinkSyncEvent.Assign, Assignee = who },
                 string.IsNullOrEmpty(who) ? $"Unassigned {items.Count:N0} change(s)" : $"Assigned {items.Count:N0} change(s) to {who}", null);
        }

        /// <summary>Saves one event per selected item (append-only record, no ownership needed), then the comment / assignee parameters on ours where editable.</summary>
        private void Note(List<SyncItem> items, Func<SyncItem, LinkSyncEvent> make, string done, Action after)
        {
            var events = items.Select(i =>
            {
                var e = make(i);
                e.LinkUid = i.LinkUid; e.Key = LinkSyncNotes.KeyFor(i); e.Author = _me; e.Utc = DateTime.UtcNow;
                return e;
            }).ToList();
            Api(app =>
            {
                LinkSyncLog.Add(_doc, events);
                _log.AddRange(events);
                _logIndex = LinkSyncNotes.Index(_log);
                Tuple<int, int> p = Tuple.Create(0, 0);
                try { p = LinkSync.WriteNotes(_doc, items, _logIndex); }
                catch (Exception ex) { Log.Info($"[LinkSync] comment / assignee parameters: {ex.Message}"); }
                after?.Invoke();
                RefreshPeople();
                Fill();
                Status(done + "." + (p.Item2 > 0 ? $" {p.Item2:N0} of ours are checked out by others: their parameters weren't updated (the note is saved)." : ""));
            });
        }

        // ═══════════════ Deciding ═══════════════

        private void DecideCurrent(bool approve)
        {
            var row = _grid.SelectedItems.Cast<Row>().FirstOrDefault();
            if (row != null) Decide(new List<SyncItem> { row.Item }, approve);
        }

        private void DecideSelected(bool approve) => Decide(_grid.SelectedItems.Cast<Row>().Select(r => r.Item).ToList(), approve);

        private void ApproveAllShown()
        {
            var shown = ((_grid.ItemsSource as IEnumerable<Row>) ?? Enumerable.Empty<Row>()).Select(r => r.Item).Where(i => i.NeedsDecision).ToList();
            if (shown.Count == 0) { Status("Nothing listed needs a decision."); return; }
            Decide(shown, true);
        }

        private void Decide(List<SyncItem> items, bool approve)
        {
            // Approve: changes needing a decision + "kept" ones (= move back to the consultant position). Reject: decisions only.
            var todo = items.Where(i => approve ? (i.NeedsDecision || i.Kind == SyncKind.KeptByHand || i.Kind == SyncKind.KeptRejected) : i.NeedsDecision).ToList();
            if (todo.Count == 0) { Status(approve ? "Nothing selected to approve." : "Nothing selected to reject (kept / in-sync rows have nothing to reject)."); return; }
            int deletes = approve ? todo.Count(i => i.Kind == SyncKind.Orphaned) : 0;
            int unmapped = approve ? todo.Count(i => i.Kind == SyncKind.ConsultantType && i.TargetUid == null) : 0;
            string msg = $"{(approve ? "Approve" : "Reject")} {todo.Count:N0} change(s)?" +
                         (deletes > 0 ? $"\n\n{deletes:N0} of them DELETE our family (its consultant element is gone)." : "") +
                         (unmapped > 0 ? $"\n\n{unmapped:N0} consultant type change(s) can't be approved until the new type is mapped." : "") +
                         "\n\nOne undo step.";
            if (todo.Count > 1 || deletes > 0)
                if (MessageBox.Show(this, msg, "Link Sync", MessageBoxButton.OKCancel, deletes > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var decisions = todo.Select(i => new LinkSync.Decision { Item = i, Approve = approve }).ToList();
            Api(app =>
            {
                var links = LinkSync.Links(_doc).ToDictionary(l => l.Uid);
                LinkSync.ApplyResult r;
                try { r = LinkSync.Apply(_doc, decisions, links, _me, (t, f) => SyncStatusToast.Progress("Link Sync", f, t)); }
                finally { SyncStatusToast.Hide(); }
                string text = $"{(approve ? "Approved" : "Rejected")} {(approve ? r.Approved : r.Rejected):N0}." +
                              (r.Replaced > 0 ? $" {r.Replaced:N0} re-placed (couldn't move on their work plane)." : "") +
                              (r.Skipped > 0 ? $" {r.Skipped:N0} checked out by others." : "") +
                              (r.Failed > 0 ? $" {r.Failed:N0} failed: {string.Join("; ", r.Warnings.Take(3))}" : "");
                Refresh();
                Status(text, r.Failed > 0);
            }, needActive: true);
        }

        private void WriteStatus()
        {
            var items = _items.ToList();
            Api(app =>
            {
                var links = LinkSync.Links(_doc).ToDictionary(l => l.Uid);
                var r = LinkSync.WriteStatus(_doc, items, links, _logIndex);
                try { LinkSync.WriteNotes(_doc, items, _logIndex); } catch (Exception ex) { Log.Info($"[LinkSync] notes parameters: {ex.Message}"); }
                Status($"Status parameters written on {r.Item1:N0} of ours" + (r.Item2 > 0 ? $"; {r.Item2:N0} checked out by others." : ".") +
                       (_inView.IsChecked == true ? " (only those in the active view)" : ""));
            });
        }

        private void OpenReviewViews() => Api(app => ReviewViewsWindow.EditFor(app, new System.Windows.Interop.WindowInteropHelper(this).Handle));

        // ═══════════════ Checking ═══════════════

        private void Find(bool force3D, bool selectOnly, bool selectConsultant)
        {
            var row = _grid.SelectedItems.Cast<Row>().FirstOrDefault();
            if (row == null) { Status("Select a change first."); return; }
            var it = row.Item;
            Api(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var link = LinkSync.Links(_doc).FirstOrDefault(l => l.Uid == it.LinkUid);
                var inst = link?.Instance;
                var linkDoc = inst?.GetLinkDocument();
                var ours = it.OursId != 0 ? _doc.GetElement(RevitExtensions.CreateId(it.OursId)) : null;
                var source = linkDoc?.GetElement(it.NewSourceUid ?? it.SourceUid);
                try
                {
                    if (selectConsultant)
                    {
                        if (source == null) { Status("The consultant element is gone.", true); return; }
                        uidoc.Selection.SetReferences(new List<ARDB.Reference> { new ARDB.Reference(source).CreateLinkReference(inst) });
                    }
                    else if (ours != null) uidoc.Selection.SetElementIds(new List<ARDB.ElementId> { ours.Id });
                    else if (selectOnly) { Status("There's none of ours for this change yet."); return; }
                }
                catch (Exception ex) { Log.Info($"[LinkSync] select: {ex.Message}"); }
                if (selectOnly) { Status(selectConsultant ? "Consultant element selected." : "Ours selected."); return; }
                var pts = new List<ARDB.XYZ>();
                var a = ours?.get_BoundingBox(null);
                if (a != null) { pts.Add(a.Min); pts.Add(a.Max); }
                var b = source?.get_BoundingBox(null);
                if (b != null && inst != null)
                {
                    var t = inst.GetTotalTransform();
                    for (int k = 0; k < 8; k++)
                        pts.Add(t.OfPoint(new ARDB.XYZ((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z)));
                }
                if (it.Expected != null) pts.Add(new ARDB.XYZ(it.Expected.O[0], it.Expected.O[1], it.Expected.O[2]));
                if (pts.Count == 0) { Status("Nothing to zoom to.", true); return; }
                double pad = 300 / MappingMath.MmPerFoot;
                var min = new ARDB.XYZ(pts.Min(p => p.X) - pad, pts.Min(p => p.Y) - pad, pts.Min(p => p.Z) - pad);
                var max = new ARDB.XYZ(pts.Max(p => p.X) + pad, pts.Max(p => p.Y) + pad, pts.Max(p => p.Z) + pad);
                ReviewViewSettings views = null;
                try { views = ReviewViews.Load(_doc); } catch { }
                string where = ReviewNavigator.ShowBox(uidoc, inst, min, max, views, force3D);
                Status($"Showing it {where}.");
            }, needActive: true);
        }

        // ═══════════════ Helpers ═══════════════

        private void Api(Action<UIApplication> work, bool needActive = false)
        {
            OwnershipUiEvent.Run(app =>
            {
                try
                {
                    if (_doc == null || !_doc.IsValidObject) { Status("The model was closed.", true); return; }
                    if (needActive && app.ActiveUIDocument?.Document?.Equals(_doc) != true) { Status($"Switch to {_doc.Title} first.", true); return; }
                    work(app);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[LinkSync] {ex}");
                    Status("Failed: " + ex.Message, true);
                }
            });
        }

        private void Status(string text, bool error = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? Bad : Ink;
        }

        private void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Warn($"[LinkSync] UI: {ex}"); Status("Something went wrong: " + ex.Message, true); }
        }

        private static TextBlock Label(string text) => new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 12, 0, 4) };

        private Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), Background = Brushes.White };
            b.Click += (s, e) => Safe(click);
            return b;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
