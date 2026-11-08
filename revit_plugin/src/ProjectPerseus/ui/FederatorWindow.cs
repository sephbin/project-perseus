using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectPerseus.federator;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ARDB = Autodesk.Revit.DB;
using UIApplication = Autodesk.Revit.UI.UIApplication;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modeless "Federator" review panel. Top: link + saved comparison pickers, "Compare against file…". Middle: the
    /// changes grid (grouped, filtered: kind, what changed, category, level, status, assignee, search, only in the
    /// active view) beside a detail pane (description of the change, parameter before/after, likely replacements of
    /// deleted elements, status, actions with assignees, comment thread). Double-click selects the linked elements in
    /// Revit and zooms to them (federator/ReviewNavigator). Everything that touches the model runs through
    /// queue/OwnershipUiEvent; reviews are written as append-only events (federator/ReviewStore).
    /// </summary>
    internal class FederatorWindow : Window
    {
        internal static FederatorWindow Instance { get; private set; }

        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);
        private static readonly Brush Accent = Frozen(0x00, 0x7C, 0xB8);
        private static readonly Brush ErrorBrush = Frozen(0xC0, 0x26, 0x26);
        private static readonly Brush ActiveFill = Frozen(0xE8, 0xF1, 0xFC);

        private const string All = "All";
        private static readonly string[] GroupModes = { "Category", "Level", "Kind", "Status", "Assignee", "Type", "None" };
        private static readonly KeyValuePair<string, ChangeAspect>[] Aspects =
        {
            new KeyValuePair<string, ChangeAspect>("Any change", ChangeAspect.None),
            new KeyValuePair<string, ChangeAspect>("Moved", ChangeAspect.Moved),
            new KeyValuePair<string, ChangeAspect>("Geometry", ChangeAspect.Geometry),
            new KeyValuePair<string, ChangeAspect>("Type", ChangeAspect.Type),
            new KeyValuePair<string, ChangeAspect>("Level", ChangeAspect.Level),
            new KeyValuePair<string, ChangeAspect>("Parameters", ChangeAspect.Parameters),
            new KeyValuePair<string, ChangeAspect>("Workset", ChangeAspect.Workset),
            new KeyValuePair<string, ChangeAspect>("Category", ChangeAspect.Category),
        };

        private class LinkItem
        {
            internal string Uid, Name, Path;
            internal bool Loaded;
            public override string ToString() => Loaded ? Name : Name + "  (unloaded)";
        }

        private class ComparisonItem
        {
            internal ComparisonRecord Record;
            public override string ToString() =>
                $"{Record.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}  vs  {Record.OldTitle}  ({Record.CreatedBy})";
        }

        // ── State ──
        private ARDB.Document _doc;
        private string _me = Environment.UserName;
        private readonly List<LinkItem> _links = new List<LinkItem>();
        private List<ComparisonRecord> _records = new List<ComparisonRecord>();
        private List<ReviewEvent> _events = new List<ReviewEvent>();
        private ComparisonRecord _record;
        private List<ChangeRow> _rows = new List<ChangeRow>();
        private ListCollectionView _view;
        private ChangeFilter _filter = new ChangeFilter();
        private readonly Dictionary<string, ChangeRow> _rowByUid = new Dictionary<string, ChangeRow>();
        private readonly Dictionary<long, ChangeRow> _rowById = new Dictionary<long, ChangeRow>();
        private readonly Dictionary<string, List<ChangeRow>> _replaces = new Dictionary<string, List<ChangeRow>>(); // added uid → deleted rows suggesting it
        private HashSet<long> _inView;
        private bool _loading, _suppressSelection;
        // Preview column: group header / column header check boxes, kept to refresh their ticked / mixed state.
        private readonly List<WeakReference<CheckBox>> _groupChecks = new List<WeakReference<CheckBox>>();
        private CheckBox _allCheck;
        private bool _previewDirty;
        private LinkTransform _toHost = LinkTransform.Identity;
        // Noise rules for the current link (shared in the model) and the triage group the grid is narrowed to.
        private NoiseRules _noise = new NoiseRules();
        private HashSet<string> _focusUids;
        private string _focusLabel;
        private CheckBox _hideNoiseBox;
        private Button _clearFocus;
        private TriageWindow _triage;
        /// <summary>Raised after reviews, noise rules or the comparison change (the triage window refreshes).</summary>
        internal event Action ReviewChanged;
        private const int BatchSize = 2000;

        // ── Controls ──
        private readonly ComboBox _linkBox, _comparisonBox, _kindBox, _aspectBox, _categoryBox, _levelBox, _statusBox, _assigneeBox, _groupBox;
        private readonly TextBox _search;
        private readonly CheckBox _inViewBox;
        private readonly Button _compareButton, _deleteButton;
        private readonly TextBlock _summary, _count, _statusLine;
        private readonly DataGrid _grid;
        private readonly StackPanel _detail;
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly CheckBox _highlightBox;
        private readonly ComboBox _highlightMode, _highlightPlace;
        private const string PlaceOnTop = "On top", PlaceUnder = "Underneath";
        private const string HighlightShown = "Shown rows", HighlightSelected = "Selected rows";

        /// <summary>Opens the panel (or brings it to the front) on the active model. Needs an API context.</summary>
        internal static void Open(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Federator", "Open a project model with consultant links first.");
                return;
            }
            if (Instance != null)
            {
                if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
                Instance.Activate();
                if (!doc.Equals(Instance._doc)) Instance.Reload(app);
                return;
            }
            var w = new FederatorWindow();
            new WindowInteropHelper(w).Owner = app.MainWindowHandle;
            w.Reload(app);
            w.Show();
        }

        private FederatorWindow()
        {
            Instance = this;
            ChangeRow.PreviewChanged += OnRowPreviewChanged;
            Title = "Perseus — Federator";
            Width = 1240;
            Height = 760;
            MinWidth = 860;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Ink;
            Background = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(12) };

            // ── Header: link, comparison, compare ──
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _linkBox = new ComboBox { Margin = new Thickness(6, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _comparisonBox = new ComboBox { Margin = new Thickness(6, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            AddAt(header, Label("Link"), 0);
            AddAt(header, _linkBox, 1);
            AddAt(header, Label("Comparison"), 2);
            AddAt(header, _comparisonBox, 3);
            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _compareButton = MakeButton("Compare against file…", "Pick an archived copy of this consultant model; Perseus opens it in the background and lists what changed since.");
            _deleteButton = MakeButton("Delete", "Delete this comparison (only its author can). Review comments are kept.");
            var refresh = MakeButton("Refresh", "Reload comparisons and reviews from the model (e.g. after Reload Latest).");
            headerButtons.Children.Add(_compareButton);
            headerButtons.Children.Add(_deleteButton);
            var triage = MakeButton("Triage…", "Review changes in bulk: groups of elements with the same kind of change (e.g. 800 sprinklers with only Mark and Comments changed), with accept / ignore / query / noise rules.");
            triage.Click += (s, e) => Safe(OpenTriage);
            headerButtons.Children.Add(triage);
            headerButtons.Children.Add(refresh);
            AddAt(header, headerButtons, 4);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _summary = new TextBlock { Foreground = Muted, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_summary, Dock.Top);
            root.Children.Add(_summary);

            // ── Filters ──
            var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _search = new TextBox { Width = 190, Margin = new Thickness(0, 0, 10, 4), VerticalContentAlignment = VerticalAlignment.Center,
                                    ToolTip = "Search type, family, level, id, parameter names/values, comments, assignees" };
            filters.Children.Add(Captioned("Search", _search));
            _kindBox = Combo(filters, "Kind", new[] { All, "Added", "Deleted", "Modified" }, 95);
            _aspectBox = Combo(filters, "Changed", Aspects.Select(a => a.Key), 110);
            _categoryBox = Combo(filters, "Category", new[] { All }, 150);
            _levelBox = Combo(filters, "Level", new[] { All }, 110);
            _statusBox = Combo(filters, "Status", new[] { All, ChangeFilter.NeedsAttention }.Concat(ReviewStatus.All), 120);
            _assigneeBox = Combo(filters, "Assignee", new[] { All, ChangeFilter.AssigneeMe, ChangeFilter.Unassigned }, 120);
            _groupBox = Combo(filters, "Group by", GroupModes, 95);
            _inViewBox = new CheckBox { Content = "Only in active view", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 7),
                                        ToolTip = "Show only this link's elements visible in the active view (deleted elements are hidden)." };
            filters.Children.Add(_inViewBox);
            _hideNoiseBox = new CheckBox { Content = "Hide noise", IsChecked = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 7),
                                           ToolTip = "Hide changes that are only noise: parameters marked as noise in Triage, and moves under the noise distance." };
            _hideNoiseBox.Checked += (s, e) => Safe(ApplyFilter);
            _hideNoiseBox.Unchecked += (s, e) => Safe(ApplyFilter);
            filters.Children.Add(_hideNoiseBox);
            _clearFocus = MakeButton("", "Show all changes again");
            _clearFocus.VerticalAlignment = VerticalAlignment.Bottom;
            _clearFocus.Margin = new Thickness(12, 0, 0, 4);
            _clearFocus.Visibility = Visibility.Collapsed;
            _clearFocus.Click += (s, e) => Safe(() => { _focusUids = null; _focusLabel = null; ApplyFilter(); });
            filters.Children.Add(_clearFocus);
            DockPanel.SetDock(filters, Dock.Top);
            root.Children.Add(filters);

            // ── Footer ──
            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var footButtons = new StackPanel { Orientation = Orientation.Horizontal };
            var zoom = MakeButton("Zoom to", "Zoom to the selected changes (opens your federator 3D view if they aren't visible here).");
            var select = MakeButton("Select in Revit", "Select the selected changes' linked elements in Revit and zoom to them.");
            _highlightBox = new CheckBox { Content = "Show in view", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                                           ToolTip = "Draw the changes in Revit's views: green added, amber modified, red deleted (old position), grey = where a moved element was. On screen only; use Bake to print." };
            _highlightMode = new ComboBox { Width = 105, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            _highlightMode.Items.Add(HighlightShown);
            _highlightMode.Items.Add(HighlightSelected);
            _highlightMode.SelectedIndex = 0;
            var bake = MakeButton("Bake to views…", "Draw the changes as printable annotations (outlines or revision clouds + labels) in the views you choose.");
            bake.Margin = new Thickness(14, 0, 0, 0);
            _highlightPlace = new ComboBox { Width = 95, Margin = new Thickness(-8, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "On top: drawn over everything visible in the view.\nUnderneath: drawn behind each changed element, like an underlay." };
            _highlightPlace.Items.Add(PlaceOnTop);
            _highlightPlace.Items.Add(PlaceUnder);
            _highlightPlace.SelectedItem = ChangeHighlightController.OnTop ? PlaceOnTop : PlaceUnder;
            footButtons.Children.Add(_highlightBox);
            footButtons.Children.Add(_highlightMode);
            footButtons.Children.Add(_highlightPlace);
            footButtons.Children.Add(zoom);
            footButtons.Children.Add(select);
            footButtons.Children.Add(bake);
            DockPanel.SetDock(footButtons, Dock.Right);
            footer.Children.Add(footButtons);
            _count = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            DockPanel.SetDock(_count, Dock.Left);
            footer.Children.Add(_count);
            _statusLine = new TextBlock { Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            footer.Children.Add(_statusLine);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            // ── Body: grid | detail ──
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 420 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            _grid = BuildGrid();
            AddAt(body, _grid, 0);
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            AddAt(body, splitter, 1);
            _detail = new StackPanel { Margin = new Thickness(12, 0, 4, 12) };
            var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                  BorderBrush = LineBrush, BorderThickness = new Thickness(1, 0, 0, 0) };
            AddAt(body, detailScroll, 2);
            root.Children.Add(body);

            Content = root;

            // ── Wiring ──
            _linkBox.SelectionChanged += (s, e) => Safe(() => { if (!_loading) FillComparisons(null); });
            _comparisonBox.SelectionChanged += (s, e) => Safe(() => { if (!_loading) LoadSelectedComparison(); });
            _compareButton.Click += (s, e) => Safe(CompareAgainstFile);
            _deleteButton.Click += (s, e) => Safe(DeleteComparison);
            refresh.Click += (s, e) => Safe(() => RunApi(Reload, reload: true));
            zoom.Click += (s, e) => Safe(() => ShowSelected(false));
            select.Click += (s, e) => Safe(() => ShowSelected(true));
            foreach (var box in new[] { _kindBox, _aspectBox, _categoryBox, _levelBox, _statusBox, _assigneeBox })
                box.SelectionChanged += (s, e) => Safe(() => { if (!_loading) ApplyFilter(); });
            _groupBox.SelectionChanged += (s, e) => Safe(() => { if (!_loading) Regroup(); });
            _search.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _searchTimer.Tick += (s, e) => Safe(() => { _searchTimer.Stop(); ApplyFilter(); });
            _inViewBox.Checked += (s, e) => Safe(() => RunApi(UpdateInView, needActive: true));
            _inViewBox.Unchecked += (s, e) => Safe(() => { _inView = null; ApplyFilter(); });
            _grid.SelectionChanged += (s, e) => Safe(() => { if (!_suppressSelection) RebuildDetail(); QueueHighlight(); });
            _grid.MouseDoubleClick += (s, e) => Safe(() => { if (_grid.SelectedItems.Count > 0) ShowSelected(true); });
            bake.Click += (s, e) => Safe(OpenBake);
            _highlightBox.Checked += (s, e) => Safe(QueueHighlight);
            _highlightBox.Unchecked += (s, e) => Safe(QueueHighlight);
            _highlightMode.SelectionChanged += (s, e) => Safe(QueueHighlight);
            _highlightPlace.SelectionChanged += (s, e) => Safe(() =>
            {
                ChangeHighlightController.OnTop = (_highlightPlace.SelectedItem as string) != PlaceUnder;
                QueueHighlight();
            });
            _highlightTimer.Tick += (s, e) => Safe(() => { _highlightTimer.Stop(); PushHighlight(); });
            Closed += (s, e) =>
            {
                _searchTimer.Stop();
                _highlightTimer.Stop();
                ChangeRow.PreviewChanged -= OnRowPreviewChanged;
                SavePreview();
                Instance = null;
                ChangeHighlightController.Clear();
                OwnershipUiEvent.Run(app => { try { app.ActiveUIDocument?.RefreshActiveView(); } catch { } });
            };

            RebuildDetail();
        }

        // ═══════════════════════════ Loading ═══════════════════════════

        /// <summary>(Re)loads links, comparisons and reviews from the active model. API context.</summary>
        private void Reload(UIApplication app)
        {
            _doc = app.ActiveUIDocument?.Document;
            _me = app.Application.Username;
            if (_doc == null) { Status("No model is open."); return; }
            Title = "Perseus — Federator · " + _doc.Title;

            string keepLink = (_linkBox.SelectedItem as LinkItem)?.Uid;
            string keepComparison = _record?.Id;

            _links.Clear();
            foreach (ARDB.RevitLinkInstance inst in new ARDB.FilteredElementCollector(_doc).OfClass(typeof(ARDB.RevitLinkInstance)))
            {
                ARDB.Document linkDoc = null;
                try { linkDoc = inst.GetLinkDocument(); } catch { /* unloaded */ }
                string typeName = _doc.GetElement(inst.GetTypeId())?.Name ?? inst.Name;
                _links.Add(new LinkItem { Uid = inst.UniqueId, Name = typeName, Path = linkDoc?.PathName, Loaded = linkDoc != null });
            }
            // Several instances of one link: tell them apart.
            foreach (var g in _links.GroupBy(l => l.Name).Where(g => g.Count() > 1))
            {
                int i = 1;
                foreach (var l in g) l.Name += $"  #{i++}";
            }
            _links.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            _records = ComparisonStore.List(_doc);
            _events = ReviewStore.Load(_doc);

            _loading = true;
            _linkBox.Items.Clear();
            foreach (var l in _links) _linkBox.Items.Add(l);
            // Default: the kept link, else the link with the newest comparison, else the first.
            var pick = _links.FirstOrDefault(l => l.Uid == keepLink)
                       ?? _links.FirstOrDefault(l => _records.Count > 0 && l.Uid == _records[0].LinkInstanceUid)
                       ?? _links.FirstOrDefault();
            _linkBox.SelectedItem = pick;
            _loading = false;

            if (_links.Count == 0) Status("This model has no Revit links.");
            else Status($"Loaded {_records.Count} comparison(s) and {_events.Count} review event(s).");
            FillComparisons(keepComparison);
        }

        private LinkItem SelectedLink => _linkBox.SelectedItem as LinkItem;

        private void FillComparisons(string selectId)
        {
            var link = SelectedLink;
            _loading = true;
            _comparisonBox.Items.Clear();
            if (link != null)
                foreach (var r in _records.Where(r => r.LinkInstanceUid == link.Uid).OrderByDescending(r => r.CreatedUtc))
                    _comparisonBox.Items.Add(new ComparisonItem { Record = r });
            _comparisonBox.SelectedItem = _comparisonBox.Items.Cast<ComparisonItem>().FirstOrDefault(i => i.Record.Id == selectId)
                                          ?? _comparisonBox.Items.Cast<ComparisonItem>().FirstOrDefault();
            _loading = false;
            _compareButton.IsEnabled = link != null && link.Loaded;
            LoadSelectedComparison();
        }

        private void LoadSelectedComparison()
        {
            var rec = (_comparisonBox.SelectedItem as ComparisonItem)?.Record;
            _deleteButton.IsEnabled = rec != null && string.Equals(rec.CreatedBy, _me, StringComparison.OrdinalIgnoreCase);
            if (rec == null)
            {
                ShowComparison(null, null);
                _summary.Text = SelectedLink == null ? "" :
                    SelectedLink.Loaded ? "No comparisons for this link yet. Use \"Compare against file…\" and pick an earlier version of it."
                                        : "This link is unloaded. Reload it in Manage Links to compare.";
                return;
            }
            if (rec.Changes != null) { ShowComparison(rec, rec.Changes); return; }
            Status("Loading comparison…");
            RunApi(app =>
            {
                rec.Changes = ComparisonStore.LoadChanges(_doc, rec.Id) ?? new List<ElementChange>();
                if ((_comparisonBox.SelectedItem as ComparisonItem)?.Record == rec) ShowComparison(rec, rec.Changes);
                Status("");
            });
        }

        private void ShowComparison(ComparisonRecord rec, List<ElementChange> changes)
        {
            SavePreview();
            _record = rec;
            _rows = new List<ChangeRow>();
            _rowByUid.Clear();
            _rowById.Clear();
            _replaces.Clear();
            if (rec != null && changes != null)
            {
                foreach (var c in changes)
                {
                    var row = new ChangeRow(c, rec.LinkInstanceUid);
                    _rows.Add(row);
                    if (!string.IsNullOrEmpty(c.UniqueId)) _rowByUid[c.UniqueId] = row;
                    if (c.Kind != ChangeKind.Deleted) _rowById[c.Id] = row;
                }
                var hidden = PreviewStateStore.Load(rec.Id);
                foreach (var row in _rows)
                    row.SetPreviewQuietly(hidden != null
                        ? !hidden.Contains(row.Change.UniqueId)
                        : !PreviewStateStore.DefaultHiddenCategories.Contains(row.Category));
                foreach (var row in _rows.Where(r => r.Change.Replacements != null))
                    foreach (var cand in row.Change.Replacements)
                    {
                        if (!_replaces.TryGetValue(cand.UniqueId, out var list)) _replaces[cand.UniqueId] = list = new List<ChangeRow>();
                        list.Add(row);
                    }
                _summary.Text =
                    $"{rec.LinkTitle}: compared with {rec.OldTitle} (file saved {rec.OldFileTimeUtc.ToLocalTime():d MMM yyyy HH:mm}) " +
                    $"by {rec.CreatedBy} on {rec.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}.   " +
                    $"+{rec.Added:N0} added   −{rec.Deleted:N0} deleted   ~{rec.Modified:N0} modified   " +
                    $"({rec.Matched:N0} of {rec.OldCount:N0} elements matched; tolerance {ChangeDescriber.Length(rec.Tolerance)})";
            }

            _loading = true;
            Refill(_categoryBox, _rows.Select(r => r.Category));
            Refill(_levelBox, _rows.Select(r => r.Level));
            _loading = false;

            Refold();
            _view = new ListCollectionView(_rows);
            Regroup();
            _grid.ItemsSource = _view;
            ApplyFilter();
            RebuildDetail();

            _toHost = LinkTransform.Identity;
            _focusUids = null;
            if (rec != null)
                RunApi(app =>
                {
                    _toHost = ChangeHighlightController.Capture(LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid));
                    _noise = FederatorSettingsStore.Load<NoiseRules>(_doc, NoiseKey) ?? new NoiseRules();
                    ApplyNoise();
                    PushHighlight();
                });
            else PushHighlight();
            ReviewChanged?.Invoke();
        }

        // ═══════════════════════════ Noise + triage API (used by ui/TriageWindow) ═══════════════════════════

        private string NoiseKey => "noise:" + _record?.LinkInstanceUid;

        internal NoiseRules Noise => _noise;
        internal ComparisonRecord Record => _record;
        internal string Me => _me;
        internal IEnumerable<string> KnownPeople => People();
        internal IEnumerable<string> KnownActionTypes => ActionTypes();

        private void ApplyNoise()
        {
            var set = NoiseFilter.ParameterSet(_noise);
            foreach (var r in _rows) r.IsNoise = NoiseFilter.IsNoise(r.Change, _noise, set);
            ApplyFilter();
        }

        /// <summary>Rows for triage: not noise; only Unreviewed unless includeReviewed.</summary>
        internal List<ChangeRow> TriageSource(bool includeReviewed) =>
            _rows.Where(r => !r.IsNoise && (includeReviewed || r.State.Status == ReviewStatus.Unreviewed)).ToList();

        internal List<ChangeRow> RowsFor(IEnumerable<ElementChange> changes) =>
            changes.Select(c => _rowByUid.TryGetValue(c.UniqueId ?? "", out var r) ? r : null).Where(r => r != null).ToList();

        internal void TriageStatus(List<ChangeRow> rows, string status) => SetStatus(rows, status);
        internal void TriageAssign(List<ChangeRow> rows, string type, string who) => AddAction(rows, type, who);
        internal void TriageComment(List<ChangeRow> rows, string text) => PostComment(rows, text);
        internal void TriagePreview(List<ChangeRow> rows, bool on) { SetPreview(rows, on); ReviewChanged?.Invoke(); }
        internal void TriageShow(List<ChangeRow> rows, bool select) => ShowRows(rows, select);

        /// <summary>Narrows the grid to these changes (clearable from the filter bar).</summary>
        internal void TriageFocus(List<ChangeRow> rows, string label)
        {
            _focusUids = new HashSet<string>(rows.Select(r => r.Change.UniqueId));
            _focusLabel = label;
            ApplyFilter();
            Activate();
        }

        /// <summary>Adds parameters (and / or a minimum move) to the link's noise rules, saved in the model for everyone.</summary>
        internal void TriageAddNoise(IEnumerable<string> parameters, double? minMoveMm)
        {
            var rules = new NoiseRules
            {
                Parameters = _noise.Parameters.Union(parameters ?? Enumerable.Empty<string>()).Distinct().OrderBy(p => p).ToList(),
                MinMoveMm = minMoveMm ?? _noise.MinMoveMm,
            };
            SaveNoise(rules, "Noise rules updated.");
        }

        internal void TriageRemoveNoise(string parameter)
        {
            var rules = new NoiseRules { Parameters = _noise.Parameters.Where(p => p != parameter).ToList(), MinMoveMm = _noise.MinMoveMm };
            SaveNoise(rules, $"'{parameter}' is no longer noise.");
        }

        private void SaveNoise(NoiseRules rules, string done)
        {
            if (_record == null) return;
            string key = NoiseKey;
            RunApi(app =>
            {
                FederatorSettingsStore.Save(_doc, key, rules, _me);
                _noise = rules;
                ApplyNoise();
                ReviewChanged?.Invoke();
                Status(done);
            });
        }

        private void OpenTriage()
        {
            if (_record == null || _rows.Count == 0) { Status("Open a comparison first."); return; }
            if (_triage == null || !_triage.IsLoaded)
            {
                _triage = new TriageWindow(this) { Owner = this };
                _triage.Closed += (s, e) => _triage = null;
                _triage.Show();
            }
            else _triage.Activate();
        }

        /// <summary>Re-folds the review events into row states (after loading or writing events).</summary>
        private void Refold()
        {
            var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in _rows) signatures[r.Key] = r.Change.Signature;
            var states = ReviewLog.Fold(_events, _record?.Id, signatures);
            foreach (var r in _rows) r.SetState(states.TryGetValue(r.Key, out var s) ? s : null);

            string keep = _assigneeBox.SelectedItem as string;
            _loading = true;
            _assigneeBox.Items.Clear();
            foreach (var x in new[] { All, ChangeFilter.AssigneeMe, ChangeFilter.Unassigned }
                         .Concat(_rows.SelectMany(r => r.State.OpenActions.Select(a => a.Assignee))
                                      .Where(a => !string.IsNullOrWhiteSpace(a))
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)))
                _assigneeBox.Items.Add(x);
            _assigneeBox.SelectedItem = _assigneeBox.Items.Contains(keep) ? keep : All;
            _loading = false;
        }

        // ═══════════════════════════ Filter / group ═══════════════════════════

        private void ApplyFilter()
        {
            if (_view == null) { _count.Text = ""; return; }
            string kind = _kindBox.SelectedItem as string;
            _filter = new ChangeFilter
            {
                Search = _search.Text,
                Kind = kind == null || kind == All ? (ChangeKind?)null : (ChangeKind)Enum.Parse(typeof(ChangeKind), kind),
                Aspect = Aspects.FirstOrDefault(a => a.Key == (_aspectBox.SelectedItem as string)).Value,
                Category = Value(_categoryBox),
                Level = Value(_levelBox),
                Status = Value(_statusBox),
                Assignee = Value(_assigneeBox),
                Me = _me,
                InView = _inViewBox.IsChecked == true ? (_inView ?? new HashSet<long>()) : null,
            };
            var f = _filter;
            bool hideNoise = _hideNoiseBox.IsChecked == true;
            var focus = _focusUids;
            _view.Filter = o => o is ChangeRow r
                                && (!hideNoise || !r.IsNoise)
                                && (focus == null || focus.Contains(r.Change.UniqueId))
                                && f.Matches(r.Change, r.State);
            int shown = _view.Count;
            int noise = _rows.Count(r => r.IsNoise);
            _count.Text = (shown == _rows.Count ? $"{_rows.Count:N0} changes" : $"Showing {shown:N0} of {_rows.Count:N0}")
                          + (noise > 0 ? $" · {noise:N0} noise{(hideNoise ? " hidden" : "")}" : "");
            _clearFocus.Visibility = focus == null ? Visibility.Collapsed : Visibility.Visible;
            _clearFocus.Content = focus == null ? "" : $"✕  Group: {_focusLabel}";
            RefreshPreviewChecks();
            QueueHighlight();
        }

        // ═══════════════════════════ Preview column ═══════════════════════════

        private DataGridTemplateColumn PreviewColumn()
        {
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                          new Binding(nameof(ChangeRow.Preview)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            cb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            _allCheck = new CheckBox { ToolTip = "Preview every change shown in the list", HorizontalAlignment = HorizontalAlignment.Center };
            _allCheck.Click += (s, e) => Safe(() =>
            {
                if (_view == null) return;
                SetPreview(_view.Cast<ChangeRow>(), _allCheck.IsChecked == true);
            });
            return new DataGridTemplateColumn
            {
                Header = _allCheck,
                Width = new DataGridLength(30),
                CanUserSort = true,
                SortMemberPath = nameof(ChangeRow.Preview),
                CellTemplate = new DataTemplate { VisualTree = cb },
            };
        }

        private void OnRowPreviewChanged(ChangeRow row)
        {
            _previewDirty = true;
            RefreshPreviewChecks();
            QueueHighlight();
        }

        private void SetPreview(IEnumerable<ChangeRow> rows, bool on)
        {
            foreach (var r in rows) r.SetPreviewQuietly(on);
            _previewDirty = true;
            RefreshPreviewChecks();
            QueueHighlight();
        }

        private void RegisterGroupCheck(CheckBox cb)
        {
            _groupChecks.RemoveAll(w => !w.TryGetTarget(out var c) || !c.IsLoaded && c != cb);
            if (!_groupChecks.Any(w => w.TryGetTarget(out var c) && c == cb)) _groupChecks.Add(new WeakReference<CheckBox>(cb));
            cb.IsChecked = StateOf((cb.DataContext as CollectionViewGroup)?.Items.OfType<ChangeRow>());
        }

        private void GroupCheckClicked(CheckBox cb)
        {
            var rows = (cb.DataContext as CollectionViewGroup)?.Items.OfType<ChangeRow>().ToList();
            if (rows == null) return;
            SetPreview(rows, cb.IsChecked == true);
        }

        /// <summary>Ticked / unticked / mixed (null) for group headers and the column header.</summary>
        private void RefreshPreviewChecks()
        {
            foreach (var w in _groupChecks.ToList())
            {
                if (!w.TryGetTarget(out var cb)) { _groupChecks.Remove(w); continue; }
                if (cb.DataContext is CollectionViewGroup g) cb.IsChecked = StateOf(g.Items.OfType<ChangeRow>());
            }
            if (_allCheck != null) _allCheck.IsChecked = _view == null ? (bool?)true : StateOf(_view.Cast<ChangeRow>());
        }

        private static bool? StateOf(IEnumerable<ChangeRow> rows)
        {
            if (rows == null) return true;
            bool any = false, on = false, off = false;
            foreach (var r in rows)
            {
                any = true;
                if (r.Preview) on = true; else off = true;
                if (on && off) return null;
            }
            return !any || on;
        }

        private void SavePreview()
        {
            if (!_previewDirty || _record == null) return;
            _previewDirty = false;
            PreviewStateStore.Save(_record.Id, _rows.Where(r => !r.Preview).Select(r => r.Change.UniqueId));
        }

        // ═══════════════════════════ In-view highlight ═══════════════════════════

        private void QueueHighlight()
        {
            _highlightTimer.Stop();
            _highlightTimer.Start();
        }

        /// <summary>Hands the shown / selected changes to the DirectContext3D server and redraws the active view.</summary>
        private void PushHighlight()
        {
            SavePreview();
            bool on = _highlightBox.IsChecked == true && _record != null && _view != null;
            ChangeHighlightController.Enabled = on;
            if (on)
            {
                var selected = _grid.SelectedItems.Cast<ChangeRow>().ToList();
                var emphasised = new HashSet<string>(selected.Select(r => r.Change.UniqueId));
                IEnumerable<ChangeRow> rows = (_highlightMode.SelectedItem as string) == HighlightSelected
                    ? (IEnumerable<ChangeRow>)selected
                    : _view.Cast<ChangeRow>().Where(r => r.Preview);
                int drawn = ChangeHighlightController.Show(_doc, rows.Select(r => r.Change), _toHost, emphasised);
                if (drawn >= ChangeHighlightController.MaxItems)
                    Status($"Showing the first {ChangeHighlightController.MaxItems:N0} changes in the view (selected first). Filter to see the rest.");
            }
            else ChangeHighlightController.Clear();
            OwnershipUiEvent.Run(app => { try { app.ActiveUIDocument?.RefreshActiveView(); } catch { } });
        }

        // ═══════════════════════════ Bake ═══════════════════════════

        private string SettingsKey => "bake:" + _record?.LinkInstanceUid;

        private void OpenBake()
        {
            var rec = _record;
            if (rec == null || rec.Changes == null) { Status("Open a comparison first."); return; }
            var shown = _view.Cast<ChangeRow>().Where(r => r.Preview).ToList();
            var selected = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            RunApi(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
                if (inst == null) { Status("The link instance is no longer in the model.", true); return; }
                var views = AnnotationBaker.Candidates(_doc);
                var revisions = AnnotationBaker.OpenRevisions(_doc);
                var settings = FederatorSettingsStore.Load<BakeSettings>(_doc, SettingsKey) ?? new BakeSettings();
                ViewTargets.Prune(settings, views);
                var dlg = new BakeDialog(rec.LinkTitle, views, revisions, settings, shown.Count, selected.Count, _rows.Count) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Action == BakeDialog.Choice.None) return;
                var targets = dlg.TargetUids;
                try { FederatorSettingsStore.Save(_doc, SettingsKey, settings, _me); }
                catch (Exception ex) { Log.Info($"[Federator] bake settings not saved: {ex.Message}"); }

                if (dlg.Action == BakeDialog.Choice.Remove)
                {
                    var r = AnnotationBaker.Remove(_doc, rec.LinkInstanceUid, targets);
                    Status($"Removed {r.Removed:N0} baked annotations from {targets.Count} view(s)." +
                           (r.SkippedOwned > 0 ? $" {r.SkippedOwned} are checked out by others and were left." : ""));
                    return;
                }

                var rows = settings.Scope == BakeSettings.ScopeSelected ? selected
                         : settings.Scope == BakeSettings.ScopeAll ? _rows : shown;
                try
                {
                    var result = AnnotationBaker.Bake(_doc, inst, rec, rows.Select(x => x.Change).ToList(), targets, settings,
                        c => LabelFor(c, settings), (text, f) => SyncStatusToast.Progress("Baking link changes", f, text));
                    SyncStatusToast.Hide();
                    string msg = $"Baked {result.Changes:N0} change marks ({result.Annotations:N0} annotations) into {result.Views} view(s)" +
                                 (result.Removed > 0 ? $", replacing {result.Removed:N0} earlier ones" : "") + ".";
                    if (result.SkippedOwned > 0) msg += $" {result.SkippedOwned} earlier annotations are checked out by others and were left.";
                    if (result.Errors.Count > 0)
                    {
                        msg += $" {result.Errors.Count} view(s) failed.";
                        MessageBox.Show(this, msg + "\n\n" + string.Join("\n", result.Errors.Take(15)), "Federator", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    Status(msg, result.Errors.Count > 0);
                }
                finally { SyncStatusToast.Hide(); }
            }, needActive: true);
        }

        private string LabelFor(ElementChange c, BakeSettings s)
        {
            _rowByUid.TryGetValue(c.UniqueId ?? "", out var row);
            string glyph = c.Kind == ChangeKind.Added ? "+" : c.Kind == ChangeKind.Deleted ? "−" : "~";
            string name = ChangeDescriber.TypeName(c.Family, c.Type);
            string text = $"{glyph} {(string.IsNullOrEmpty(name) ? c.Category : name)} (Id {c.Id}) — {ChangeDescriber.Summary(c)}";
            if (s.LabelStatus && row != null)
            {
                var st = row.State;
                var extra = new List<string>();
                if (st.Status != ReviewStatus.Unreviewed) extra.Add(st.Status);
                if (!string.IsNullOrEmpty(st.Assignees)) extra.Add("→ " + st.Assignees);
                if (extra.Count > 0) text += " [" + string.Join(" ", extra) + "]";
            }
            return text;
        }

        private void Regroup()
        {
            if (_view == null) return;
            string mode = _groupBox.SelectedItem as string ?? "Category";
            foreach (var r in _rows) r.GroupKey = GroupKeyOf(r, mode);
            using (_view.DeferRefresh())
            {
                _view.GroupDescriptions.Clear();
                if (mode != "None") _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChangeRow.GroupKey)));
                _view.CustomSort = new RowOrder(mode != "None");
            }
        }

        private static string GroupKeyOf(ChangeRow r, string mode)
        {
            switch (mode)
            {
                case "Level": return string.IsNullOrEmpty(r.Level) ? "(no level)" : r.Level;
                case "Kind": return r.KindName;
                case "Status": return r.State.Status;
                case "Assignee": return string.IsNullOrEmpty(r.Assignee) ? "(unassigned)" : r.Assignee;
                case "Type": return string.IsNullOrEmpty(r.Element) ? "(no type)" : r.Element;
                default: return string.IsNullOrEmpty(r.Category) ? "(no category)" : r.Category;
            }
        }

        private class RowOrder : System.Collections.IComparer
        {
            private readonly bool _grouped;
            internal RowOrder(bool grouped) { _grouped = grouped; }
            public int Compare(object x, object y)
            {
                var a = (ChangeRow)x; var b = (ChangeRow)y;
                int c = _grouped ? string.Compare(a.GroupKey, b.GroupKey, StringComparison.OrdinalIgnoreCase) : 0;
                if (c == 0) c = a.KindOrder.CompareTo(b.KindOrder);
                if (c == 0) c = string.Compare(a.Element, b.Element, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = a.Id.CompareTo(b.Id);
                return c;
            }
        }

        // After reviews change, rows grouped / filtered by status or assignee may need to move.
        private void AfterReviewChange()
        {
            string mode = _groupBox.SelectedItem as string;
            if (mode == "Status" || mode == "Assignee") Regroup();
            var keep = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            ApplyFilter();
            _suppressSelection = true;
            foreach (var r in keep) if (_view.PassesFilter(r)) _grid.SelectedItems.Add(r);
            _suppressSelection = false;
            RebuildDetail();
            ReviewChanged?.Invoke();
        }

        // ═══════════════════════════ Revit events (API context) ═══════════════════════════

        internal void OnViewActivated(ARDB.Document doc, ARDB.View view)
        {
            if (_inViewBox.IsChecked != true || _doc == null || doc == null || !doc.Equals(_doc)) return;
            ComputeInView(view);
        }

        internal void OnRevitSelection(ARDB.Document doc, IList<ARDB.Reference> refs)
        {
            if (_record == null || _doc == null || doc == null || refs == null || !doc.Equals(_doc)) return;
            var inst = LinkedElementRef.FindInstance(_doc, _record.LinkInstanceUid);
            if (inst == null) return;
            var rows = new List<ChangeRow>();
            foreach (var r in refs)
            {
                if (r == null || r.LinkedElementId == ARDB.ElementId.InvalidElementId || r.ElementId != inst.Id) continue;
                if (_rowById.TryGetValue(r.LinkedElementId.GetIdValue(), out var row)) rows.Add(row);
            }
            if (rows.Count == 0) return;
            var visible = rows.Where(r => _view != null && _view.PassesFilter(r)).ToList();
            _suppressSelection = true;
            _grid.SelectedItems.Clear();
            foreach (var row in visible) _grid.SelectedItems.Add(row);
            _suppressSelection = false;
            if (visible.Count > 0) _grid.ScrollIntoView(visible[0]);
            RebuildDetail();
            if (visible.Count < rows.Count) Status($"{rows.Count - visible.Count} of the selected linked elements are hidden by the filters.");
        }

        private void UpdateInView(UIApplication app) => ComputeInView(app.ActiveUIDocument?.ActiveView);

        private void ComputeInView(ARDB.View view)
        {
            var inst = _record == null ? null : LinkedElementRef.FindInstance(_doc, _record.LinkInstanceUid);
            if (inst == null || view == null) { _inView = new HashSet<long>(); ApplyFilter(); return; }
            var ids = ReviewNavigator.VisibleLinkedIds(_doc, view, inst);
            _inView = ids ?? new HashSet<long>();
            ApplyFilter();
            Status(ids == null ? $"'{view.Name}' doesn't show the model; nothing is in view."
                               : $"{_inView.Count:N0} elements of this link are visible in '{view.Name}'.");
        }

        // ═══════════════════════════ Commands ═══════════════════════════

        private void CompareAgainstFile()
        {
            var link = SelectedLink;
            if (link == null || !link.Loaded) { Status("Pick a loaded link first."); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Pick an earlier version of {link.Name}",
                Filter = "Revit models (*.rvt)|*.rvt",
                CheckFileExists = true,
            };
            try
            {
                string dir = string.IsNullOrEmpty(link.Path) ? null : Path.GetDirectoryName(link.Path);
                if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            catch { /* cloud / odd path */ }
            if (dlg.ShowDialog(this) != true) return;
            string path = dlg.FileName;

            _compareButton.IsEnabled = false;
            Status($"Comparing with {Path.GetFileName(path)}… Revit is busy until it finishes.");
            RunApi(app =>
            {
                try
                {
                    var inst = LinkedElementRef.FindInstance(_doc, link.Uid);
                    if (inst == null) { Status("That link is no longer in the model. Press Refresh."); return; }
                    var result = ComparisonRunner.Run(app, _doc, inst, path);
                    if (result.Warning != null &&
                        MessageBox.Show(this, result.Warning + "\n\nKeep this comparison?", "Federator",
                                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    {
                        ComparisonStore.Delete(_doc, result.Record.Id, _me, out _);
                        Status("Comparison discarded.");
                        return;
                    }
                    _records.Insert(0, result.Record);
                    FillComparisons(result.Record.Id);
                    Status($"Compared: +{result.Record.Added:N0} added, −{result.Record.Deleted:N0} deleted, ~{result.Record.Modified:N0} modified.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] comparison failed: {ex}");
                    Status("Comparison failed: " + ex.Message, true);
                }
                finally { _compareButton.IsEnabled = SelectedLink?.Loaded == true; }
            });
        }

        private void DeleteComparison()
        {
            var rec = _record;
            if (rec == null) return;
            if (MessageBox.Show(this, $"Delete the comparison with {rec.OldTitle}?\nReview comments and actions are kept.",
                                "Federator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunApi(app =>
            {
                if (!ComparisonStore.Delete(_doc, rec.Id, _me, out string reason)) { Status("Not deleted: " + reason, true); return; }
                _records.Remove(rec);
                FillComparisons(null);
                Status("Comparison deleted.");
            });
        }

        private void ShowSelected(bool select)
        {
            var rows = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            if (rows.Count == 0 || _record == null) { Status("Select one or more changes first."); return; }
            ShowRows(rows, select);
        }

        private void ShowRows(List<ChangeRow> rows, bool select)
        {
            var rec = _record;
            RunApi(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
                if (inst == null) { Status("The link instance is no longer in the model."); return; }
                Status(ReviewNavigator.Show(app.ActiveUIDocument, inst, rows.Select(r => r.Change).ToList(), select));
            }, needActive: true);
        }

        private void SelectRow(string uniqueId)
        {
            if (!_rowByUid.TryGetValue(uniqueId ?? "", out var row)) return;
            if (!_view.PassesFilter(row))
            {
                Status("That element is hidden by the filters; clear them to see it.");
                return;
            }
            _grid.SelectedItems.Clear();
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(row);
        }

        // ═══════════════════════════ Review writes ═══════════════════════════

        private void Write(IList<ChangeRow> rows, Func<ChangeRow, ReviewEvent> make, string done)
        {
            var rec = _record;
            if (rec == null || rows.Count == 0) return;
            var now = DateTime.UtcNow;
            List<ReviewEvent> events;
            if (rows.Count == 1)
            {
                var r = rows[0];
                var e = make(r);
                e.LinkedElementUid = r.Change.UniqueId;
                e.LinkedElementId = r.Change.Id;
                e.Signature = r.Change.Signature;
                events = new List<ReviewEvent> { e };
            }
            else
            {
                // Bulk: batch events (one per BatchSize elements), so thousands of reviews stay a handful of DataStorages.
                events = new List<ReviewEvent>();
                for (int i = 0; i < rows.Count; i += BatchSize)
                {
                    var chunk = rows.Skip(i).Take(BatchSize).ToList();
                    var e = make(chunk[0]);
                    e.Elements = chunk.Select(x => x.Change.UniqueId).ToList();
                    e.Signatures = chunk.Where(x => x.Change.Signature != null)
                                        .GroupBy(x => x.Change.UniqueId).ToDictionary(g => g.Key, g => g.First().Change.Signature);
                    events.Add(e);
                }
            }
            foreach (var e in events)
            {
                e.Id = Guid.NewGuid().ToString("N");
                e.ComparisonId = rec.Id;
                e.LinkInstanceUid = rec.LinkInstanceUid;
                e.Author = _me;
                e.CreatedUtc = now;
            }

            RunApi(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
                foreach (var e in events.Where(x => x.Elements == null))
                    if (_rowByUid.TryGetValue(e.LinkedElementUid ?? "", out var row) && row.Change.Kind != ChangeKind.Deleted)
                        e.StableRef = LinkedElementRef.StableRepresentation(_doc, inst, LinkedElementRef.Resolve(inst, e.LinkedElementUid));
                ReviewStore.Add(_doc, events);
                _events.AddRange(events);
                Refold();
                AfterReviewChange();
                Status(done);
            });
        }

        private void SetStatus(IList<ChangeRow> rows, string status) =>
            Write(rows, r => new ReviewEvent { Status = status },
                  rows.Count == 1 ? $"Status set to {status}." : $"{rows.Count} elements set to {status}.");

        private void PostComment(IList<ChangeRow> rows, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Write(rows, r => new ReviewEvent { Text = text.Trim() }, rows.Count == 1 ? "Comment posted." : $"Comment posted on {rows.Count} elements.");
        }

        private void AddAction(IList<ChangeRow> rows, string type, string assignee)
        {
            if (string.IsNullOrWhiteSpace(type)) { Status("Pick or type an action type."); return; }
            if (string.IsNullOrWhiteSpace(assignee)) { Status("Pick or type who it's assigned to."); return; }
            Write(rows, r => new ReviewEvent
            {
                Action = new ReviewAction { Id = Guid.NewGuid().ToString("N"), Type = type.Trim(), Assignee = assignee.Trim() },
            }, $"{type.Trim()} assigned to {assignee.Trim()}" + (rows.Count > 1 ? $" on {rows.Count} elements." : "."));
        }

        private void CloseAction(ChangeRow row, ReviewAction action, string outcome) =>
            Write(new[] { row }, r => new ReviewEvent { ClosesActionId = action.Id, CloseOutcome = outcome },
                  $"{action.Type} ({action.Assignee}) marked {outcome.ToLowerInvariant()}.");

        private void DeleteEvent(ReviewEvent ev)
        {
            if (MessageBox.Show(this, "Delete this entry from the review history?", "Federator",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            RunApi(app =>
            {
                if (!ReviewStore.Delete(_doc, ev.Id, _me, out string reason)) { Status("Not deleted: " + reason, true); return; }
                _events.RemoveAll(e => e.Id == ev.Id);
                Refold();
                AfterReviewChange();
                Status("Deleted.");
            });
        }

        // ═══════════════════════════ Detail pane ═══════════════════════════

        private void RebuildDetail()
        {
            _detail.Children.Clear();
            var rows = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            if (_record == null) { _detail.Children.Add(Muted_("Pick a link and a comparison, or compare against an earlier file.")); return; }
            if (rows.Count == 0) { BuildOverview(); return; }
            if (rows.Count == 1) BuildSingle(rows[0]);
            else BuildMulti(rows);
        }

        private void BuildOverview()
        {
            _detail.Children.Add(Heading("Select a change to review it"));
            _detail.Children.Add(Muted_("Double-click a row to select the linked element in Revit and zoom to it. " +
                                        "Ctrl/Shift-click to review several at once. Tab-select a linked element in Revit to find its row."));
            int attention = _rows.Count(r => r.State.Status == ReviewStatus.Unreviewed || r.State.Status == ReviewStatus.Query);
            int mine = _rows.Count(r => r.State.OpenActions.Any(a => string.Equals(a.Assignee, _me, StringComparison.OrdinalIgnoreCase)));
            _detail.Children.Add(Section("This comparison"));
            foreach (var s in ReviewStatus.All)
            {
                int n = _rows.Count(r => r.State.Status == s);
                if (n > 0) _detail.Children.Add(Text($"{s}: {n:N0}", ChangeRow.StatusBrushOf(s)));
            }
            _detail.Children.Add(Text($"Needing attention: {attention:N0}"));
            _detail.Children.Add(Text($"Actions assigned to you: {mine:N0}"));
            _detail.Children.Add(Muted_($"Old file: {_record.OldPath}"));
        }

        private void BuildSingle(ChangeRow row)
        {
            var c = row.Change;
            var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            title.Inlines.Add(new System.Windows.Documents.Run(row.KindGlyph + " ") { Foreground = row.KindBrush });
            title.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(row.Element) ? c.Category : row.Element));
            _detail.Children.Add(title);
            _detail.Children.Add(Muted_($"{c.Category} · Id {c.Id} · {(string.IsNullOrEmpty(c.Level) ? "no level" : c.Level)} · {c.Kind}"));

            _detail.Children.Add(Section("What changed"));
            foreach (var line in ChangeDescriber.Lines(c)) _detail.Children.Add(Text(line));

            if (c.ParameterChanges != null && c.ParameterChanges.Count > 0) _detail.Children.Add(ParameterTable(c.ParameterChanges));

            if (c.Kind == ChangeKind.Deleted && c.Replacements != null && c.Replacements.Count > 0)
            {
                _detail.Children.Add(Section("Possibly replaced by"));
                foreach (var cand in c.Replacements) _detail.Children.Add(CandidateLink(cand.UniqueId,
                    $"{ChangeDescriber.TypeName(cand.Family, cand.Type)} · Id {cand.Id}", $"{cand.Reason} · {cand.Score * 100:0} % match"));
                var both = MakeButton("Show old and replacement", "Zoom to the deleted element's old position and its best replacement.");
                both.HorizontalAlignment = HorizontalAlignment.Left;
                both.Margin = new Thickness(0, 4, 0, 0);
                both.Click += (s, e) => Safe(() =>
                {
                    var list = new List<ChangeRow> { row };
                    if (_rowByUid.TryGetValue(c.Replacements[0].UniqueId, out var best)) list.Add(best);
                    ShowRows(list, true);
                });
                _detail.Children.Add(both);
            }
            if (c.Kind == ChangeKind.Added && _replaces.TryGetValue(c.UniqueId ?? "", out var olds))
            {
                _detail.Children.Add(Section("Possibly replaces"));
                foreach (var old in olds)
                {
                    var cand = old.Change.Replacements.First(x => x.UniqueId == c.UniqueId);
                    _detail.Children.Add(CandidateLink(old.Change.UniqueId, $"{old.Element} · Id {old.Change.Id} (deleted)",
                                                       $"{cand.Reason} · {cand.Score * 100:0} % match"));
                }
            }

            BuildReview(new List<ChangeRow> { row });
            BuildThread(row);
        }

        private void BuildMulti(List<ChangeRow> rows)
        {
            _detail.Children.Add(Heading($"{rows.Count:N0} changes selected"));
            _detail.Children.Add(Muted_(string.Join("   ", rows.GroupBy(r => r.Change.Kind).OrderBy(g => g.Key)
                                                            .Select(g => $"{g.Key}: {g.Count():N0}"))));
            _detail.Children.Add(Muted_(string.Join("   ", rows.GroupBy(r => r.State.Status).OrderBy(g => g.Key)
                                                            .Select(g => $"{g.Key}: {g.Count():N0}"))));
            BuildReview(rows);
        }

        /// <summary>Status buttons, actions and the comment box; applies to every row given.</summary>
        private void BuildReview(List<ChangeRow> rows)
        {
            bool single = rows.Count == 1;
            var state = single ? rows[0].State : null;

            _detail.Children.Add(Section("Review"));
            if (single)
            {
                string by = state.StatusBy == null ? "" : $" — {state.StatusBy}, {state.StatusUtc?.ToLocalTime():d MMM HH:mm}";
                string carried = "";
                if (state.StatusCarried)
                {
                    var from = _records.FirstOrDefault(r => r.Id == state.StatusComparisonId);
                    carried = from == null ? " (carried over: unchanged since it was reviewed)"
                                           : $" (carried over from the {from.CreatedUtc.ToLocalTime():d MMM} comparison: unchanged since)";
                }
                _detail.Children.Add(Text("Status: " + state.Status + by + carried, ChangeRow.StatusBrushOf(state.Status)));
            }
            var statusRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var s in ReviewStatus.Settable)
            {
                var b = MakeButton(s == ReviewStatus.Unreviewed ? "Reset" : s,
                                   s == ReviewStatus.Unreviewed ? "Back to Unreviewed" : $"Set {(single ? "this element" : "the selected elements")} to {s}");
                if (single && state.Status == s) b.Background = ActiveFill;
                string status = s;
                b.Click += (o, e) => Safe(() => SetStatus(rows, status));
                statusRow.Children.Add(b);
            }
            _detail.Children.Add(statusRow);

            // Actions
            _detail.Children.Add(Section("Actions"));
            if (single)
            {
                if (state.OpenActions.Count == 0) _detail.Children.Add(Muted_("No open actions."));
                foreach (var a in state.OpenActions)
                {
                    var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                    var doneB = MakeButton("Done", "Mark this action done");
                    var cancelB = MakeButton("Cancel", "Cancel this action");
                    var action = a;
                    doneB.Click += (o, e) => Safe(() => CloseAction(rows[0], action, ReviewAction.Done));
                    cancelB.Click += (o, e) => Safe(() => CloseAction(rows[0], action, ReviewAction.Cancelled));
                    buttons.Children.Add(doneB);
                    buttons.Children.Add(cancelB);
                    DockPanel.SetDock(buttons, Dock.Right);
                    line.Children.Add(buttons);
                    var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    t.Inlines.Add(new System.Windows.Documents.Run($"{a.Type} → {a.Assignee}") { FontWeight = FontWeights.SemiBold });
                    t.Inlines.Add(new System.Windows.Documents.Run($"   by {a.CreatedBy}, {a.CreatedUtc.ToLocalTime():d MMM}") { Foreground = Muted });
                    line.Children.Add(t);
                    _detail.Children.Add(line);
                }
            }
            else
            {
                int open = rows.Sum(r => r.State.OpenActions.Count);
                _detail.Children.Add(Muted_(open == 0 ? "No open actions on these elements." : $"{open} open action(s) on these elements (select one element to close them)."));
            }
            var addRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var typeBox = new ComboBox { IsEditable = true, Width = 120, Margin = new Thickness(0, 0, 6, 4), ToolTip = "Action type: pick one or type your own" };
            foreach (var t in ActionTypes()) typeBox.Items.Add(t);
            typeBox.Text = ReviewAction.DefaultTypes[0];
            var whoBox = new ComboBox { IsEditable = true, Width = 150, Margin = new Thickness(0, 0, 6, 4), ToolTip = "Assignee: a Revit user, a person or a discipline" };
            foreach (var p in People()) whoBox.Items.Add(p);
            var assign = MakeButton("Assign", single ? "Add this action to the element" : "Add this action to every selected element");
            assign.Click += (o, e) => Safe(() => AddAction(rows, typeBox.Text, whoBox.Text));
            addRow.Children.Add(typeBox);
            addRow.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4), Foreground = Muted });
            addRow.Children.Add(whoBox);
            addRow.Children.Add(assign);
            _detail.Children.Add(addRow);

            // Comment
            _detail.Children.Add(Section(single ? "Add a comment" : "Comment on all selected"));
            var comment = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 160,
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Ctrl+Enter to post" };
            var post = MakeButton("Post comment", "Comments can be added whatever the status");
            post.HorizontalAlignment = HorizontalAlignment.Left;
            post.Margin = new Thickness(0, 4, 0, 0);
            post.Click += (o, e) => Safe(() => PostComment(rows, comment.Text));
            comment.PreviewKeyDown += (o, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Safe(() => PostComment(rows, comment.Text)); }
            };
            _detail.Children.Add(comment);
            _detail.Children.Add(post);
        }

        private void BuildThread(ChangeRow row)
        {
            var events = row.State.Events;
            _detail.Children.Add(Section($"History ({events.Count})"));
            if (events.Count == 0) { _detail.Children.Add(Muted_("No comments yet.")); return; }
            var actionsById = _events.Where(e => e.Action?.Id != null).GroupBy(e => e.Action.Id).ToDictionary(g => g.Key, g => g.First().Action);
            foreach (var e in events.AsEnumerable().Reverse())
            {
                var card = new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 6) };
                var stack = new StackPanel();
                var head = new DockPanel();
                if (string.Equals(e.Author, _me, StringComparison.OrdinalIgnoreCase))
                {
                    var del = LinkButton("Delete", "Delete this entry (yours)");
                    var ev = e;
                    del.Click += (o, a) => Safe(() => DeleteEvent(ev));
                    DockPanel.SetDock(del, Dock.Right);
                    head.Children.Add(del);
                }
                string other = e.ComparisonId != _record?.Id
                    ? "  · " + (_records.FirstOrDefault(r => r.Id == e.ComparisonId) is ComparisonRecord r0 ? $"{r0.CreatedUtc.ToLocalTime():d MMM} comparison" : "earlier comparison")
                    : "";
                head.Children.Add(new TextBlock { Text = $"{e.Author} · {e.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}{other}", FontWeight = FontWeights.SemiBold, FontSize = 11 });
                stack.Children.Add(head);
                if (!string.IsNullOrEmpty(e.Status)) stack.Children.Add(Text("Set status: " + e.Status, ChangeRow.StatusBrushOf(e.Status)));
                if (e.Action != null) stack.Children.Add(Text($"Assigned {e.Action.Type} to {e.Action.Assignee}"));
                if (!string.IsNullOrEmpty(e.ClosesActionId))
                {
                    string what = actionsById.TryGetValue(e.ClosesActionId, out var a) ? $"{a.Type} ({a.Assignee})" : "an action";
                    stack.Children.Add(Text($"{e.CloseOutcome ?? ReviewAction.Done}: {what}"));
                }
                if (!string.IsNullOrWhiteSpace(e.Text)) stack.Children.Add(Text(e.Text));
                if (e.Elements != null && e.Elements.Count > 1) stack.Children.Add(Muted_($"(bulk: applied to {e.Elements.Count:N0} elements)"));
                card.Child = stack;
                _detail.Children.Add(card);
            }
        }

        private UIElement ParameterTable(List<ParameterChange> changes)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            void Cell(string text, int col, Brush fg, FontWeight weight)
            {
                var t = new TextBlock { Text = text, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 8, 1) };
                Grid.SetRow(t, row); Grid.SetColumn(t, col); g.Children.Add(t);
            }
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell("Parameter", 0, Muted, FontWeights.SemiBold); Cell("Before", 1, Muted, FontWeights.SemiBold); Cell("After", 2, Muted, FontWeights.SemiBold);
            foreach (var p in changes.Take(150))
            {
                row++;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                string name = p.Name.StartsWith(LinkDiff.TypeParameterPrefix, StringComparison.Ordinal)
                    ? "Type: " + p.Name.Substring(LinkDiff.TypeParameterPrefix.Length) : p.Name;
                Cell(name, 0, Ink, FontWeights.Normal);
                Cell(p.Old ?? "(none)", 1, p.Old == null ? Muted : Ink, FontWeights.Normal);
                Cell(p.New ?? "(removed)", 2, p.New == null ? Muted : Ink, FontWeights.Normal);
            }
            if (changes.Count > 150)
            {
                row++;
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell($"… and {changes.Count - 150} more", 0, Muted, FontWeights.Normal);
            }
            return g;
        }

        private UIElement CandidateLink(string uniqueId, string text, string detail)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            var link = LinkButton(text, "Go to this element in the list");
            link.Click += (s, e) => Safe(() => SelectRow(uniqueId));
            panel.Children.Add(link);
            panel.Children.Add(Muted_(detail));
            return panel;
        }

        private IEnumerable<string> ActionTypes() =>
            ReviewAction.DefaultTypes.Concat(_events.Where(e => e.Action != null).Select(e => e.Action.Type))
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase);

        private IEnumerable<string> People() =>
            new[] { _me }.Concat(_events.Select(e => e.Author))
                         .Concat(_events.Where(e => e.Action != null).Select(e => e.Action.Assignee))
                         .Concat(_records.Select(r => r.CreatedBy))
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        // ═══════════════════════════ Grid ═══════════════════════════

        private DataGrid BuildGrid()
        {
            var g = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = LineBrush,
                BorderBrush = LineBrush,
                Background = Brushes.White,
                RowBackground = Brushes.White,
                AlternatingRowBackground = Brushes.White,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserResizeRows = false,
                RowHeight = 22,
                EnableRowVirtualization = true,
            };
            VirtualizingPanel.SetIsVirtualizingWhenGrouping(g, true);
            VirtualizingPanel.SetVirtualizationMode(g, VirtualizationMode.Recycling);

            g.Columns.Add(PreviewColumn());
            g.Columns.Add(Coloured("", nameof(ChangeRow.KindGlyph), nameof(ChangeRow.KindBrush), 26, nameof(ChangeRow.KindOrder), true));
            g.Columns.Add(TextCol("Element", nameof(ChangeRow.Element), new DataGridLength(1.3, DataGridLengthUnitType.Star)));
            g.Columns.Add(TextCol("Id", nameof(ChangeRow.Id), new DataGridLength(70)));
            g.Columns.Add(TextCol("Level", nameof(ChangeRow.Level), new DataGridLength(80)));
            g.Columns.Add(TextCol("Change", nameof(ChangeRow.Summary), new DataGridLength(1.2, DataGridLengthUnitType.Star)));
            g.Columns.Add(Coloured("Status", nameof(ChangeRow.Status), nameof(ChangeRow.StatusBrush), 92, nameof(ChangeRow.Status), false));
            g.Columns.Add(TextCol("Assignee", nameof(ChangeRow.Assignee), new DataGridLength(100)));
            g.Columns.Add(TextCol("💬", nameof(ChangeRow.Comments), new DataGridLength(32)));

            // Groups as expanders: "Structural Framing (402)".
            var expander = new FrameworkElementFactory(typeof(Expander));
            expander.SetValue(Expander.IsExpandedProperty, true);
            expander.SetBinding(HeaderedContentControl.HeaderProperty, new Binding());
            var headerPanel = new FrameworkElementFactory(typeof(StackPanel));
            headerPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            var count = new FrameworkElementFactory(typeof(TextBlock));
            count.SetBinding(TextBlock.TextProperty, new Binding("ItemCount") { StringFormat = "  ({0})" });
            count.SetValue(TextBlock.ForegroundProperty, Muted);
            var groupCheck = new FrameworkElementFactory(typeof(CheckBox));
            groupCheck.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            groupCheck.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            groupCheck.SetValue(FrameworkElement.ToolTipProperty, "Preview this group in the view");
            groupCheck.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) => Safe(() => RegisterGroupCheck((CheckBox)s))));
            groupCheck.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((s, e) => Safe(() => GroupCheckClicked((CheckBox)s))));
            headerPanel.AppendChild(groupCheck);
            headerPanel.AppendChild(name);
            headerPanel.AppendChild(count);
            expander.SetValue(HeaderedContentControl.HeaderTemplateProperty, new DataTemplate { VisualTree = headerPanel });
            expander.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            var containerStyle = new Style(typeof(GroupItem));
            containerStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(GroupItem)) { VisualTree = expander }));
            g.GroupStyle.Add(new GroupStyle { ContainerStyle = containerStyle });
            return g;
        }

        private static DataGridTextColumn TextCol(string header, string path, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width, ElementStyle = style };
        }

        private static DataGridTemplateColumn Coloured(string header, string textPath, string brushPath, double width, string sortPath, bool bold)
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding(textPath));
            tb.SetBinding(TextBlock.ForegroundProperty, new Binding(brushPath));
            tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tb.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0));
            if (bold)
            {
                tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
                tb.SetValue(TextBlock.FontSizeProperty, 14.0);
                tb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            }
            return new DataGridTemplateColumn
            {
                Header = header,
                Width = new DataGridLength(width),
                SortMemberPath = sortPath,
                CanUserSort = true,
                CellTemplate = new DataTemplate { VisualTree = tb },
            };
        }

        // ═══════════════════════════ Helpers ═══════════════════════════

        /// <summary>Runs model work in an API context, guarding against a closed / switched document.</summary>
        private void RunApi(Action<UIApplication> work, bool needActive = false, bool reload = false)
        {
            OwnershipUiEvent.Run(app =>
            {
                try
                {
                    if (reload) { work(app); return; }
                    if (_doc == null || !_doc.IsValidObject) { Status("The model was closed. Press Refresh.", true); return; }
                    if (needActive && (app.ActiveUIDocument?.Document == null || !app.ActiveUIDocument.Document.Equals(_doc)))
                    {
                        Status($"Switch to {_doc.Title} first (the panel shows that model), or press Refresh to load the active one.", true);
                        return;
                    }
                    work(app);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] {ex}");
                    Status("Failed: " + ex.Message, true);
                }
            });
        }

        private void Status(string text, bool error = false)
        {
            _statusLine.Text = text ?? "";
            _statusLine.Foreground = error ? ErrorBrush : Ink;
        }

        private void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                Log.Warn($"[Federator] UI: {ex}");
                Status("Something went wrong: " + ex.Message, true);
            }
        }

        private static string Value(ComboBox box)
        {
            string v = box.SelectedItem as string;
            return string.IsNullOrEmpty(v) || v == All ? null : v;
        }

        private static void Refill(ComboBox box, IEnumerable<string> values)
        {
            string keep = box.SelectedItem as string;
            box.Items.Clear();
            box.Items.Add(All);
            foreach (var v in values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                box.Items.Add(v);
            box.SelectedItem = keep != null && box.Items.Contains(keep) ? keep : All;
        }

        private ComboBox Combo(Panel parent, string caption, IEnumerable<string> items, double width)
        {
            var box = new ComboBox { Width = width, Margin = new Thickness(0, 0, 10, 4) };
            foreach (var i in items) box.Items.Add(i);
            box.SelectedIndex = 0;
            parent.Children.Add(Captioned(caption, box));
            return box;
        }

        private static UIElement Captioned(string caption, FrameworkElement control)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = caption, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(control);
            return sp;
        }

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };

        private static Button MakeButton(string text, string tip) =>
            new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
                         Background = Brushes.White, BorderBrush = LineBrush };

        private static Button LinkButton(string text, string tip)
        {
            var tb = new TextBlock { Text = text, Foreground = Accent, TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap };
            var b = new Button { Content = tb, ToolTip = tip, Cursor = Cursors.Hand, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                                 Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            return b;
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Muted,
                            Margin = new Thickness(0, 14, 0, 4) };

        private static TextBlock Text(string text, Brush fg = null) =>
            new TextBlock { Text = text, Foreground = fg ?? Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };

        private static TextBlock Muted_(string text) =>
            new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };

        private static void AddAt(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

}
