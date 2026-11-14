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
        private readonly DataGrid _grid;          // open changes
        private readonly DataGrid _aside;         // rejected changes, set aside (the modeller stands by the local position)
        private readonly TextBlock _asideHeader;
        private bool _selecting;
        private readonly MultiPick _linkPick, _kindPick;
        private readonly CheckBox _inView, _showInModel;
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
            { SyncKind.New, SyncKind.Match, SyncKind.Moved, SyncKind.ConsultantType, SyncKind.MappingType, SyncKind.Replaced, SyncKind.Orphaned,
              SyncKind.Unlinked, SyncKind.OursDeleted };

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
            w.StartBadges(app.MainWindowHandle);
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
                       "the consultant's state, reject it to keep the local family. Families moved by hand (beyond the mapping's tolerance) are listed as kept: approve to put them " +
                       "back. Orphans: local families with no consultant element (the consultant deleted it, or an existing local family with nothing near it) and " +
                       "consultant elements whose local family was deleted here. The list updates when local families change. Use 'Only in the active view' to work your own zone.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _linkPick = new MultiPick(220) { ToolTip = "Mapped links to check" };
            _kindPick = new MultiPick(200) { ToolTip = "Which changes to list" };
            _kindPick.SetItems(Enum.GetValues(typeof(SyncKind)).Cast<SyncKind>().Where(k => k != SyncKind.KeptRejected).Select(k => new MultiPick.Item(k.ToString(), LinkSyncPlanner.Label(k))));
            foreach (SyncKind k in Enum.GetValues(typeof(SyncKind))) _kindPick.SetTicked(k.ToString(), LinkSyncPlanner.NeedsDecision(k) || k == SyncKind.KeptByHand);
            _kindPick.AddPreset("Changes", "Everything that differs from the consultant: decisions + edited by hand",
                                DecisionKinds.Select(k => k.ToString()).Concat(new[] { SyncKind.KeptByHand.ToString() }).ToArray());
            _kindPick.AddPreset("Needs a decision", "New, match, moved, type changes, replaced, orphans", DecisionKinds.Select(k => k.ToString()).ToArray());
            _kindPick.AddPreset("Orphans", "Local families without a consultant element + consultant elements whose local family was deleted",
                                SyncKind.Orphaned.ToString(), SyncKind.Unlinked.ToString(), SyncKind.OursDeleted.ToString());
            _kindPick.AddPreset("Kept", "Moved by hand", SyncKind.KeptByHand.ToString());
            _inView = new CheckBox { Content = "Only in the active view", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                     ToolTip = "Only local families or consultant elements visible in the active view (follows view switches)" };
            tools.Children.Add(Label("Links "));
            tools.Children.Add(_linkPick);
            tools.Children.Add(Label("Show "));
            tools.Children.Add(_kindPick);
            tools.Children.Add(_inView);
            _assignFilter = new ComboBox { Width = 130, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Only changes assigned to someone" };
            _assignFilter.Items.Add(AnyoneFilter); _assignFilter.Items.Add(MeFilter); _assignFilter.Items.Add(UnassignedFilter);
            _assignFilter.SelectedIndex = 0;
            tools.Children.Add(_assignFilter);
            _showInModel = new CheckBox { Content = "Show in model", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                          ToolTip = "Draw the listed changes in plans, sections and 3D: arrows from the local family to where the mapping puts it, markers where new families will appear (selected rows stand out)" };
            tools.Children.Add(_showInModel);
            tools.Children.Add(Btn("Refresh", "Read the links and the local families again", Refresh));
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            tools.Children.Add(_summary);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var actions = new WrapPanel();
            actions.Children.Add(Btn2("Approve selected", "Accept linked model position", "Take the consultant's state: move the local family to the linked model position, place new ones, change the type, follow the replacement, delete orphans - for the selected changes (one undo step)", () => DecideSelected(true)));
            actions.Children.Add(Btn2("Reject selected", "Retain current model position", "Keep the local family as it is in this model (not proposed again until the linked model changes once more) - for the selected changes", () => DecideSelected(false)));
            actions.Children.Add(Btn2("Approve all shown…", "Accept linked model position", "Approve every change listed that needs a decision", ApproveAllShown));
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
            _grid = MakeGrid(false);
            _aside = MakeGrid(true);
            // Open changes on top, rejected ones set aside below (drag the splitter).
            var lists = new Grid();
            lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2.2, GridUnitType.Star), MinHeight = 120 });
            lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 60 });
            Grid.SetRow(_grid, 0);
            lists.Children.Add(_grid);
            var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                                              Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            Grid.SetRow(splitter, 1);
            lists.Children.Add(splitter);
            _asideHeader = new TextBlock { Foreground = Muted, Margin = new Thickness(0, 2, 0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_asideHeader, 2);
            lists.Children.Add(_asideHeader);
            Grid.SetRow(_aside, 3);
            lists.Children.Add(_aside);
            Grid.SetColumn(lists, 0);
            body.Children.Add(lists);

            _side = new StackPanel();
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            _side.Children.Add(_title);
            _side.Children.Add(_detail);
            _side.Children.Add(Section("Check"));
            var find = _checkPanel = new WrapPanel();
            find.Children.Add(Btn("Zoom to", "Zoom to the local family, the consultant element and where the mapping puts it (this view if it shows them, else a review view, else your federator 3D view)", () => Find(false, false, false)));
            find.Children.Add(Btn("Show in 3D", "Your federator 3D view with a section box around them", () => Find(true, false, false)));
            find.Children.Add(Btn("Select local", "Select the local family in Revit", () => Find(false, true, false)));
            find.Children.Add(Btn("Select consultant", "Select the consultant element in Revit", () => Find(false, true, true)));
            _side.Children.Add(find);
            _side.Children.Add(Section("Decide"));
            var decide = _decidePanel = new WrapPanel();
            decide.Children.Add(Btn2("Approve", "Accept linked model position", "Take the consultant's state: move the local family to the linked model position, place new ones, change the type, follow the replacement, delete orphans", () => DecideCurrent(true)));
            decide.Children.Add(Btn2("Reject", "Retain current model position", "Keep the local family as it is in this model: the change is set aside (second list) until the linked model changes once more", () => DecideCurrent(false)));
            decide.Children.Add(_reconsiderBtn = Btn2("Reconsider", "Back for a decision", "Forget the rejection: the change goes back to the list of changes to decide", ReconsiderSelected));
            _side.Children.Add(decide);

            _notePanel = new StackPanel();
            _notePanel.Children.Add(Section("Comments and assignment"));
            _thread = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            _notePanel.Children.Add(_thread);
            _commentBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 48, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _notePanel.Children.Add(_commentBox);
            var noteRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            noteRow.Children.Add(Btn("Add comment", "Add the comment to the selected change(s) (also Perseus_LinkComment on the local family where editable)", AddComment));
            _notePanel.Children.Add(noteRow);
            var assignRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            _assignBox = new ComboBox { IsEditable = true, Width = 150, Margin = new Thickness(0, 0, 6, 6), ToolTip = "A Revit username (people seen in this model are listed)" };
            assignRow.Children.Add(_assignBox);
            assignRow.Children.Add(Btn("Assign", "Assign the selected change(s) (also Perseus_LinkAssignee on the local family where editable)", () => Assign(_assignBox.Text?.Trim())));
            assignRow.Children.Add(Btn("Unassign", "Clear the assignment of the selected change(s)", () => Assign("")));
            _notePanel.Children.Add(assignRow);
            _side.Children.Add(_notePanel);
            var sideScroll = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(sideScroll, 2);
            body.Children.Add(sideScroll);
            root.Children.Add(body);
            Content = root;
            _side.IsEnabled = false;

            foreach (var g in new[] { _grid, _aside })
            {
                var grid = g;
                grid.SelectionChanged += (s, e) => Safe(() =>
                {
                    if (_selecting) return;
                    // One list at a time: picking rows in one clears the other.
                    var other = grid == _grid ? _aside : _grid;
                    if (grid.SelectedItems.Count > 0 && other.SelectedItems.Count > 0) { _selecting = true; try { other.SelectedItems.Clear(); } finally { _selecting = false; } }
                    ShowCurrent(); PushMarkers();
                    if (!_fromRevit) { _selectTimer.Stop(); _selectTimer.Start(); }
                });
                grid.MouseDoubleClick += (s, e) => Safe(() => Find(false, false, false));
                grid.ContextMenu = new ContextMenu();
                grid.ContextMenuOpening += (s, e) => Safe(() =>
                {
                    var rows = SelectedRows().ToList();
                    if (rows.Count == 0) { e.Handled = true; return; }
                    FillMenu(grid.ContextMenu, rows);
                });
            }
            _selectTimer.Tick += (s, e) => Safe(() => { _selectTimer.Stop(); PushRevitSelection(); });
            _showInModel.Checked += (s, e) => Safe(PushMarkers);
            _showInModel.Unchecked += (s, e) => Safe(PushMarkers);
            _linkPick.Changed += () => Safe(Refresh);
            _kindPick.Changed += () => Safe(Fill);
            _assignFilter.SelectionChanged += (s, e) => { if (!_peopleLoading) Safe(Fill); };
            _inView.Checked += (s, e) => Safe(Refresh);
            _inView.Unchecked += (s, e) => Safe(Refresh);
            _viewTimer.Tick += (s, e) => Safe(() => { _viewTimer.Stop(); Refresh(); });
            _changeTimer.Tick += (s, e) => Safe(() => { _changeTimer.Stop(); Refresh(); });
            Api(app => { app.ViewActivated += OnViewActivated; app.SelectionChanged += OnRevitSelectionChanged; app.Application.DocumentChanged += OnDocumentChanged; });
            Closed += (s, e) =>
            {
                _viewTimer.Stop();
                _changeTimer.Stop();
                StopBadges();
                if (Instance == this) Instance = null;
                ChangeHighlightController.ShowLayer(ChangeHighlightController.LinkSyncLayer, null, null);
                OwnershipUiEvent.Run(a => { try { a.ViewActivated -= OnViewActivated; a.SelectionChanged -= OnRevitSelectionChanged; a.Application.DocumentChanged -= OnDocumentChanged; a.ActiveUIDocument?.RefreshActiveView(); } catch { } });
            };
            Refresh();
        }

        private Button _reconsiderBtn;

        /// <summary>A change list; the set-aside one (rejected) in a light weight and muted.</summary>
        private DataGrid MakeGrid(bool aside)
        {
            var g = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush, Background = Brushes.White,
                RowHeaderWidth = 0, CanUserAddRows = false,
            };
            if (aside) { g.FontWeight = FontWeights.Light; g.Foreground = Muted; }
            void Col(string header, string path, double width) => g.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new System.Windows.Data.Binding(path),
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            });
            Col("Change", nameof(Row.Change), 150);
            Col("Link", nameof(Row.Link), 110);
            Col("Category", nameof(Row.Category), 110);
            Col("Consultant element", nameof(Row.Consultant), 0);
            Col("Local family", nameof(Row.Ours), 0);
            Col(aside ? "Rejected" : "Decision", nameof(Row.Decision), 150);
            Col("Edited by", nameof(Row.EditedBy), 95);
            Col("Assignee", nameof(Row.Assignee), 95);
            Col("💬", nameof(Row.Comments), 32);
            return g;
        }

        private IEnumerable<Row> AllRows() =>
            ((_grid.ItemsSource as IEnumerable<Row>) ?? Enumerable.Empty<Row>()).Concat((_aside.ItemsSource as IEnumerable<Row>) ?? Enumerable.Empty<Row>());

        private IEnumerable<Row> SelectedRows() => _grid.SelectedItems.Cast<Row>().Concat(_aside.SelectedItems.Cast<Row>());

        private DataGrid GridOf(Row r) => r.Item.Rejected ? _aside : _grid;

        /// <summary>Selects exactly these rows (in whichever list they are).</summary>
        private void SetSelection(IList<Row> rows)
        {
            _selecting = true;
            try
            {
                _grid.SelectedItems.Clear();
                _aside.SelectedItems.Clear();
                foreach (var r in rows) GridOf(r).SelectedItems.Add(r);
            }
            finally { _selecting = false; }
            if (rows.Count > 0) GridOf(rows[0]).ScrollIntoView(rows[0]);
            ShowCurrent(); PushMarkers();
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
                // Undo / redo (of anything, our own Link Sync steps included: their names start with "Perseus") = re-read.
                if (e.Operation == ARDB.Events.UndoOperation.TransactionUndone || e.Operation == ARDB.Events.UndoOperation.TransactionRedone)
                {
                    Dispatcher.BeginInvoke(new Action(() => { _changeTimer.Stop(); _changeTimer.Start(); }));
                    return;
                }
                if (names.Any(n => n.StartsWith("Perseus", StringComparison.Ordinal))) return;
                var watched = _watched;
                bool ours = e.GetModifiedElementIds().Concat(e.GetDeletedElementIds()).Any(id => watched.Contains(id.GetIdValue()));
                // A copy of one of ours carries its record (read-only check, first few added only).
                if (!ours) ours = e.GetAddedElementIds().Take(200).Any(id => doc.GetElement(id) is ARDB.FamilyInstance fi && MappedFamilyStore.Get(fi) != null);
                bool linkReload = names.Any(n => n.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Link", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!ours && !linkReload) return;
                Dispatcher.BeginInvoke(new Action(() => { _changeTimer.Stop(); _changeTimer.Start(); }));
            }
            catch (Exception ex) { Log.Info($"[LinkSync] watching changes: {ex.Message}"); }
        }

        /// <summary>
        /// Revit selection -> rows: picking a local family or consultant element of a listed change selects its row(s); clicking
        /// empty space or anything else clears the row selection (and so the emphasised markers).
        /// </summary>
        private void OnRevitSelectionChanged(object sender, Autodesk.Revit.UI.Events.SelectionChangedEventArgs e)
        {
            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.Equals(_doc)) return;
                var host = new HashSet<long>();
                var linked = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in e.GetReferences() ?? new List<ARDB.Reference>())
                {
                    if (r == null) continue;
                    if (r.LinkedElementId != ARDB.ElementId.InvalidElementId) linked.Add(doc.GetElement(r.ElementId)?.UniqueId + "|" + r.LinkedElementId.GetIdValue());
                    else host.Add(r.ElementId.GetIdValue());
                }
                Dispatcher.BeginInvoke(new Action(() => Safe(() => SyncFromRevit(host, linked))));
            }
            catch (Exception ex) { Log.Info($"[LinkSync] selection changed: {ex.Message}"); }
        }

        private bool _fromRevit;
        private readonly DispatcherTimer _selectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private const int MaxAutoSelect = 500;

        /// <summary>
        /// Rows picked in the list are selected in Revit too (local families; the consultant element when there's none), so
        /// clicking elsewhere in Revit is a selection change that clears them again.
        /// </summary>
        private void PushRevitSelection()
        {
            var items = SelectedRows().Select(r => r.Item).ToList();
            if (items.Count == 0 || items.Count > MaxAutoSelect) return;
            Api(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc?.Document == null || !uidoc.Document.Equals(_doc)) return;
                var links = new Dictionary<string, ARDB.RevitLinkInstance>();
                var refs = new List<ARDB.Reference>();
                foreach (var i in items)
                {
                    var ours = i.OursId != 0 ? _doc.GetElement(RevitExtensions.CreateId(i.OursId)) : null;
                    if (ours != null) { refs.Add(new ARDB.Reference(ours)); continue; }
                    if (!links.TryGetValue(i.LinkUid ?? "", out var inst)) links[i.LinkUid ?? ""] = inst = LinkedElementRef.FindInstance(_doc, i.LinkUid);
                    var src = inst?.GetLinkDocument()?.GetElement(i.NewSourceUid ?? i.SourceUid);
                    if (src != null) { try { refs.Add(new ARDB.Reference(src).CreateLinkReference(inst)); } catch { } }
                }
                try { uidoc.Selection.SetReferences(refs); } catch (Exception ex) { Log.Info($"[LinkSync] select in Revit: {ex.Message}"); }
            });
        }

        private void SyncFromRevit(HashSet<long> host, HashSet<string> linked)
        {
            var all = AllRows();
            var rows = all.Where(r => (r.Item.OursId != 0 && host.Contains(r.Item.OursId)) ||
                                      linked.Contains(r.Item.LinkUid + "|" + r.Item.SourceId) ||
                                      (r.Item.NewSourceId != 0 && linked.Contains(r.Item.LinkUid + "|" + r.Item.NewSourceId))).ToList();
            var now = SelectedRows().ToList();
            if (rows.Count == 0 && now.Count == 0) return;
            if (rows.Count > 0 && now.Count == rows.Count && now.All(rows.Contains)) return;
            _fromRevit = true;
            try { SetSelection(rows); }
            finally { _fromRevit = false; }
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
                _log = LinkSyncLog.Load(_doc);
                var copies = LinkSync.FindCopies(_doc, records, _log);
                var register = new List<LinkSyncEvent>();
                var items = new List<SyncItem>();
                foreach (var l in mapped.Where(l => _linkPick.IsTicked(l.Uid)))
                    items.AddRange(LinkSync.Collect(_doc, l, events, records, _log, register, copies));
                if (copies.Count > 0 && !_doc.IsReadOnly)
                {
                    // Copies of our families carry the original's record: they're not linked to its consultant element.
                    try { int n = LinkSync.StripCopies(_doc, copies); Log.Info($"[LinkSync] {n:N0} of {copies.Count:N0} copied famil(ies) of ours unlinked from the original's consultant element"); }
                    catch (Exception ex) { Log.Info($"[LinkSync] couldn't unlink copies: {ex.Message}"); }
                }
                items = LinkSyncPlanner.Reconcile(items);
                if (register.Count > 0 && !_doc.IsReadOnly)
                {
                    // Families of ours linked before the log kept links: remember them, so deleting one later is noticed.
                    try { LinkSyncLog.Add(_doc, register); _log.AddRange(register); Log.Info($"[LinkSync] registered {register.Count:N0} existing link(s) of ours"); }
                    catch (Exception ex) { Log.Info($"[LinkSync] couldn't register existing links: {ex.Message}"); }
                }
                _watched = new HashSet<long>(records.Select(r => r.Element.GetIdValue()).Concat(items.Where(i => i.OursId != 0).Select(i => i.OursId)));
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
                Status($"{decide:N0} change(s) to decide{scope} · {_items.Count(i => i.Rejected):N0} set aside (rejected) · {_items.Count(i => i.Kind == SyncKind.KeptByHand):N0} moved by hand · " +
                       $"{_items.Count(i => i.Kind == SyncKind.InSync):N0} in sync ({clock.ElapsedMilliseconds:N0} ms).");
            });
        }

        private void Fill()
        {
            var names = _links.ToDictionary(l => l.Uid, l => l.Name);
            var keep = new HashSet<string>(SelectedRows().Select(r => r.Item.Key));
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
                    Decision = t.i.Kind == SyncKind.KeptByHand ? "Rejected by hand edit" + (string.IsNullOrEmpty(t.i.OursEditedBy) ? "" : " · " + t.i.OursEditedBy)
                             : t.notes.LastDecision == null ? (t.i.NeedsDecision ? "Pending" : t.i.Rejected ? "Rejected" : "") : LinkSyncNotes.Describe(t.notes.LastDecision, false),
                    EditedBy = t.i.OursEditedBy ?? "",
                    Assignee = t.notes.Assignee ?? "",
                    Comments = t.notes.Comments.Count > 0 ? t.notes.Comments.Count.ToString() : "",
                    Item = t.i, Change = LinkSyncPlanner.Label(t.i.Kind), Link = names.TryGetValue(t.i.LinkUid ?? "", out var n) ? n : "?",
                    Category = t.i.Category ?? "",
                    Consultant = $"{LinkSyncPlanner.Pretty(t.i.SourceTypeKey)} · {(t.i.Kind == SyncKind.Replaced ? $"{t.i.SourceId} → {t.i.NewSourceId}" : t.i.SourceId.ToString())}",
                    Ours = t.i.OursId == 0 ? (t.i.TargetName != null ? "(none) → " + t.i.TargetName : "(none)") : $"{t.i.OursType} · {t.i.OursId}",
                    Detail = t.i.Detail,
                }).ToList();
            _selecting = true;
            try
            {
                _grid.ItemsSource = rows.Where(r => !r.Item.Rejected).ToList();
                _aside.ItemsSource = rows.Where(r => r.Item.Rejected).ToList();
                foreach (var r in rows.Where(r => keep.Contains(r.Item.Key))) GridOf(r).SelectedItems.Add(r);
            }
            finally { _selecting = false; }
            int asideCount = rows.Count(r => r.Item.Rejected);
            _asideHeader.Text = $"Set aside: {asideCount:N0} rejected change(s) — the modeller keeps the local position. They stay here until the linked model " +
                                "changes again. Approve takes the consultant's state after all; Reconsider puts a change back above.";
            var byKind = _items.Where(i => !i.Rejected).GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());
            _summary.Text = $"{rows.Count - asideCount:N0} shown · {asideCount:N0} set aside · " + string.Join(" · ", DecisionKinds.Where(byKind.ContainsKey).Select(k => $"{byKind[k]:N0} {LinkSyncPlanner.Label(k).ToLowerInvariant()}"));
            ShowCurrent();
            PushMarkers();
        }

        private void ShowCurrent()
        {
            var sel = SelectedRows().ToList();
            _side.IsEnabled = sel.Count > 0;
            _checkPanel.IsEnabled = _decidePanel.IsEnabled = sel.Count == 1;
            _reconsiderBtn.Visibility = sel.Any(r => r.Item.Rejected) ? Visibility.Visible : Visibility.Collapsed;
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
                lines.Add($"Local family {i.OursId} ({i.OursType})" + (string.IsNullOrEmpty(i.OursEditedBy) ? "" : $" · last edited by {i.OursEditedBy} (in this model)"));
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
            var items = SelectedRows().Select(r => r.Item).ToList();
            if (items.Count == 0) { Status("Select a change first."); return; }
            Note(items, i => new LinkSyncEvent { Type = LinkSyncEvent.Comment, Text = text }, $"Comment added to {items.Count:N0} change(s)", () => _commentBox.Text = "");
        }

        private void Assign(string who)
        {
            var items = SelectedRows().Select(r => r.Item).ToList();
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
                Status(done + "." + (p.Item2 > 0 ? $" {p.Item2:N0} local families are checked out by others: their parameters weren't updated (the note is saved)." : ""));
            });
        }

        // ═══════════════ Model markers ═══════════════

        /// <summary>
        /// The listed changes drawn in the model (federator/ChangeHighlightServer, its own layer next to Link Changes): moves as
        /// a magenta box on ours (this model) and a cyan box where the linked model puts it, each with a badge, double-headed
        /// arrow between; new as a green box where ours will appear; orphans red (solid on ours with no consultant element,
        /// dashed where the consultant element lost its family of ours); type / replacement / match changes as a box on ours.
        /// In-sync rows aren't drawn. Badges: click = select the row, right-click = the row menu (ui/BadgeHitOverlay).
        /// </summary>
        private void PushMarkers()
        {
            var items = new List<ChangeHighlightServer.Item>();
            if (_showInModel.IsChecked == true)
            {
                var selected = new HashSet<string>(SelectedRows().Select(r => r.Item.Key));
                foreach (var r in AllRows())
                {
                    var i = r.Item;
                    if (i.Kind == SyncKind.InSync || i.Kind == SyncKind.Unmapped || i.ShowMin == null || i.ShowMax == null) continue;
                    var it = new ChangeHighlightServer.Item
                    {
                        Min = i.ShowMin, Max = i.ShowMax, Key = "ls:" + i.Key, Emphasis = selected.Contains(i.Key), Thin = i.Rejected,
                        Kind = i.Kind == SyncKind.New ? ChangeHighlightServer.Kind.Added
                             : LinkSyncPlanner.IsOrphan(i.Kind) ? ChangeHighlightServer.Kind.Orphan
                             : i.Kind == SyncKind.Replaced ? ChangeHighlightServer.Kind.Replaced
                             : ChangeHighlightServer.Kind.Modified,
                    };
                    if (LinkSyncPlanner.IsOrphan(i.Kind))
                    {
                        it.Dashed = i.Kind == SyncKind.OursDeleted;            // linked orphan: where ours was (dashed), approve places it again
                        it.Glyph = i.Kind == SyncKind.OursDeleted ? '+' : '-';  // local orphan: approve deletes ours
                    }
                    if (i.GhostMin != null && i.GhostMax != null)
                    {
                        // Moves: cyan box where the linked model puts ours, magenta box on ours (each with a badge), double-headed arrow between.
                        it.OldMin = i.GhostMin; it.OldMax = i.GhostMax;
                        it.Kind = ChangeHighlightServer.Kind.Linked;
                        it.OldKind = ChangeHighlightServer.Kind.Local;
                        it.DoubleArrow = true;
                    }
                    items.Add(it);
                }
            }
            ChangeHighlightController.ShowLayer(ChangeHighlightController.LinkSyncLayer, items.Count > 0 ? _doc : null, items);
            OwnershipUiEvent.Run(app => { try { app.ActiveUIDocument?.RefreshActiveView(); } catch { } });
        }

        // ═══════════════ Badges in the model + the row menu ═══════════════

        private BadgeHitOverlay _badges;
        private readonly DispatcherTimer _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private bool _badgeBusy, _badgeLogged;
        private string _badgeViewSig;

        private void StartBadges(IntPtr revitMain)
        {
            _badges = new BadgeHitOverlay(revitMain, OnBadgeClicked, BadgeTip, BadgeMenu);
            _badgeTimer.Tick += (s, e) => Safe(BadgeTick);
            _badgeTimer.Start();
        }

        private void StopBadges()
        {
            _badgeTimer.Stop();
            try { _badges?.Close(); } catch { }
            _badges = null;
        }

        private void BadgeTick()
        {
            if (_badges == null || _badgeBusy) return;
            bool on = _showInModel.IsChecked == true && ChangeHighlightServer.Instance.Count > 0 && BadgeHitOverlay.RevitIsForeground();
            if (!on) { _badges.HideTargets(); _badgeViewSig = null; return; }
            _badgeBusy = true;
            OwnershipUiEvent.Run(app =>
            {
                try
                {
                    Safe(() =>
                    {
                        if (_badges == null) return;
                        if (!BadgeHitOverlay.Targets(app, _doc, this, k => k.StartsWith("ls:", StringComparison.Ordinal), ref _badgeViewSig,
                                                     out var rect, out var targets, out var sig, out var info))
                        { _badges.HideTargets(); return; }
                        if (!_badgeLogged) { _badgeLogged = true; Log.Info($"[LinkSync] badge targets: {info}"); }
                        _badges.Place(rect[0], rect[1], rect[2], rect[3], targets, sig);
                    });
                }
                finally { _badgeBusy = false; }
            });
        }

        private Row RowOf(string badgeKey)
        {
            if (badgeKey == null || !badgeKey.StartsWith("ls:", StringComparison.Ordinal)) return null;
            string key = badgeKey.Substring(3);
            return AllRows().FirstOrDefault(r => r.Item.Key == key);
        }

        private void OnBadgeClicked(string key, bool add)
        {
            var row = RowOf(key);
            if (row == null) return;
            var rows = SelectedRows().ToList();
            if (add) { if (rows.Contains(row)) rows.Remove(row); else rows.Add(row); }
            else rows = new List<Row> { row };
            SetSelection(rows);
        }

        private string BadgeTip(string key)
        {
            var row = RowOf(key);
            if (row == null) return null;
            return $"{row.Change} · {row.Category}\nLocal: {row.Ours}\nConsultant: {row.Consultant}\n" +
                   (string.IsNullOrEmpty(row.Decision) ? "" : row.Decision + "\n") + "Click to select · Ctrl+click to add · right-click for actions";
        }

        private ContextMenu BadgeMenu(string key)
        {
            var row = RowOf(key);
            if (row == null) return null;
            if (!SelectedRows().Contains(row)) OnBadgeClicked(key, false);
            var rows = SelectedRows().ToList();
            if (rows.Count == 0) rows.Add(row);
            var cm = new ContextMenu();
            FillMenu(cm, rows);
            return cm;
        }

        /// <summary>The row menu (badge right-click and the list's context menu): decide, check, connect / disconnect by hand.</summary>
        private void FillMenu(ContextMenu cm, List<Row> rows)
        {
            cm.Items.Clear();
            var items = rows.Select(r => r.Item).ToList();
            var one = items.Count == 1 ? items[0] : null;
            MenuItem Item(string header, string tip, Action click, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, ToolTip = tip, IsEnabled = enabled };
                mi.Click += (s, e) => Safe(click);
                cm.Items.Add(mi);
                return mi;
            }
            string n = items.Count == 1 ? "" : $" ({items.Count:N0})";
            Item("Approve" + n + " — accept linked model position", "Take the consultant's state", () => Decide(items, true),
                 items.Any(i => i.NeedsDecision || i.Rejected || i.Kind == SyncKind.KeptByHand));
            Item("Reject" + n + " — retain current model position", "Keep the local family as it is (set aside)", () => Decide(items, false), items.Any(i => i.NeedsDecision));
            if (items.Any(i => i.Rejected)) Item("Reconsider" + n + " — back for a decision", "Forget the rejection", () => Reconsider(items));
            cm.Items.Add(new Separator());
            Item("Zoom to", "Zoom to the local family and the consultant element", () => Find(false, false, false), one != null);
            Item("Select local", "Select the local family in Revit", () => Find(false, true, false), one != null && one.OursId != 0);
            Item("Select consultant", "Select the consultant element in Revit", () => Find(false, true, true), one != null && one.SourceId != 0);
            cm.Items.Add(new Separator());
            bool connected = one != null && one.OursId != 0 && one.Kind != SyncKind.Unlinked && one.Kind != SyncKind.Match;
            Item("Disconnect from the consultant element", "The local family stays where it is, as a family of this model only; neither is proposed again", () => Disconnect(one), connected);
            Item(connected ? "Connect the local family to another consultant element…" : "Connect the local family to a consultant element…",
                 "Pick the consultant element in the view (linked element)", () => PickConsultant(one), one != null && one.OursId != 0);
            Item("Connect this consultant element to a local family…", "Pick the local family in the view", () => PickOurs(one),
                 one != null && one.OursId == 0 && one.SourceId != 0);
        }

        private void Disconnect(SyncItem item)
        {
            if (item == null) return;
            Api(app =>
            {
                string why = LinkSync.Disconnect(_doc, item, _me);
                Refresh();
                Status(why == null ? "Disconnected: the local family now belongs to this model only." : "Couldn't disconnect: " + why, why != null);
            }, needActive: true);
        }

        private void PickConsultant(SyncItem item)
        {
            if (item == null || item.OursId == 0) return;
            Status("Pick the consultant element in the view (Esc to cancel)…");
            Api(app =>
            {
                var uidoc = app.ActiveUIDocument;
                ARDB.Reference r;
                try { r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.LinkedElement, "Pick the consultant element to connect the local family to"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { Status("Cancelled."); return; }
                var inst = _doc.GetElement(r.ElementId) as ARDB.RevitLinkInstance;
                var link = inst == null ? null : LinkSync.Links(_doc).FirstOrDefault(l => l.Uid == inst.UniqueId);
                var source = link?.Instance?.GetLinkDocument()?.GetElement(r.LinkedElementId);
                var ours = _doc.GetElement(RevitExtensions.CreateId(item.OursId)) as ARDB.FamilyInstance;
                Connect(link, ours, source);
            }, needActive: true);
        }

        private void PickOurs(SyncItem item)
        {
            if (item == null || item.SourceId == 0) return;
            Status("Pick the local family in the view (Esc to cancel)…");
            Api(app =>
            {
                var uidoc = app.ActiveUIDocument;
                ARDB.Reference r;
                try { r = uidoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, new FamilyInstanceFilter(), "Pick the local family to connect to this consultant element"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { Status("Cancelled."); return; }
                var link = LinkSync.Links(_doc).FirstOrDefault(l => l.Uid == item.LinkUid);
                var source = link?.Instance?.GetLinkDocument()?.GetElement(item.NewSourceUid ?? item.SourceUid);
                Connect(link, _doc.GetElement(r.ElementId) as ARDB.FamilyInstance, source);
            }, needActive: true);
        }

        /// <summary>API context.</summary>
        private void Connect(LinkSync.LinkInfo link, ARDB.FamilyInstance ours, ARDB.Element source)
        {
            if (link == null) { Status("That isn't in a loaded consultant link.", true); return; }
            if (ours == null || source == null) { Status("Couldn't find both elements.", true); return; }
            string why = LinkSync.Connect(_doc, link, ours, source, _me, out string note);
            Refresh();
            Status(why == null ? $"Connected the local family to consultant element {source.Id.GetIdValue()}" + (note != null ? $" ({note})." : ".") : "Couldn't connect: " + why, why != null);
        }

        private class FamilyInstanceFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            public bool AllowElement(ARDB.Element e) => e is ARDB.FamilyInstance;
            public bool AllowReference(ARDB.Reference r, ARDB.XYZ p) => false;
        }

        // ═══════════════ Deciding ═══════════════

        private void DecideCurrent(bool approve)
        {
            var row = SelectedRows().FirstOrDefault();
            if (row != null) Decide(new List<SyncItem> { row.Item }, approve);
        }

        private void ReconsiderSelected() => Reconsider(SelectedRows().Select(r => r.Item).ToList());

        private void Reconsider(List<SyncItem> items)
        {
            var todo = items.Where(i => i.Rejected).ToList();
            if (todo.Count == 0) { Status("Nothing set aside selected."); return; }
            Api(app =>
            {
                var r = LinkSync.Reconsider(_doc, todo, _me);
                Refresh();
                Status($"{r.Item1:N0} change(s) back for a decision." + (r.Item2 > 0 ? $" {r.Item2:N0} checked out by others." : ""));
            }, needActive: true);
        }

        private void DecideSelected(bool approve) => Decide(SelectedRows().Select(r => r.Item).ToList(), approve);

        private void ApproveAllShown()
        {
            var shown = AllRows().Select(r => r.Item).Where(i => i.NeedsDecision).ToList();
            if (shown.Count == 0) { Status("Nothing listed needs a decision."); return; }
            Decide(shown, true);
        }

        private void Decide(List<SyncItem> items, bool approve)
        {
            // Approve: changes needing a decision + "kept" ones (= move back to the consultant position). Reject: decisions only.
            // Approve also takes set-aside (rejected) changes: the modeller changed their mind.
            var todo = items.Where(i => approve ? (i.NeedsDecision || i.Rejected || i.Kind == SyncKind.KeptByHand) : i.NeedsDecision).ToList();
            if (todo.Count == 0) { Status(approve ? "Nothing selected to approve." : "Nothing selected to reject (kept / in-sync rows have nothing to reject)."); return; }
            int deletes = approve ? todo.Count(i => LinkSyncPlanner.ApproveDeletes(i.Kind)) : 0;
            int unmapped = approve ? todo.Count(i => i.Kind == SyncKind.ConsultantType && i.TargetUid == null) : 0;
            string msg = $"{(approve ? "Approve" : "Reject")} {todo.Count:N0} change(s)?" +
                         (deletes > 0 ? $"\n\n{deletes:N0} of them DELETE the local family (no consultant element for it)." : "") +
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
                Status($"Status parameters written on {r.Item1:N0} local families" + (r.Item2 > 0 ? $"; {r.Item2:N0} checked out by others." : ".") +
                       (_inView.IsChecked == true ? " (only those in the active view)" : ""));
            });
        }

        private void OpenReviewViews() => Api(app => ReviewViewsWindow.EditFor(app, new System.Windows.Interop.WindowInteropHelper(this).Handle));

        // ═══════════════ Checking ═══════════════

        private void Find(bool force3D, bool selectOnly, bool selectConsultant)
        {
            var row = SelectedRows().FirstOrDefault();
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
                    else if (selectOnly) { Status("There's no local family for this change yet."); return; }
                }
                catch (Exception ex) { Log.Info($"[LinkSync] select: {ex.Message}"); }
                if (selectOnly) { Status(selectConsultant ? "Consultant element selected." : "Local family selected."); return; }
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

        /// <summary>Two-line button: bold action + what it means (e.g. Approve / Accept linked model position).</summary>
        private Button Btn2(string title, string subtitle, string tip, Action click)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            content.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center });
            var b = new Button { Content = content, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6), Background = Brushes.White };
            b.Click += (s, e) => Safe(click);
            return b;
        }

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
