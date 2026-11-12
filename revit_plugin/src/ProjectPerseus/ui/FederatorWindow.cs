using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectPerseus.aps;
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
    /// Modeless "Federator" review panel. Top: link + From / To version pickers (live link, cached versions ✓, files,
    /// Browse…) + Compare, then the saved comparison picker. Middle: the
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
        private const string ModifiedKey = "Modified";
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
            internal string ModelKey;       // federator/VersionCatalog identity of the consultant model
            internal string LiveGuid;       // save GUID of the loaded version (null when unloaded / unknown)
            internal int LiveSaves;
            internal string CloudProjectId, CloudItemUrn;   // cloud model (ACC / BIM 360): Data Management ids, else null
            internal Guid CloudProjectGuid, CloudModelGuid;  // from the cloud model path (local); ids resolved via APS when Revit can't
            internal bool IsCloud => CloudItemUrn != null || CloudModelGuid != Guid.Empty;
            public override string ToString() => Loaded ? Name : Name + "  (unloaded)";
        }

        private class ComparisonItem
        {
            internal ComparisonRecord Record;
            public override string ToString() =>
                $"{Record.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}   {Record.OldLabel ?? Record.OldTitle}  →  {Record.NewLabel ?? "Live"}   ({Record.CreatedBy})";
        }

        /// <summary>An entry of the From / To version pickers: the live link, a cached version (✓), a file, or "Browse…".</summary>
        private class VersionItem
        {
            internal ComparisonRunner.Source Source;
            internal string Key;       // identity, so From and To can't be the same version
            internal string Text;
            internal VersionAction Action;
            internal int Saves;        // save count when known (ACC version number is entered per save)
            internal bool Browse => Action != VersionAction.None;   // not a version: picking it does something
            public override string ToString() => Text;
        }

        private enum VersionAction { None, Browse, SetUpCloud, SignIn, Retry, Loading }

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
        // Grid selection -> Revit selection (debounced); the linked ids we pushed, so the echo from Revit is ignored.
        private readonly DispatcherTimer _selectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private HashSet<long> _pushedSelection;
        private DateTime _pushedAt;   // the echo of a push only counts shortly after it (a push that changed nothing never echoes)
        private const int MaxAutoSelect = 1000;
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
        private readonly ComboBox _linkBox, _comparisonBox, _groupBox;
        // Filters: tick lists (all ticked = no filter). Change = kind + what changed on modified elements.
        private readonly MultiPick _changeBox, _categoryBox, _levelBox, _statusBox, _assigneeBox;
        private readonly TextBox _search;
        private readonly CheckBox _inViewBox;
        private readonly Button _compareButton, _deleteButton;
        private readonly ComboBox _fromBox, _toBox;
        private object _lastFrom, _lastTo;
        // Files picked with "Browse…" this session, per link instance (they stay in the pickers).
        private readonly Dictionary<string, List<string>> _browsed = new Dictionary<string, List<string>>();
        private const string Tick = "✓", NoTick = "    ";
        // This model's archive folder (<Perseus root>\<project>_<hash>); set in Reload.
        private string _archiveRoot;
        // Cloud version lists per link instance (fetched in the background), and their state while not loaded:
        // "loading", "signin" (not signed in), or an error message.
        private readonly Dictionary<string, List<CloudVersion>> _cloudVersions = new Dictionary<string, List<CloudVersion>>();
        private readonly Dictionary<string, string> _cloudState = new Dictionary<string, string>();
        private readonly TextBlock _summary, _count, _statusLine;
        // Orphan fix (federator/ReplacementLinks): stored changes before links, folded links, pick mode, suggestions window.
        private List<ElementChange> _stored;
        private ReplacementLinks.State _replacementLinks = new ReplacementLinks.State();
        private ChangeRow _linkSource;
        private readonly Border _linkBanner;
        private readonly TextBlock _linkBannerText;
        private readonly Button _linkPickRevit;
        private Button _replacementsButton;
        private ReplacementWindow _replacementWindow;
        private ReplacementLearning _learning = ReplacementLearning.Empty;
        // Review views (federator/ReviewViews): registered views, change uid -> view uid it's shown in, captured images.
        private ReviewViewSettings _reviewViews = new ReviewViewSettings();
        private readonly Dictionary<string, string> _viewOf = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ViewImageFrame> _frames = new Dictionary<string, ViewImageFrame>(StringComparer.Ordinal);
        private readonly Queue<string> _captureQueue = new Queue<string>();
        private int _captureTotal, _captureDone;
        private bool _capturing;
        // Image captures the user said no to this session ("view uid|reason"), so they aren't asked again after every write.
        private readonly HashSet<string> _captureDeclined = new HashSet<string>(StringComparer.Ordinal);
        private string _imageModel;
        /// <summary>Raised when review view images were captured (thumbnails can refresh).</summary>
        internal event Action ImagesChanged;
        private readonly DataGrid _grid;
        private readonly StackPanel _detail;
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly CheckBox _highlightBox;
        private readonly ComboBox _highlightMode, _highlightPlace;
        private const string PlaceAtElement = "At element", PlaceOnTop = "On top", PlaceUnder = "Underneath";
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
            w.StartBadges(app.MainWindowHandle);
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

            // ── Header: link + From / To versions + Compare; saved comparisons below ──
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var pick = new Grid();
            foreach (var w in new[] { GridLength.Auto, new GridLength(240), GridLength.Auto, new GridLength(1, GridUnitType.Star),
                                      GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                pick.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            const string versionTip = "✓ = a snapshot of this version is saved on this computer: comparing it is quick (nothing to open or download).\n" +
                                      "Live = the version currently loaded in this model. Browse… adds an archived / downloaded copy.";
            _linkBox = new ComboBox { Margin = new Thickness(6, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _fromBox = new ComboBox { Margin = new Thickness(6, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = versionTip };
            _toBox = new ComboBox { Margin = new Thickness(6, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = versionTip };
            foreach (var box in new[] { _fromBox, _toBox })
            {
                box.ToolTip = versionTip + "\nRight-click to enter the ACC version number (V1, V2…) of the From / To version.";
                box.ContextMenu = new ContextMenu();
                box.ContextMenuOpening += (s, e) => Safe(() => FillVersionMenu(((ComboBox)s).ContextMenu));
            }
            AddAt(pick, Label("Link"), 0);
            AddAt(pick, _linkBox, 1);
            AddAt(pick, Label("From"), 2);
            AddAt(pick, _fromBox, 3);
            AddAt(pick, Label("To"), 4);
            AddAt(pick, _toBox, 5);
            _compareButton = MakeButton("Compare", "List what changed from the From version to the To version. Versions not yet saved on this computer are opened in the background (Revit is busy meanwhile).");
            _compareButton.Margin = new Thickness(0);
            var archive = MakeButton("Archive…", "Version archive: Perseus copies each new version of a link when Revit loads it, so it can be compared later without downloading. Set a shared folder for a team history.");
            archive.Margin = new Thickness(6, 0, 0, 0);
            archive.Click += (s, e) => Safe(EditArchiveSettings);
            var compareButtons = new StackPanel { Orientation = Orientation.Horizontal };
            compareButtons.Children.Add(_compareButton);
            compareButtons.Children.Add(archive);
            AddAt(pick, compareButtons, 6);
            header.Children.Add(pick);

            var saved = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            saved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            saved.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            saved.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _comparisonBox = new ComboBox { Margin = new Thickness(6, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            AddAt(saved, Label("Comparison"), 0);
            AddAt(saved, _comparisonBox, 1);
            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            _deleteButton = MakeButton("Delete", "Delete this comparison (only its author can). Review comments are kept.");
            var refresh = MakeButton("Refresh", "Reload comparisons and reviews from the model (e.g. after Reload Latest).");
            headerButtons.Children.Add(_deleteButton);
            var triage = MakeButton("Triage…", "Review changes in bulk: groups of elements with the same kind of change (e.g. 800 sprinklers with only Mark and Comments changed), with accept / ignore / query / noise rules.");
            triage.Click += (s, e) => Safe(OpenTriage);
            headerButtons.Children.Add(triage);
            _replacementsButton = MakeButton("Replacements…", "Deleted elements that look replaced by a new element (consultant deleted and re-modelled it). " +
                                                              "Confirm them to treat each pair as one Replaced change.");
            _replacementsButton.Click += (s, e) => Safe(OpenReplacements);
            headerButtons.Children.Add(_replacementsButton);
            var viewsButton = MakeButton("Review views…", "The go-to views for showing changes: changes not visible in the active view open there (before the 3D view), " +
                                                          "and the panel shows changes on captured images of them.");
            viewsButton.Click += (s, e) => Safe(OpenReviewViews);
            headerButtons.Children.Add(viewsButton);
            headerButtons.Children.Add(refresh);
            AddAt(saved, headerButtons, 2);
            header.Children.Add(saved);
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
            _changeBox = Pick(filters, "Change", 130);
            _changeBox.SetItems(new[] { new MultiPick.Item("Added"), new MultiPick.Item("Deleted"), new MultiPick.Item("Replaced"), new MultiPick.Item(ModifiedKey, "Modified") }
                .Concat(Aspects.Where(a => a.Value != ChangeAspect.None).Select(a => new MultiPick.Item(a.Key, a.Key, ModifiedKey))));
            _categoryBox = Pick(filters, "Category", 150);
            _levelBox = Pick(filters, "Level", 110);
            _statusBox = Pick(filters, "Status", 120);
            _statusBox.SetItems(ReviewStatus.All.Select(x => new MultiPick.Item(x)));
            _statusBox.AddPreset(ChangeFilter.NeedsAttention, "Unreviewed and Query only", ReviewStatus.Unreviewed, ReviewStatus.Query);
            _assigneeBox = Pick(filters, "Assignee", 120);
            _groupBox = Combo(filters, "Group by", GroupModes, 95);
            _inViewBox = new CheckBox { Content = "Only in active view", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 7),
                                        ToolTip = "Show only this link's elements visible in the active view, plus deleted elements whose old position is in it." };
            filters.Children.Add(_inViewBox);
            _hideNoiseBox = new CheckBox { Content = "Hide noise", IsChecked = true, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 7),
                                           ToolTip = "Hide changes that are only noise: categories and parameters marked as noise in Triage, and moves under the noise distance." };
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

            // ── Replacement pick banner (Link to replacement… mode) ──
            _linkBannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ink };
            _linkPickRevit = MakeButton("Pick in Revit…", "Pick the replacing element in the Revit view (any element of this link, also ones that aren't changes).");
            var linkCancel = MakeButton("Cancel", "Stop linking (Esc)");
            _linkPickRevit.Click += (s, e) => Safe(PickReplacementInRevit);
            linkCancel.Click += (s, e) => Safe(EndLinkPick);
            var bannerButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            bannerButtons.Children.Add(_linkPickRevit);
            bannerButtons.Children.Add(linkCancel);
            var bannerDock = new DockPanel();
            DockPanel.SetDock(bannerButtons, Dock.Right);
            bannerDock.Children.Add(bannerButtons);
            bannerDock.Children.Add(_linkBannerText);
            _linkBanner = new Border { Child = bannerDock, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
                                       Background = new SolidColorBrush(Color.FromRgb(0xEF, 0xEA, 0xFF)), BorderBrush = ChangeRow.ReplacedBrush, BorderThickness = new Thickness(1) };
            DockPanel.SetDock(_linkBanner, Dock.Top);
            root.Children.Add(_linkBanner);
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && _linkSource != null) { e.Handled = true; Safe(EndLinkPick); } };

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
            _highlightPlace = new ComboBox { Width = 100, Margin = new Thickness(-8, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "At element (default): drawn on the changed element's face nearest you, so things in front of it still cover it.\n" +
                          "On top: drawn over everything visible in the view (at the cut plane).\n" +
                          "Underneath: drawn behind each changed element, like an underlay." };
            _highlightPlace.Items.Add(PlaceAtElement);
            _highlightPlace.Items.Add(PlaceOnTop);
            _highlightPlace.Items.Add(PlaceUnder);
            var place = ChangeHighlightController.Place;
            _highlightPlace.SelectedItem = place == ChangeHighlightServer.Placement.OnTop ? PlaceOnTop
                : place == ChangeHighlightServer.Placement.Underneath ? PlaceUnder : PlaceAtElement;
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
            _compareButton.Click += (s, e) => Safe(CompareVersions);
            _fromBox.SelectionChanged += (s, e) => Safe(() => OnVersionPicked(_fromBox));
            _toBox.SelectionChanged += (s, e) => Safe(() => OnVersionPicked(_toBox));
            _deleteButton.Click += (s, e) => Safe(DeleteComparison);
            refresh.Click += (s, e) => Safe(() => RunApi(Reload, reload: true));
            zoom.Click += (s, e) => Safe(() => ShowSelected(false));
            select.Click += (s, e) => Safe(() => ShowSelected(true));
            foreach (var box in new[] { _changeBox, _categoryBox, _levelBox, _statusBox, _assigneeBox })
                box.Changed += () => Safe(() => { if (!_loading) ApplyFilter(); });
            _groupBox.SelectionChanged += (s, e) => Safe(() => { if (!_loading) Regroup(); });
            _search.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _searchTimer.Tick += (s, e) => Safe(() => { _searchTimer.Stop(); ApplyFilter(); });
            _inViewBox.Checked += (s, e) => Safe(() => RunApi(UpdateInView, needActive: true));
            _inViewBox.Unchecked += (s, e) => Safe(() => { _inView = null; ApplyFilter(); });
            _grid.SelectionChanged += (s, e) => Safe(() =>
            {
                if (!_suppressSelection && _linkSource != null && _grid.SelectedItems.Count == 1 && TryCompleteLinkPick(_grid.SelectedItem as ChangeRow)) return;
                if (!_suppressSelection) { RebuildDetail(); _selectTimer.Stop(); _selectTimer.Start(); }
                QueueHighlight();
            });
            _selectTimer.Tick += (s, e) => Safe(() => { _selectTimer.Stop(); PushRevitSelection(); });
            Action<string> onArchived = key => Dispatcher.BeginInvoke(new Action(() => Safe(() =>
            {
                if (SelectedLink?.ModelKey == key) { RefillVersions(); Status("A new version of this link was archived."); }
            })));
            LinkArchive.Archived += onArchived;
            Closed += (s, e) => LinkArchive.Archived -= onArchived;
            _grid.MouseDoubleClick += (s, e) => Safe(() => { if (_grid.SelectedItems.Count > 0) ShowSelected(true); });
            _grid.ContextMenu = new ContextMenu();
            _grid.ContextMenuOpening += (s, e) => Safe(() =>
            {
                var rows = _grid.SelectedItems.Cast<ChangeRow>().ToList();
                if (rows.Count == 0 || _record == null) { e.Handled = true; return; }
                FillReviewMenu(_grid.ContextMenu, rows, rows[0], (title, prompt) =>
                    TextPrompt.Ask(new WindowInteropHelper(this).Handle, title, prompt, multiline: true, okText: "Post"));
            });
            bake.Click += (s, e) => Safe(OpenBake);
            _highlightBox.Checked += (s, e) => Safe(QueueHighlight);
            _highlightBox.Unchecked += (s, e) => Safe(QueueHighlight);
            _highlightMode.SelectionChanged += (s, e) => Safe(QueueHighlight);
            _highlightPlace.SelectionChanged += (s, e) => Safe(() =>
            {
                var sel = _highlightPlace.SelectedItem as string;
                ChangeHighlightController.Place = sel == PlaceOnTop ? ChangeHighlightServer.Placement.OnTop
                    : sel == PlaceUnder ? ChangeHighlightServer.Placement.Underneath : ChangeHighlightServer.Placement.AtElement;
                QueueHighlight();
            });
            _highlightTimer.Tick += (s, e) => Safe(() => { _highlightTimer.Stop(); PushHighlight(); });
            Closed += (s, e) =>
            {
                _searchTimer.Stop();
                _highlightTimer.Stop();
                StopBadges();
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

            var clock = System.Diagnostics.Stopwatch.StartNew();
            _links.Clear();
            foreach (ARDB.RevitLinkInstance inst in new ARDB.FilteredElementCollector(_doc).OfClass(typeof(ARDB.RevitLinkInstance)))
            {
                ARDB.Document linkDoc = null;
                try { linkDoc = inst.GetLinkDocument(); } catch { /* unloaded */ }
                string typeName = _doc.GetElement(inst.GetTypeId())?.Name ?? inst.Name;
                var item = new LinkItem { Uid = inst.UniqueId, Name = typeName, Path = linkDoc?.PathName, Loaded = linkDoc != null,
                                          ModelKey = VersionCatalog.ModelKey(_doc, inst, linkDoc, typeName) };
                if (linkDoc != null)
                {
                    try
                    {
                        var dv = ARDB.Document.GetDocumentVersion(linkDoc);
                        if (dv != null) { item.LiveGuid = dv.VersionGUID.ToString(); item.LiveSaves = dv.NumberOfSaves; }
                    }
                    catch { /* no version info */ }
                    // Cloud GUIDs are read locally. Not Document.GetProjectId / GetCloudModelUrn: for consultants' links they
                    // block ~30 s each before failing ("unauthorized to access this hub/project"), freezing the panel's open.
                    // The Data Management ids are looked up via APS (ApsClient.ResolveItemAsync, background) when needed.
                    if (VersionCatalog.TryCloudGuids(linkDoc, out Guid pg, out Guid mg)) { item.CloudProjectGuid = pg; item.CloudModelGuid = mg; }
                }
                _links.Add(item);
            }
            long tLinks = clock.ElapsedMilliseconds;
            // Several instances of one link: tell them apart.
            foreach (var g in _links.GroupBy(l => l.Name).Where(g => g.Count() > 1))
            {
                int i = 1;
                foreach (var l in g) l.Name += $"  #{i++}";
            }
            _links.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            _records = ComparisonStore.List(_doc);
            long tRecords = clock.ElapsedMilliseconds;
            _events = ReviewStore.Load(_doc);
            long tEvents = clock.ElapsedMilliseconds;
            _archiveRoot = LinkArchive.RootFor(_doc);
            _imageModel = ViewImageStore.ModelKey(string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName);
            _reviewViews = ReviewViews.Load(_doc);
            _frames.Clear();
            long tArchive = clock.ElapsedMilliseconds;
            _cloudVersions.Clear();   // Refresh re-reads the cloud version lists too
            _cloudState.Clear();

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
            Log.Info($"[Federator] panel load {clock.ElapsedMilliseconds} ms: links {tLinks} ({_links.Count}), comparisons {tRecords - tLinks}, " +
                     $"reviews {tEvents - tRecords} ({_events.Count}), archive folder {tArchive - tEvents}, pickers + list {clock.ElapsedMilliseconds - tArchive}");
        }

        private LinkItem SelectedLink => _linkBox.SelectedItem as LinkItem;

        private void FillComparisons(string selectId)
        {
            var link = SelectedLink;
            FillVersions((_fromBox.SelectedItem as VersionItem)?.Key, (_toBox.SelectedItem as VersionItem)?.Key);
            _loading = true;
            _comparisonBox.Items.Clear();
            if (link != null)
                foreach (var r in _records.Where(r => r.LinkInstanceUid == link.Uid).OrderByDescending(r => r.CreatedUtc))
                    _comparisonBox.Items.Add(new ComparisonItem { Record = r });
            _comparisonBox.SelectedItem = _comparisonBox.Items.Cast<ComparisonItem>().FirstOrDefault(i => i.Record.Id == selectId)
                                          ?? _comparisonBox.Items.Cast<ComparisonItem>().FirstOrDefault();
            _loading = false;
            UpdateCompareEnabled();
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
                    SelectedLink.Loaded ? "No comparisons for this link yet. Pick a From version (or Browse… for an archived copy) and press Compare."
                                        : "This link is unloaded: you can still compare two saved versions; reload it in Manage Links to compare the live one.";
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
            if (_linkSource != null) EndLinkPick();
            _stored = changes;
            _learning = ReplacementLearning.From(_events);
            if (changes != null && !_learning.IsEmpty) ReplacementMatcher.Suggest(changes, _learning.Bonus);
            _replacementLinks = ReplacementLinks.Fold(_events, rec?.LinkInstanceUid);
            if (rec != null && changes != null)
                changes = ReplacementLinks.Apply(changes, _replacementLinks, rec.Tolerance > 0 ? rec.Tolerance : 1 / 304.8);
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
                    $"{rec.LinkTitle}: {rec.OldLabel ?? rec.OldTitle}  →  {rec.NewLabel ?? "live link"}, compared " +
                    $"by {rec.CreatedBy} on {rec.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}.   " +
                    $"+{rec.Added:N0} added   −{rec.Deleted:N0} deleted   ~{rec.Modified:N0} modified   " +
                    $"({rec.Matched:N0} of {rec.OldCount:N0} elements matched; tolerance {ChangeDescriber.Length(rec.Tolerance)})";
                int replaced = _rows.Count(r => r.Change.Kind == ChangeKind.Replaced);
                if (replaced > 0) _summary.Text += $"   ⇄{replaced:N0} replaced (linked)";
            }
            UpdateReplacementsButton();

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
                    AssignReviewViews(rec, _reviewViews.AutoCapture, false);
                    // Only in active view: recompute for this comparison (its link instance and its deleted elements).
                    if (_inViewBox.IsChecked == true) UpdateInView(app);
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
            foreach (var r in _rows) r.SetShown(NoiseFilter.Effective(r.Change, _noise, set));
            if (_groupBox.SelectedItem as string != null) Regroup();
            ApplyFilter();
            UpdateReplacementsButton();
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

        /// <summary>Adds parameters, categories and / or a minimum move to the link's noise rules, saved in the model for everyone.</summary>
        internal void TriageAddNoise(IEnumerable<string> parameters, double? minMoveMm, IEnumerable<string> categories = null)
        {
            var rules = new NoiseRules
            {
                Parameters = _noise.Parameters.Union(parameters ?? Enumerable.Empty<string>()).Distinct().OrderBy(p => p).ToList(),
                Categories = (_noise.Categories ?? new List<string>()).Union(categories ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(c => c).ToList(),
                MinMoveMm = minMoveMm ?? _noise.MinMoveMm,
            };
            SaveNoise(rules, "Noise rules updated.");
        }

        internal void TriageRemoveNoise(string parameter)
        {
            var rules = new NoiseRules { Parameters = _noise.Parameters.Where(p => p != parameter).ToList(),
                                         Categories = _noise.Categories ?? new List<string>(), MinMoveMm = _noise.MinMoveMm };
            SaveNoise(rules, $"'{parameter}' is no longer noise.");
        }

        internal void TriageRemoveNoiseCategory(string category)
        {
            var rules = new NoiseRules { Parameters = _noise.Parameters,
                                         Categories = (_noise.Categories ?? new List<string>()).Where(c => !string.Equals(c, category, StringComparison.OrdinalIgnoreCase)).ToList(),
                                         MinMoveMm = _noise.MinMoveMm };
            SaveNoise(rules, $"{category} changes count again.");
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
            foreach (var r in _rows)
            {
                states.TryGetValue(r.Key, out var s);
                if (r.Change.Kind == ChangeKind.Replaced && _record != null
                    && states.TryGetValue(ReviewLog.Key(_record.LinkInstanceUid, r.Change.OldUniqueId), out var old))
                    s = WithHistoryOf(s, old, r.Key);
                r.SetState(s);
            }

            _loading = true;
            _assigneeBox.SetItems(new[] { ChangeFilter.AssigneeMe, ChangeFilter.Unassigned }
                .Concat(_rows.SelectMany(r => r.State.OpenActions.Select(a => a.Assignee))
                             .Where(a => !string.IsNullOrWhiteSpace(a))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
                .Select(x => new MultiPick.Item(x)));
            _loading = false;
        }

        // ═══════════════════════════ Filter / group ═══════════════════════════

        private void ApplyFilter()
        {
            if (_view == null) { _count.Text = ""; return; }
            // Change: Added / Deleted / aspects of Modified. All aspects ticked = any modification (also ones with no aspect).
            HashSet<ChangeKind> kinds = null;
            var modAspects = ChangeAspect.None;
            var change = _changeBox.Selected;
            if (change != null)
            {
                kinds = new HashSet<ChangeKind>();
                if (change.Contains("Added")) kinds.Add(ChangeKind.Added);
                if (change.Contains("Deleted")) kinds.Add(ChangeKind.Deleted);
                if (change.Contains("Replaced")) kinds.Add(ChangeKind.Replaced);
                var aspects = Aspects.Where(a => a.Value != ChangeAspect.None).ToList();
                foreach (var a in aspects) if (change.Contains(a.Key)) modAspects |= a.Value;
                if (modAspects != ChangeAspect.None) kinds.Add(ChangeKind.Modified);
                if (aspects.All(a => change.Contains(a.Key))) modAspects = ChangeAspect.None;
            }
            _filter = new ChangeFilter
            {
                Search = _search.Text,
                Kinds = kinds,
                ModifiedAspects = modAspects,
                Categories = _categoryBox.Selected,
                Levels = _levelBox.Selected,
                Statuses = _statusBox.Selected,
                Assignees = _assigneeBox.Selected,
                Me = _me,
                InView = _inViewBox.IsChecked == true ? (_inView ?? new HashSet<long>()) : null,
            };
            var f = _filter;
            bool hideNoise = _hideNoiseBox.IsChecked == true;
            var focus = _focusUids;
            _view.Filter = o => o is ChangeRow r
                                && (!hideNoise || !r.IsNoise)
                                && (focus == null || focus.Contains(r.Change.UniqueId))
                                && f.Matches(r.Shown, r.State);
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
            string text = $"{glyph} {(string.IsNullOrEmpty(name) ? c.Category : name)} (Id {c.Id}) — {ChangeDescriber.Summary(row?.Shown ?? c)}";
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
            UpdateReplacementsButton();
            ReviewChanged?.Invoke();
        }

        // ═══════════════════════════ Badge clicks ═══════════════════════════
        // Invisible click targets over the drawn badges (ui/BadgeHitOverlay): click = select the row (Ctrl / Shift adds or
        // removes), right-click = review menu. Placed from an API-context tick; hidden while the view moves (pan / zoom).

        private BadgeHitOverlay _badges;
        private readonly DispatcherTimer _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private bool _badgeBusy, _badgeLogged;
        private string _badgeViewSig;

        private static readonly HashSet<ARDB.ViewType> BadgeViewTypes = new HashSet<ARDB.ViewType>
        {
            ARDB.ViewType.FloorPlan, ARDB.ViewType.CeilingPlan, ARDB.ViewType.EngineeringPlan, ARDB.ViewType.AreaPlan,
            ARDB.ViewType.Section, ARDB.ViewType.Elevation, ARDB.ViewType.Detail,
        };

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
            bool on = _highlightBox.IsChecked == true && _record != null && ChangeHighlightServer.Instance.Count > 0
                      && BadgeHitOverlay.RevitIsForeground();
            if (!on) { _badges.HideTargets(); _badgeViewSig = null; return; }
            _badgeBusy = true;
            OwnershipUiEvent.Run(app =>
            {
                try { Safe(() => PlaceBadges(app)); }
                finally { _badgeBusy = false; }
            });
        }

        /// <summary>Badge spots from the last highlight build -> screen pixels of the active view (API context).</summary>
        private void PlaceBadges(UIApplication app)
        {
            if (_badges == null) return;
            var uidoc = app.ActiveUIDocument;
            var view = uidoc?.ActiveView;
            if (view == null || _doc == null || !uidoc.Document.Equals(_doc) || !BadgeViewTypes.Contains(view.ViewType))
            { _badges.HideTargets(); return; }
            Autodesk.Revit.UI.UIView uiView = null;
            foreach (var uv in uidoc.GetOpenUIViews()) if (uv.ViewId == view.Id) { uiView = uv; break; }
            if (uiView == null) { _badges.HideTargets(); return; }
            var rect = uiView.GetWindowRectangle();
            var zc = uiView.GetZoomCorners();
            if (zc == null || zc.Count < 2) { _badges.HideTargets(); return; }
            ARDB.XYZ c0 = zc[0], c1 = zc[1], right = view.RightDirection, up = view.UpDirection;

            // Moving (pan / zoom / resize): stay hidden until the view has been still for one tick.
            string viewSig = $"{view.Id.GetIdValue()}|{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}|" +
                             $"{c0.X:F4},{c0.Y:F4},{c0.Z:F4}|{c1.X:F4},{c1.Y:F4},{c1.Z:F4}";
            if (viewSig != _badgeViewSig) { _badgeViewSig = viewSig; _badges.HideTargets(); return; }

            var hits = ChangeHighlightServer.Instance.HitsFor(view.Id.GetIdValue(), out int stamp);
            if (hits == null || hits.Count == 0) { _badges.HideTargets(); return; }
            double spanU = (c1 - c0).DotProduct(right), spanV = (c1 - c0).DotProduct(up);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            if (!(spanU > 0) || !(spanV > 0) || w <= 0 || h <= 0) { _badges.HideTargets(); return; }
            double pxU = w / spanU, pxV = h / spanV;

            // Leave out badges under this panel (the overlay would take the panel's clicks there).
            bool panelShown = IsVisible && WindowState != WindowState.Minimized;
            BadgeHitOverlay.TryGetRect(new WindowInteropHelper(this).Handle, out int pl, out int pt, out int pr, out int pb);
            var targets = new List<BadgeHitOverlay.Target>();
            foreach (var hit in hits)
            {
                var d = new ARDB.XYZ(hit.X, hit.Y, hit.Z) - c0;
                double x = rect.Left + d.DotProduct(right) * pxU, y = rect.Bottom - d.DotProduct(up) * pxV;
                if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom) continue;
                if (panelShown && x >= pl && x <= pr && y >= pt && y <= pb) continue;
                targets.Add(new BadgeHitOverlay.Target { Key = hit.Key, X = x, Y = y, R = hit.R * pxU + 2 });
            }
            if (!_badgeLogged)
            {
                _badgeLogged = true;
                Log.Info($"[Federator] badge targets: view rect {rect.Left},{rect.Top}..{rect.Right},{rect.Bottom} px, " +
                         $"{targets.Count} of {hits.Count} on screen, {pxU:F2} x {pxV:F2} px/ft");
            }
            _badges.Place(rect.Left, rect.Top, rect.Right, rect.Bottom, targets, $"{viewSig}|{stamp}|{pl},{pt},{pr},{pb}");
        }

        private void OnBadgeClicked(string key, bool add)
        {
            Safe(() =>
            {
                if (_view == null || !_rowByUid.TryGetValue(key ?? "", out var row)) return;
                if (_linkSource != null && TryCompleteLinkPick(row)) return;
                if (!_view.PassesFilter(row)) { Status("That change is hidden by the panel's filters."); return; }
                if (add)
                {
                    if (_grid.SelectedItems.Contains(row)) _grid.SelectedItems.Remove(row);
                    else _grid.SelectedItems.Add(row);
                }
                else
                {
                    _grid.SelectedItems.Clear();
                    _grid.SelectedItem = row;
                }
                _grid.ScrollIntoView(row);
            });
        }

        private string BadgeTip(string key)
        {
            if (!_rowByUid.TryGetValue(key ?? "", out var row)) return null;
            var c = row.Shown ?? row.Change;
            string name = string.Join(": ", new[] { c.Family, c.Type }.Where(x => !string.IsNullOrEmpty(x)));
            return $"{c.Kind} · {c.Category}{(name.Length > 0 ? " · " + name : "")}\n{ChangeDescriber.Summary(c)}\n" +
                   $"{row.State.Status}{(string.IsNullOrEmpty(row.State.Assignees) ? "" : " · " + row.State.Assignees)}\n" +
                   "Click to select · Ctrl+click to add · right-click to review";
        }

        /// <summary>Right-click on a badge: review menu for that change, or for the whole selection when it's part of it.</summary>
        private ContextMenu BadgeMenu(string key)
        {
            if (_view == null || !_rowByUid.TryGetValue(key ?? "", out var row)) return null;
            if (_linkSource != null) { TryCompleteLinkPick(row); return null; }
            if (!_grid.SelectedItems.Contains(row)) OnBadgeClicked(key, false);
            var rows = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            if (rows.Count == 0) rows.Add(row);
            var cm = new ContextMenu();
            FillReviewMenu(cm, rows, row, (title, prompt) => _badges?.AskText(title, prompt));
            return cm;
        }

        /// <summary>Review menu (badge right-click and the grid's context menu): statuses, comment, replacement links, zoom.</summary>
        private void FillReviewMenu(ContextMenu cm, List<ChangeRow> rows, ChangeRow row, Func<string, string, string> ask)
        {
            cm.Items.Clear();
            string what = rows.Count == 1 ? $"{row.Change.Kind} · {row.Change.Category}" : $"{rows.Count:N0} selected changes";
            void Add(string header, Action act) =>
                cm.Items.Add(MenuItemFor(header, () => Safe(act)));
            cm.Items.Add(new MenuItem { Header = what, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            cm.Items.Add(new Separator());
            foreach (var st in new[] { ReviewStatus.Accepted, ReviewStatus.Query, ReviewStatus.Resolved, ReviewStatus.Ignored })
            {
                string status = st;
                Add(status, () => SetStatus(rows, status));
            }
            cm.Items.Add(new Separator());
            Add("Comment…", () =>
            {
                string text = ask("Comment", what);
                if (!string.IsNullOrWhiteSpace(text)) PostComment(rows, text);
            });
            var replacement = ReplacementMenuItems(rows, row);
            if (replacement.Count > 0)
            {
                cm.Items.Add(new Separator());
                foreach (var mi in replacement) cm.Items.Add(mi);
            }
            cm.Items.Add(new Separator());
            Add("Zoom to", () => ShowRows(rows, true));
            Add("Hide from preview", () => { SetPreview(rows, false); ReviewChanged?.Invoke(); });
            Add("Show in panel", () => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); });
        }

        // ═══════════════════════════ Orphan fix: replacement links ═══════════════════════════
        // A consultant deleting an element and modelling a new one (new UniqueId) is one change for us: confirmed links
        // (federator/ReplacementLinks, stored as review events) merge the Deleted + Added rows into one Replaced row, and
        // later let references to the old element move to the new one.

        private List<MenuItem> ReplacementMenuItems(List<ChangeRow> rows, ChangeRow row)
        {
            var items = new List<MenuItem>();
            void Add(string header, Action act) => items.Add(MenuItemFor(header, () => Safe(act)));
            if (rows.Count == 2)
            {
                var del = rows.FirstOrDefault(r => r.Change.Kind == ChangeKind.Deleted);
                var add = rows.FirstOrDefault(r => r.Change.Kind == ChangeKind.Added);
                if (del != null && add != null)
                    Add($"⇄  Link as replacement: Id {del.Change.Id} replaced by Id {add.Change.Id}", () => LinkPair(del, add.Change.UniqueId, add.Change.Id));
            }
            if (rows.Count > 1)
            {
                var suggested = rows.Select(SuggestionFor).Where(x => x != null).ToList();
                if (suggested.Count > 0)
                {
                    Add($"⇄  Link each to its suggested replacement ({suggested.Count})", () => ConfirmReplacements(suggested));
                    Add($"Not replacements ({suggested.Count})", () => RejectReplacements(suggested.Select(x => x.Deleted).ToList()));
                }
                var linked = rows.Where(r => r.Change.Kind == ChangeKind.Replaced).ToList();
                if (linked.Count > 0) Add($"Break replacement links ({linked.Count})", () => RejectReplacements(linked));
                return items;
            }

            var c = row.Change;
            switch (c.Kind)
            {
                case ChangeKind.Deleted:
                    foreach (var cand in c.Replacements ?? new List<ReplacementCandidate>())
                        if (_rowByUid.TryGetValue(cand.UniqueId ?? "", out var candRow) && candRow.Change.Kind == ChangeKind.Added)
                        {
                            var target = candRow;
                            Add($"⇄  Replaced by {ChangeDescriber.TypeName(cand.Family, cand.Type)} · Id {cand.Id} ({cand.Score * 100:0} %)",
                                () => LinkPair(row, target.Change.UniqueId, target.Change.Id));
                        }
                    Add("⇄  Link to replacement…", () => StartLinkPick(row));
                    if (c.Replacements != null && c.Replacements.Count > 0 && !_replacementLinks.Rejected.Contains(c.UniqueId ?? ""))
                        Add("Not a replacement", () => RejectReplacements(new List<ChangeRow> { row }));
                    break;
                case ChangeKind.Added:
                    if (_replaces.TryGetValue(c.UniqueId ?? "", out var olds))
                        foreach (var old in olds.Where(o => o.Change.Kind == ChangeKind.Deleted))
                        {
                            var o2 = old;
                            Add($"⇄  Replaces deleted {o2.Element} · Id {o2.Change.Id}", () => LinkPair(o2, c.UniqueId, c.Id));
                        }
                    Add("⇄  Link as replacement of…", () => StartLinkPick(row));
                    break;
                case ChangeKind.Replaced:
                    Add($"Break replacement link (Id {c.OldId} ⇄ Id {c.Id})", () => RejectReplacements(new List<ChangeRow> { row }));
                    break;
            }
            return items;
        }

        /// <summary>A deleted row's best suggested replacement that is still an unlinked Added row; null when none / rejected.</summary>
        internal ReplacementSuggestion SuggestionFor(ChangeRow row)
        {
            var c = row.Change;
            if (c.Kind != ChangeKind.Deleted || c.Replacements == null || _replacementLinks.Rejected.Contains(c.UniqueId ?? "")) return null;
            // Best candidate of the same family first (a detector isn't replaced by a sprinkler), else the best one.
            ReplacementSuggestion other = null;
            foreach (var cand in c.Replacements)
                if (_rowByUid.TryGetValue(cand.UniqueId ?? "", out var add) && add.Change.Kind == ChangeKind.Added)
                {
                    var s = new ReplacementSuggestion { Deleted = row, Replacement = add, Candidate = cand, Alternatives = c.Replacements.Count - 1 };
                    if (SameFamily(c, add.Change)) return s;
                    if (other == null) other = s;
                }
            return other;
        }

        /// <summary>
        /// Unconfirmed suggestions in this comparison, best match first (ui/ReplacementWindow). Suggestions on noise
        /// (Triage noise rules) or on elements / types people ignored are left out unless includeHidden (then Hidden says why).
        /// </summary>
        internal List<ReplacementSuggestion> ReplacementSuggestions(bool includeHidden = false)
        {
            var ignoredTypes = IgnoredTypes();
            var list = new List<ReplacementSuggestion>();
            foreach (var row in _rows)
            {
                var s = SuggestionFor(row);
                if (s == null) continue;
                s.Hidden = HiddenReason(s, ignoredTypes);
                if (s.Hidden == null || includeHidden) list.Add(s);
            }
            return list.OrderByDescending(x => x.Candidate.Score).ToList();
        }

        internal ReplacementLearning Learning => _learning;

        private static string TypeKey(ElementChange c) => (c.Category ?? "") + "|" + ChangeDescriber.TypeName(c.Family, c.Type);

        /// <summary>
        /// Category + type combinations people don't care about: at least 3 of their changes Ignored (e.g. a triage group
        /// ignored) and none Accepted / Query / Resolved.
        /// </summary>
        private HashSet<string> IgnoredTypes()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in _rows.GroupBy(r => TypeKey(r.Change)))
            {
                int ignored = 0; bool cared = false;
                foreach (var r in g)
                {
                    var st = r.State.Status;
                    if (st == ReviewStatus.Ignored) ignored++;
                    else if (st == ReviewStatus.Accepted || st == ReviewStatus.Query || st == ReviewStatus.Resolved) { cared = true; break; }
                }
                if (!cared && ignored >= 3) set.Add(g.Key);
            }
            return set;
        }

        private static bool SameFamily(ElementChange a, ElementChange b) =>
            string.Equals(a.Family ?? "", b.Family ?? "", StringComparison.Ordinal);

        private string HiddenReason(ReplacementSuggestion s, HashSet<string> ignoredTypes)
        {
            // Different family: almost never the same element; shown only once people have linked that pattern before.
            if (!SameFamily(s.Deleted.Change, s.Replacement.Change)
                && _learning.Bias(ReplacementGroups.PatternKey(s.Deleted.Change, s.Replacement.Change)) <= 0) return "different family";
            if (s.Deleted.IsNoise || s.Replacement.IsNoise) return "noise";
            if (s.Deleted.State.Status == ReviewStatus.Ignored || s.Replacement.State.Status == ReviewStatus.Ignored) return "ignored";
            if (ignoredTypes.Contains(TypeKey(s.Deleted.Change)) || ignoredTypes.Contains(TypeKey(s.Replacement.Change))) return "type ignored in triage";
            return null;
        }

        internal List<ChangeRow> LinkedReplacements() => _rows.Where(r => r.Change.Kind == ChangeKind.Replaced).ToList();

        private void UpdateReplacementsButton()
        {
            if (_replacementsButton == null) return;
            int n = _record == null ? 0 : ReplacementSuggestions().Count;
            _replacementsButton.Content = n > 0 ? $"Replacements ({n:N0})…" : "Replacements…";
        }

        private void OpenReplacements()
        {
            if (_record == null || _rows.Count == 0) { Status("Open a comparison first."); return; }
            if (_replacementWindow == null || !_replacementWindow.IsLoaded)
            {
                _replacementWindow = new ReplacementWindow(this) { Owner = this };
                _replacementWindow.Closed += (s, e) => _replacementWindow = null;
                _replacementWindow.Show();
            }
            else _replacementWindow.Activate();
        }

        private ReviewEvent ReplacementEvent(string oldUid, long oldId, string op, string newUid = null, long newId = 0,
                                             string pattern = null, string relation = null) =>
            new ReviewEvent { LinkedElementUid = oldUid, LinkedElementId = oldId, ReplacementOp = op, ReplacedByUid = newUid, ReplacedById = newId,
                              ReplacementPattern = pattern, ReplacementRelation = relation };

        /// <param name="pattern">ReplacementGroups.PatternKey of the pair; computed from the added row when null.</param>
        private void LinkPair(ChangeRow deleted, string newUid, long newId, string pattern = null)
        {
            if (deleted == null || deleted.Change.Kind != ChangeKind.Deleted || string.IsNullOrEmpty(newUid)) return;
            string relation = null;
            if (_rowByUid.TryGetValue(newUid, out var added) && added.Change.Kind == ChangeKind.Added)
            {
                pattern = pattern ?? ReplacementGroups.PatternKey(deleted.Change, added.Change);
                relation = ReplacementGroups.Relation(deleted.Change, added.Change);
            }
            WriteReplacementEvents(new List<ReviewEvent> { ReplacementEvent(deleted.Change.UniqueId, deleted.Change.Id, ReplacementLinks.OpLink, newUid, newId, pattern, relation) },
                                   $"Linked: Id {deleted.Change.Id} replaced by Id {newId}.", newUid);
        }

        internal void ConfirmReplacements(List<ReplacementSuggestion> pairs)
        {
            var events = pairs.Where(p => p != null).GroupBy(p => p.Replacement.Change.UniqueId).Select(g => g.First())   // one-to-one
                .Select(p => ReplacementEvent(p.Deleted.Change.UniqueId, p.Deleted.Change.Id, ReplacementLinks.OpLink,
                                              p.Replacement.Change.UniqueId, p.Replacement.Change.Id,
                                              ReplacementGroups.PatternKey(p.Deleted.Change, p.Replacement.Change),
                                              ReplacementGroups.Relation(p.Deleted.Change, p.Replacement.Change))).ToList();
            if (events.Count == 0) return;
            WriteReplacementEvents(events, events.Count == 1 ? "Replacement linked." : $"{events.Count:N0} replacements linked.",
                                   events.Count == 1 ? events[0].ReplacedByUid : null);
        }

        /// <summary>Deleted rows: not a replacement (no longer suggested). Replaced rows: link broken (back to Deleted + Added).</summary>
        internal void RejectReplacements(List<ChangeRow> rows)
        {
            var events = new List<ReviewEvent>();
            foreach (var r in rows)
            {
                if (r.Change.Kind == ChangeKind.Replaced)
                {
                    _replacementLinks.ByOld.TryGetValue(r.Change.OldUniqueId ?? "", out var link);
                    events.Add(ReplacementEvent(r.Change.OldUniqueId, r.Change.OldId, ReplacementLinks.OpReject, pattern: link?.Pattern));
                }
                else if (r.Change.Kind == ChangeKind.Deleted)
                {
                    // "Not a replacement" is learnt against its best suggestion.
                    var best = SuggestionFor(r);
                    events.Add(ReplacementEvent(r.Change.UniqueId, r.Change.Id, ReplacementLinks.OpReject,
                        pattern: best == null ? null : ReplacementGroups.PatternKey(best.Deleted.Change, best.Replacement.Change),
                        relation: best == null ? null : ReplacementGroups.Relation(best.Deleted.Change, best.Replacement.Change)));
                }
            }
            if (events.Count == 0) return;
            bool broke = rows.Any(r => r.Change.Kind == ChangeKind.Replaced);
            WriteReplacementEvents(events, broke ? (events.Count == 1 ? "Replacement link broken." : $"{events.Count:N0} replacement links broken.")
                                                 : (events.Count == 1 ? "Marked as not a replacement." : $"{events.Count:N0} marked as not replacements."),
                                   events.Count == 1 ? events[0].LinkedElementUid : null);
        }

        private void WriteReplacementEvents(List<ReviewEvent> events, string done, string selectUid)
        {
            var rec = _record;
            if (rec == null || events.Count == 0) return;
            var now = DateTime.UtcNow;
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
                ReviewStore.Add(_doc, events);
                _events.AddRange(events);
                if (_record != rec) return;
                ShowComparison(rec, _stored);   // re-applies the links: rows merge / split
                if (selectUid != null && _rowByUid.ContainsKey(selectUid)) SelectRow(selectUid);
                Status(done);
            });
        }

        private void StartLinkPick(ChangeRow source)
        {
            _linkSource = source;
            bool fromDeleted = source.Change.Kind == ChangeKind.Deleted;
            _linkBannerText.Text = fromDeleted
                ? $"⇄  Pick the element that replaced {source.Element} (Id {source.Change.Id}): click an added (+) change in the list or its badge in the view, or Pick in Revit…   Esc cancels."
                : $"⇄  Pick the deleted element that {source.Element} (Id {source.Change.Id}) replaces: click a deleted (−) change in the list or its badge in the view.   Esc cancels.";
            _linkPickRevit.Visibility = fromDeleted ? Visibility.Visible : Visibility.Collapsed;
            _linkBanner.Visibility = Visibility.Visible;
            Status("");
        }

        private void EndLinkPick()
        {
            _linkSource = null;
            _linkBanner.Visibility = Visibility.Collapsed;
        }

        /// <summary>In pick mode: links the source with this row when one is deleted and the other added. True when linked.</summary>
        private bool TryCompleteLinkPick(ChangeRow target)
        {
            var src = _linkSource;
            if (src == null || target == null || target == src) return false;
            var del = src.Change.Kind == ChangeKind.Deleted ? src : target;
            var add = src.Change.Kind == ChangeKind.Deleted ? target : src;
            if (del.Change.Kind != ChangeKind.Deleted || add.Change.Kind != ChangeKind.Added)
            {
                Status(src.Change.Kind == ChangeKind.Deleted ? "Pick an added (+) change, or Pick in Revit… for any element of the link."
                                                             : "Pick a deleted (−) change.", true);
                return false;
            }
            EndLinkPick();
            LinkPair(del, add.Change.UniqueId, add.Change.Id);
            return true;
        }

        private void PickReplacementInRevit()
        {
            var src = _linkSource;
            var rec = _record;
            if (src == null || rec == null) return;
            RunApi(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
                var linkDoc = inst?.GetLinkDocument();
                if (linkDoc == null) { Status("The link isn't loaded: pick the replacement in the list instead.", true); return; }
                ARDB.Reference picked;
                try
                {
                    picked = app.ActiveUIDocument.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.LinkedElement, new InLinkFilter(inst.Id),
                                                                       $"Pick the element that replaced Id {src.Change.Id} (Esc to cancel)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { Status("Pick cancelled."); return; }
                if (_linkSource != src) return;   // linked from the list meanwhile, or cancelled
                var el = picked == null ? null : linkDoc.GetElement(picked.LinkedElementId);
                if (el == null) { Status("That isn't an element of this link.", true); return; }
                if (el.UniqueId == src.Change.UniqueId) { Status("That's the deleted element itself.", true); return; }
                EndLinkPick();
                string pattern = null;
                if (linkDoc.GetElement(el.GetTypeId()) is ARDB.ElementType et)
                    pattern = ReplacementGroups.PatternKey(src.Change.Category, src.Change.Family, src.Change.Type, et.FamilyName, et.Name);
                else pattern = ReplacementGroups.PatternKey(src.Change.Category, src.Change.Family, src.Change.Type, null, el.Name);
                LinkPair(src, el.UniqueId, el.Id.GetIdValue(), pattern);
            }, needActive: true);
        }

        /// <summary>Revit pick filter: only elements of one link instance.</summary>
        private class InLinkFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            private readonly ARDB.ElementId _instance;
            internal InLinkFilter(ARDB.ElementId instance) { _instance = instance; }
            public bool AllowElement(ARDB.Element elem) => elem != null && elem.Id == _instance;
            public bool AllowReference(ARDB.Reference reference, ARDB.XYZ position) => reference != null && reference.ElementId == _instance;
        }

        /// <summary>A Replaced row's state: its own (new element) status / actions plus the deleted element's history.</summary>
        private static ElementReviewState WithHistoryOf(ElementReviewState own, ElementReviewState old, string key)
        {
            var s = new ElementReviewState
            {
                Key = key,
                Status = own?.Status ?? ReviewStatus.Unreviewed,
                StatusBy = own?.StatusBy, StatusUtc = own?.StatusUtc, StatusCarried = own?.StatusCarried ?? false,
                StatusComparisonId = own?.StatusComparisonId,
                CommentCount = (own?.CommentCount ?? 0) + old.CommentCount,
            };
            s.OpenActions.AddRange(old.OpenActions);
            if (own != null) s.OpenActions.AddRange(own.OpenActions);
            s.Events.AddRange((own?.Events ?? new List<ReviewEvent>()).Concat(old.Events).Distinct().OrderBy(e => e.CreatedUtc));
            s.Assignees = string.Join(", ", s.OpenActions.Select(a => a.Assignee).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase));
            return s;
        }

        private static MenuItem MenuItemFor(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => act();
            return mi;
        }

        // ═══════════════════════════ Revit events (API context) ═══════════════════════════

        internal void OnViewActivated(ARDB.Document doc, ARDB.View view)
        {
            if (_inViewBox.IsChecked != true || _doc == null || doc == null || !doc.Equals(_doc)) return;
            ComputeInView(view);
        }

        /// <summary>Selects the grid's selected changes in Revit (no zoom), so they show as selected in the view too.</summary>
        private void PushRevitSelection()
        {
            var rows = _grid.SelectedItems.Cast<ChangeRow>().ToList();
            if (_record == null || rows.Count == 0) return;
            if (rows.Count > MaxAutoSelect) { Status($"{rows.Count:N0} rows selected: not selected in Revit (over {MaxAutoSelect:N0}). Use Select to do it anyway."); return; }
            var rec = _record;
            var changes = rows.Select(r => r.Change).ToList();
            RunApi(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc?.Document == null || !uidoc.Document.Equals(_doc)) return;   // another model is active: leave it
                var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
                if (inst == null) return;
                _pushedSelection = ReviewNavigator.Select(uidoc, inst, changes);
                _pushedAt = DateTime.UtcNow;
            });
        }

        internal void OnRevitSelection(ARDB.Document doc, IList<ARDB.Reference> refs)
        {
            if (_record == null || _doc == null || doc == null || refs == null || !doc.Equals(_doc)) return;
            var inst = LinkedElementRef.FindInstance(_doc, _record.LinkInstanceUid);
            if (inst == null) return;
            var rows = new List<ChangeRow>();
            var ids = new HashSet<long>();
            foreach (var r in refs)
            {
                if (r == null || r.LinkedElementId == ARDB.ElementId.InvalidElementId || r.ElementId != inst.Id) continue;
                ids.Add(r.LinkedElementId.GetIdValue());
                if (_rowById.TryGetValue(r.LinkedElementId.GetIdValue(), out var row)) rows.Add(row);
            }
            // The echo of our own PushRevitSelection: keep the grid as the user left it (e.g. deleted rows selected).
            var pushed = _pushedSelection;
            bool recent = (DateTime.UtcNow - _pushedAt).TotalSeconds < 2;
            if (pushed != null && recent && pushed.SetEquals(ids) && refs.Count == ids.Count) { _pushedSelection = null; return; }
            _pushedSelection = null;
            if (rows.Count == 0)
            {
                // Clicked empty space or something that isn't one of these changes: deselect the panel too.
                if (_grid.SelectedItems.Count == 0) return;
                _suppressSelection = true;
                _grid.SelectedItems.Clear();
                _suppressSelection = false;
                RebuildDetail();
                return;
            }
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
            // Deleted elements aren't in the live link, so Revit can't report them visible: count them in view when their
            // old box (in host coordinates) falls inside what the view shows. Revit doesn't reuse element ids, so a
            // deleted id can't clash with a live one.
            int deletedInView = 0;
            if (ids != null && _rows != null)
            {
                try
                {
                    var extent = ViewExtent.For(view);
                    var toHost = ChangeHighlightController.Capture(inst);
                    foreach (var row in _rows)
                    {
                        var c = row.Change;
                        bool oldOnly = c.Kind == ChangeKind.Deleted || (c.Kind == ChangeKind.Replaced && c.NewMin == null);
                        if (!oldOnly || c.OldMin == null || c.OldMax == null) continue;
                        toHost.Box(c.OldMin, c.OldMax, out var hmin, out var hmax);
                        if (extent.Contains(new ARDB.XYZ(hmin[0], hmin[1], hmin[2]), new ARDB.XYZ(hmax[0], hmax[1], hmax[2])) && _inView.Add(c.Id))
                            deletedInView++;
                    }
                }
                catch (Exception ex) { Log.Info($"[Federator] deleted elements in view: {ex.Message}"); }
            }
            ApplyFilter();
            Status(ids == null ? $"'{view.Name}' doesn't show the model; nothing is in view."
                               : $"{_inView.Count - deletedInView:N0} elements of this link are visible in '{view.Name}'" +
                                 (deletedInView > 0 ? $", plus {deletedInView:N0} deleted ones that were here." : "."));
        }

        // ═══════════════════════════ Commands ═══════════════════════════

        // ── Version pickers ──

        /// <summary>Fills From / To for the selected link, keeping the given picks when they're still there.</summary>
        // Right-click on From / To: set the ACC version number of either selected version.
        private void FillVersionMenu(ContextMenu cm)
        {
            cm.Items.Clear();
            foreach (var pair in new[] { new { Name = "From", Box = _fromBox }, new { Name = "To", Box = _toBox } })
            {
                var item = pair.Box.SelectedItem as VersionItem;
                bool can = item != null && !item.Browse && item.Saves > 0 && SelectedLink != null;
                var mi = new MenuItem
                {
                    Header = can ? $"Set ACC version number of {pair.Name} (save {item.Saves})…" : $"{pair.Name}: version number can't be set (save count unknown)",
                    IsEnabled = can,
                };
                if (can) { var it = item; mi.Click += (s, e) => Safe(() => AskVersionNumber(it)); }
                cm.Items.Add(mi);
            }
        }

        private void AskVersionNumber(VersionItem item)
        {
            var link = SelectedLink;
            if (link == null || item == null || item.Saves <= 0) return;
            string root = _archiveRoot ?? LinkArchive.LoadSettings().Root;
            LinkArchive.VersionNumbers(root, link.ModelKey).TryGetValue(item.Saves, out int current);
            string text = TextPrompt.Ask(new WindowInteropHelper(this).Handle, "ACC version number",
                $"Version number shown on Autodesk Docs (V1, V2…) for {link.Name.Split(new[] { "  #" }, StringSplitOptions.None)[0]}, save {item.Saves}:",
                current > 0 ? "V" + current : "", hint: "e.g. 11 or V11. Leave empty to clear. Saved in the shared archive folder for everyone.");
            if (text == null) return;
            text = text.Trim().TrimStart('V', 'v').Trim();
            int? number = null;
            if (text.Length > 0)
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
                { Status("Enter a version number such as 11 or V11.", true); return; }
                number = n;
            }
            LinkArchive.SetVersionNumber(root, link.ModelKey, link.Name.Split(new[] { "  #" }, StringSplitOptions.None)[0], item.Saves, number);
            RefillVersions();
            Status(number.HasValue ? $"Save {item.Saves} is V{number}." : $"Version number of save {item.Saves} cleared.");
        }

        private bool _versionDupesLogged;

        private void FillVersions(string keepFrom, string keepTo)
        {
            var link = SelectedLink;
            var items = new List<VersionItem>();
            if (link != null)
            {
                var cached = VersionCatalog.List(link.ModelKey);
                var accNumbers = LinkArchive.VersionNumbers(_archiveRoot ?? LinkArchive.LoadSettings().Root, link.ModelKey);
                string Acc(int saves) => saves > 0 && accNumbers.TryGetValue(saves, out int n) ? $"V{n} · " : "";
                var liveEntry = link.LiveGuid == null ? null : cached.FirstOrDefault(v => SameGuid(v.VersionGuid, link.LiveGuid));
                bool cloudLink = link.IsCloud;
                _cloudVersions.TryGetValue(link.Uid, out var cloudList);
                if (link.Loaded)
                {
                    string acc = Acc(link.LiveSaves);
                    string label = "Live" + (acc.Length > 0 ? " · " + acc.TrimEnd(' ', '·') : liveEntry?.CloudVersionNumber > 0 ? $" · v{liveEntry.CloudVersionNumber}" : "")
                                   + (link.LiveSaves > 0 ? $" · save {link.LiveSaves}" : "") + " · loaded now";
                    items.Add(new VersionItem { Source = ComparisonRunner.Source.Live(label), Key = "live", Saves = link.LiveSaves,
                                                Text = (liveEntry != null ? Tick : NoTick) + "  " + label });
                }

                // Cloud versions (newest first): ✓ when downloaded + cached before, else downloaded on Compare.
                var shownCloud = new HashSet<string>();
                if (cloudList != null)
                    foreach (var cv in cloudList)
                    {
                        string label = CloudLabel(cv);
                        var entry = cached.FirstOrDefault(v => v.CloudVersionId == cv.Id);
                        if (entry != null && link.Loaded && entry == liveEntry) { shownCloud.Add(cv.Id); continue; }   // that's Live
                        shownCloud.Add(cv.Id);
                        items.Add(new VersionItem
                        {
                            Source = entry != null ? ComparisonRunner.Source.Cached(entry, label) : ComparisonRunner.Source.FromCloud(cv, label),
                            Key = "cloud:" + cv.Id, Text = (entry != null ? Tick : NoTick) + "  " + label, Saves = entry?.Saves ?? 0,
                        });
                    }

                var shownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in cached)
                {
                    if (link.Loaded && v == liveEntry) continue;                                  // that's the live one
                    if (v.CloudVersionId != null && shownCloud.Contains(v.CloudVersionId)) continue;   // in the cloud list
                    string label = (v.CloudVersionNumber > 0 ? "" : Acc(v.Saves)) + VersionLabel(v);
                    if (!v.FromLiveLink && !string.IsNullOrEmpty(v.SourcePath)) shownFiles.Add(SafeFull(v.SourcePath));
                    items.Add(new VersionItem { Source = ComparisonRunner.Source.Cached(v, label), Saves = v.Saves,
                                                Key = v.CloudVersionId != null ? "cloud:" + v.CloudVersionId
                                                    : v.VersionGuid != null ? "guid:" + v.VersionGuid.ToLowerInvariant() : "key:" + v.CacheKey,
                                                Text = Tick + "  " + label });
                }
                // Archived copies (LinkArchive: Revit's cache / the link file, copied when a new version was loaded).
                var shownKeys = new HashSet<string>(items.Select(i => i.Key));
                foreach (var a in LinkArchive.List(_archiveRoot ?? LinkArchive.LoadSettings().Root, link.ModelKey))
                {
                    if (link.Loaded && SameGuid(a.VersionGuid, link.LiveGuid)) continue;   // that's Live
                    string key = "guid:" + a.VersionGuid.ToLowerInvariant();
                    var entry = cached.FirstOrDefault(v => SameGuid(v.VersionGuid, a.VersionGuid));
                    if (entry?.CloudVersionId != null) key = "cloud:" + entry.CloudVersionId;
                    if (shownKeys.Contains(key)) continue;   // already listed (cached snapshot / cloud list)
                    shownKeys.Add(key);
                    shownFiles.Add(SafeFull(a.RvtPath));
                    string label = (a.FromCloud ? "☁ " : "") + Acc(a.Saves) + $"save {a.Saves} · loaded {a.SourceTimeUtc.ToLocalTime():d MMM yyyy HH:mm} · archived";
                    items.Add(new VersionItem
                    {
                        Saves = a.Saves,
                        Source = entry != null ? ComparisonRunner.Source.Cached(entry, label) : ComparisonRunner.Source.File(a.RvtPath, label),
                        Key = key, Text = (entry != null ? Tick : NoTick) + "  " + label,
                    });
                }

                // Files compared before (from this model's comparisons) and files picked this session.
                var files = _records.Where(r => r.LinkInstanceUid == link.Uid).OrderByDescending(r => r.CreatedUtc)
                    .SelectMany(r => new[] { r.OldPath, r.NewPath }).ToList();
                if (_browsed.TryGetValue(link.Uid, out var picked)) files.InsertRange(0, picked);
                foreach (var f in files.Where(f => !string.IsNullOrEmpty(f)))
                {
                    string full = SafeFull(f);
                    if (shownFiles.Contains(full) || full.StartsWith(ApsClient.DownloadFolder, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(_archiveRoot ?? LinkArchive.LoadSettings().Root, StringComparison.OrdinalIgnoreCase)
                        || (link.Path != null && string.Equals(full, SafeFull(link.Path), StringComparison.OrdinalIgnoreCase))) continue;
                    if (!System.IO.File.Exists(f)) continue;
                    shownFiles.Add(full);
                    string stamp = SnapshotCache.StampOf(f);
                    bool isCached = stamp != null && SnapshotCache.Has(SnapshotCache.FileKey(f, stamp));
                    string label = $"{Path.GetFileName(f)} · saved {System.IO.File.GetLastWriteTime(f):d MMM yyyy HH:mm}";
                    items.Add(new VersionItem { Source = ComparisonRunner.Source.File(f, label), Key = "file:" + full.ToLowerInvariant(),
                                                Text = (isCached ? Tick : NoTick) + "  " + label });
                }

                // One entry per save: a cloud link's loaded GUID changes per session, so the same save shows up as Live, a
                // cached snapshot and archived copies under different GUIDs. Keep Live, else a cached (✓) one, else the first.
                var bySave = items.Where(i => i.Saves > 0).GroupBy(i => i.Saves)
                    .ToDictionary(g => g.Key, g => g.FirstOrDefault(i => i.Key == "live") ?? g.FirstOrDefault(i => i.Text.StartsWith(Tick)) ?? g.First());
                int before = items.Count;
                items = items.Where(i => i.Saves <= 0 || bySave[i.Saves] == i).ToList();
                if (items.Count != before && !_versionDupesLogged)
                {
                    _versionDupesLogged = true;
                    Log.Info($"[Federator] version pickers: {before - items.Count} duplicate entr{(before - items.Count == 1 ? "y" : "ies")} of the same save hidden");
                }

                // Cloud list not there yet: why, and what picking the entry does.
                if (cloudLink && cloudList == null)
                {
                    _cloudState.TryGetValue(link.Uid, out var state);
                    if (!ApsConfig.Load().IsSet)
                        items.Add(new VersionItem { Action = VersionAction.SetUpCloud, Key = "cloud-setup", Text = NoTick + "  ☁  Set up Autodesk access to list cloud versions…" });
                    else if (state == "loading")
                        items.Add(new VersionItem { Action = VersionAction.Loading, Key = "cloud-loading", Text = NoTick + "  ☁  Loading cloud versions…" });
                    else if (state == null && ApsAuth.HasSession)
                    {
                        FetchCloudVersions(link, interactive: false);   // sets "loading" first, so no loop
                        items.Add(new VersionItem { Action = VersionAction.Loading, Key = "cloud-loading", Text = NoTick + "  ☁  Loading cloud versions…" });
                    }
                    else if (state == null || state == "signin")
                        items.Add(new VersionItem { Action = VersionAction.SignIn, Key = "cloud-signin", Text = NoTick + "  ☁  Sign in to Autodesk to list cloud versions…" });
                    else
                        items.Add(new VersionItem { Action = VersionAction.Retry, Key = "cloud-retry", Text = NoTick + "  ☁  Couldn't list cloud versions: retry" });
                }
            }
            items.Add(new VersionItem { Action = VersionAction.Browse, Key = "browse", Text = NoTick + "  Browse for a file…" });

            _loading = true;
            foreach (var box in new[] { _fromBox, _toBox })
            {
                box.Items.Clear();
                foreach (var it in items) box.Items.Add(it);
            }
            var real = items.Where(i => !i.Browse).ToList();
            var to = real.FirstOrDefault(i => i.Key == keepTo) ?? real.FirstOrDefault(i => i.Key == "live") ?? real.FirstOrDefault();
            var from = real.FirstOrDefault(i => i.Key == keepFrom && i != to)
                       ?? real.FirstOrDefault(i => i != to && i.Text.StartsWith(Tick)) ?? real.FirstOrDefault(i => i != to);
            _toBox.SelectedItem = to;
            _fromBox.SelectedItem = from;
            _lastFrom = from; _lastTo = to;
            _loading = false;
            UpdateCompareEnabled();
        }

        /// <summary>Version archive settings: on / off, the Perseus archive root (all projects), versions kept per link.</summary>
        private void EditArchiveSettings()
        {
            var cfg = LinkArchive.LoadSettings();
            var enabled = new CheckBox { Content = "Archive each new version of a link when Revit loads it", IsChecked = cfg.Enabled, Margin = new Thickness(0, 0, 0, 10) };
            var folder = new TextBox { Text = cfg.Folder ?? "", Padding = new Thickness(3), Width = 380 };
            var browse = MakeButton("Browse…", "Pick the Perseus archive root");
            browse.Margin = new Thickness(6, 0, 0, 0);
            browse.Click += (s2, e2) =>
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Perseus archive root (all projects)",
                                                                                 SelectedPath = string.IsNullOrWhiteSpace(folder.Text) ? cfg.Root : folder.Text })
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dlg.SelectedPath;
            };
            var keep = new TextBox { Text = cfg.KeepPerLink.ToString(CultureInfo.CurrentCulture), Width = 50, Padding = new Thickness(3) };
            var open = LinkButton("Open this project's archive folder", "Show the archived versions in Explorer");
            open.Click += (s2, e2) => { try { Directory.CreateDirectory(_archiveRoot); System.Diagnostics.Process.Start("explorer.exe", _archiveRoot); } catch { } };
            var ok = MakeButton("Save", "Save the archive settings for this Windows user");
            ok.IsDefault = true;
            ok.Margin = new Thickness(0, 12, 0, 0);
            string fallback = !string.IsNullOrWhiteSpace(cfg.MachineFolder) ? cfg.MachineFolder + "  (set for this computer by IT)" : LinkArchive.DefaultFolder;

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Width = 470, Margin = new Thickness(0, 0, 0, 10),
                Text = "When Revit loads a new version of a link (opening the model, Reload Latest), Perseus copies the file Revit loaded " +
                       "(for cloud links Revit's own local copy) into the archive in the background. Archived versions appear in From / To " +
                       "and are compared without downloading anything. Versions are only captured when someone loads them.\n\n" +
                       "One root folder serves every project: each project gets its own subfolder automatically. Use a shared network " +
                       "folder so everyone's Revit adds to one history.",
            });
            panel.Children.Add(enabled);
            panel.Children.Add(Label("Perseus archive root (all projects)"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(folder);
            row.Children.Add(browse);
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock { Text = "Empty = " + fallback, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 470 });
            if (_doc != null)
                panel.Children.Add(new TextBlock { Text = "This project: " + _archiveRoot, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 470, Margin = new Thickness(0, 2, 0, 8) });
            var keepRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            keepRow.Children.Add(new TextBlock { Text = "Keep the newest ", VerticalAlignment = VerticalAlignment.Center });
            keepRow.Children.Add(keep);
            keepRow.Children.Add(new TextBlock { Text = " versions of each link (about 100–300 MB each)", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(keepRow);
            panel.Children.Add(new ContentControl { Content = open, Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(ok);

            var win = new Window { Title = "Version archive", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                                   ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                   FontFamily = FontFamily, FontSize = FontSize, ShowInTaskbar = false };
            ok.Click += (s2, e2) => win.DialogResult = true;
            if (win.ShowDialog() != true) return;
            cfg.Enabled = enabled.IsChecked == true;
            cfg.Folder = string.IsNullOrWhiteSpace(folder.Text) ? null : folder.Text.Trim();
            if (int.TryParse(keep.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n > 0) cfg.KeepPerLink = n;
            try
            {
                Directory.CreateDirectory(cfg.Root);
                LinkArchive.SaveSettings(cfg);
            }
            catch (Exception ex) { Status("Couldn't use that folder: " + ex.Message, true); return; }
            RunApi(app =>
            {
                _archiveRoot = LinkArchive.RootFor(_doc);
                RefillVersions();
                Status(cfg.Enabled ? $"Version archive: {_archiveRoot} (newest {cfg.KeepPerLink} per link)." : "Version archive turned off.");
            });
        }

        private static string CloudLabel(CloudVersion v) =>
            $"☁ v{v.Number} · {v.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}" + (string.IsNullOrEmpty(v.User) ? "" : " · " + v.User);

        private void RefillVersions() =>
            FillVersions((_fromBox.SelectedItem as VersionItem)?.Key, (_toBox.SelectedItem as VersionItem)?.Key);

        /// <summary>Lists the link's cloud versions on a background thread (interactive: browser sign-in if needed).</summary>
        private void FetchCloudVersions(LinkItem link, bool interactive)
        {
            if (link == null || !link.IsCloud) return;
            string uid = link.Uid, projectId = link.CloudProjectId, urn = link.CloudItemUrn;
            Guid pg = link.CloudProjectGuid, mg = link.CloudModelGuid;
            string fileName = link.Name.Split(new[] { "  #" }, StringSplitOptions.None)[0];
            if (!fileName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) fileName += ".rvt";
            _cloudState[uid] = "loading";
            if (interactive) Status("Signing in to Autodesk in your browser…");
            Task.Run(async () =>
            {
                try
                {
                    // Signed in? (interactive: browser if needed)
                    if (await ApsAuth.GetTokenAsync(interactive, CancellationToken.None).ConfigureAwait(false) == null)
                    {
                        await Dispatcher.InvokeAsync(() => Safe(() => { _cloudState[uid] = "signin"; if (SelectedLink?.Uid == uid) RefillVersions(); }));
                        return;
                    }
                    if (urn == null)
                    {
                        var ids = await ApsClient.ResolveItemAsync(pg, mg, fileName, CancellationToken.None).ConfigureAwait(false);
                        projectId = ids.Item1; urn = ids.Item2;
                    }
                    var list = await ApsClient.ListVersionsAsync(projectId, urn, false, CancellationToken.None).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() => Safe(() =>
                    {
                        link.CloudProjectId = projectId; link.CloudItemUrn = urn;   // for downloads
                        if (list == null) _cloudState[uid] = "signin";
                        else { _cloudVersions[uid] = list; _cloudState.Remove(uid); Status($"{list.Count} cloud version(s) of {link.Name}."); }
                        if (SelectedLink?.Uid == uid) RefillVersions();
                    }));
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] listing cloud versions failed: {ex.Message}");
                    try
                    {
                        await Dispatcher.InvokeAsync(() => Safe(() =>
                        {
                            _cloudState[uid] = ex.Message;
                            Status("Couldn't list cloud versions: " + ex.Message, true);
                            if (SelectedLink?.Uid == uid) RefillVersions();
                        }));
                    }
                    catch { /* window closed */ }
                }
            });
        }

        /// <summary>Asks for the APS app's client ID (once per user); returns true when one is set.</summary>
        private bool SetUpCloudAccess()
        {
            var cfg = ApsConfig.Load();
            var box = new TextBox { Text = cfg.ClientId ?? "", Margin = new Thickness(0, 6, 0, 10), Padding = new Thickness(3) };
            var ok = MakeButton("Save", "Save the client ID for this Windows user");
            ok.IsDefault = true;
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Width = 460,
                Text = "Listing and downloading cloud versions goes through Autodesk Platform Services (APS).\n\n" +
                       "1. At aps.autodesk.com create an app of type \"Desktop, Mobile, Single-Page App\" with the Data Management API.\n" +
                       $"2. Add this callback URL to it: {cfg.CallbackUrl}\n" +
                       "3. On ACC / BIM 360 your account admin may need to add the client ID under Account Admin > Custom Integrations.\n" +
                       "4. Paste the app's client ID below. You'll sign in with your own Autodesk account; nothing is shared.",
            });
            panel.Children.Add(Label("Client ID"));
            panel.Children.Add(box);
            panel.Children.Add(ok);
            var dlg = new Window { Title = "Autodesk cloud versions", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                                   ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                                   FontFamily = FontFamily, FontSize = FontSize, ShowInTaskbar = false };
            ok.Click += (s2, e2) => dlg.DialogResult = true;
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Text)) return false;
            cfg.ClientId = box.Text.Trim();
            cfg.Save();
            ApsAuth.SignOut();   // a new app: the old app's tokens don't apply
            return true;
        }

        private static string VersionLabel(LinkVersion v)
        {
            string saves = v.Saves > 0 ? $"save {v.Saves}" : "";
            if (v.CloudVersionNumber > 0)
                return $"☁ v{v.CloudVersionNumber} · {v.SavedUtc.ToLocalTime():d MMM yyyy HH:mm}";
            if (v.FromLiveLink)
                return (saves.Length > 0 ? saves + " · " : "") + $"was loaded {v.SavedUtc.ToLocalTime():d MMM yyyy HH:mm}";
            return $"{Path.GetFileName(v.SourcePath)} · saved {v.SavedUtc.ToLocalTime():d MMM yyyy HH:mm}" + (saves.Length > 0 ? " · " + saves : "");
        }

        private static bool SameGuid(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p ?? ""; }
        }

        private void OnVersionPicked(ComboBox box)
        {
            if (_loading) return;
            if (box.SelectedItem is VersionItem act && act.Action != VersionAction.None && act.Action != VersionAction.Browse)
            {
                _loading = true;
                box.SelectedItem = box == _fromBox ? _lastFrom : _lastTo;
                _loading = false;
                var link = SelectedLink;
                switch (act.Action)
                {
                    case VersionAction.SetUpCloud:
                        if (SetUpCloudAccess()) { FetchCloudVersions(link, interactive: true); RefillVersions(); }
                        break;
                    case VersionAction.SignIn:
                    case VersionAction.Retry:
                        FetchCloudVersions(link, interactive: true);
                        RefillVersions();
                        break;
                }
                UpdateCompareEnabled();
                return;
            }
            if (box.SelectedItem is VersionItem v && v.Browse)
            {
                string path = PickVersionFile();
                var link = SelectedLink;
                if (path == null || link == null)
                {
                    _loading = true;
                    box.SelectedItem = box == _fromBox ? _lastFrom : _lastTo;
                    _loading = false;
                }
                else
                {
                    if (!_browsed.TryGetValue(link.Uid, out var list)) _browsed[link.Uid] = list = new List<string>();
                    list.Remove(path);
                    list.Insert(0, path);
                    string key = "file:" + SafeFull(path).ToLowerInvariant();
                    string otherKey = ((box == _fromBox ? _toBox : _fromBox).SelectedItem as VersionItem)?.Key;
                    if (box == _fromBox) FillVersions(key, otherKey); else FillVersions(otherKey, key);
                    // Already known (cached under the same save)? Then the picker shows that entry instead.
                    return;
                }
            }
            if (box == _fromBox) _lastFrom = box.SelectedItem; else _lastTo = box.SelectedItem;
            UpdateCompareEnabled();
        }

        private string PickVersionFile()
        {
            var link = SelectedLink;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Pick a version of {link?.Name}",
                Filter = "Revit models (*.rvt)|*.rvt",
                CheckFileExists = true,
            };
            try
            {
                string dir = string.IsNullOrEmpty(link?.Path) ? null : Path.GetDirectoryName(link.Path);
                if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            catch { /* cloud / odd path */ }
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        private void UpdateCompareEnabled()
        {
            var link = SelectedLink;
            var from = _fromBox.SelectedItem as VersionItem;
            var to = _toBox.SelectedItem as VersionItem;
            bool needsLive = from?.Key == "live" || to?.Key == "live";
            _compareButton.IsEnabled = link != null && from != null && to != null && !from.Browse && !to.Browse
                                       && from.Key != to.Key && (!needsLive || link.Loaded);
        }

        // ── Compare ──

        private void CompareVersions()
        {
            var link = SelectedLink;
            var from = _fromBox.SelectedItem as VersionItem;
            var to = _toBox.SelectedItem as VersionItem;
            if (link == null || from?.Source == null || to?.Source == null) { Status("Pick a link and two versions."); return; }
            if (from.Key == to.Key) { Status("From and To are the same version."); return; }

            _compareButton.IsEnabled = false;
            if (from.Source.Kind == ComparisonRunner.Source.SourceKind.Cloud || to.Source.Kind == ComparisonRunner.Source.SourceKind.Cloud)
            {
                DownloadThenCompare(link, from, to);
                return;
            }
            RunComparison(link, from, to, from.Source, to.Source, null);
        }

        /// <summary>Downloads the chosen cloud versions on a background thread, then compares them as files.</summary>
        private void DownloadThenCompare(LinkItem link, VersionItem from, VersionItem to)
        {
            string projectId = link.CloudProjectId;
            Task.Run(async () =>
            {
                var downloads = new List<string>();
                try
                {
                    async Task<ComparisonRunner.Source> Get(ComparisonRunner.Source src)
                    {
                        if (src.Kind != ComparisonRunner.Source.SourceKind.Cloud) return src;
                        var cv = src.Cloud;
                        string title = $"Downloading {link.Name} v{cv.Number}";
                        SyncStatusToast.Progress(title, -1, "Asking Autodesk for the file…");
                        var progress = new Progress<double>(f => SyncStatusToast.Progress(title, f, f < 0 ? "Downloading…" : $"Downloading… {f:P0}"));
                        string path = await ApsClient.DownloadAsync(projectId, cv, progress, CancellationToken.None).ConfigureAwait(false);
                        downloads.Add(path);
                        return ComparisonRunner.Source.Downloaded(path, cv, src.Label);
                    }
                    var f2 = await Get(from.Source).ConfigureAwait(false);
                    var t2 = await Get(to.Source).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() => Safe(() => RunComparison(link, from, to, f2, t2, downloads)));
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] cloud download failed: {ex}");
                    foreach (var d in downloads) ApsClient.Discard(d);
                    SyncStatusToast.Hide();
                    try
                    {
                        await Dispatcher.InvokeAsync(() => Safe(() =>
                        {
                            Status("Download failed: " + ex.Message, true);
                            UpdateCompareEnabled();
                        }));
                    }
                    catch { /* window closed */ }
                }
            });
        }

        private void RunComparison(LinkItem link, VersionItem from, VersionItem to, ComparisonRunner.Source fromSrc,
                                   ComparisonRunner.Source toSrc, List<string> downloads)
        {
            Status($"Comparing {from.Source.Label} → {to.Source.Label}… Revit is busy until it finishes.");
            RunApi(app =>
            {
                try
                {
                    var inst = LinkedElementRef.FindInstance(_doc, link.Uid);
                    if (inst == null) { Status("That link is no longer in the model. Press Refresh."); return; }
                    var result = ComparisonRunner.Run(app, _doc, inst, link.ModelKey, fromSrc, toSrc);
                    if (result.Warning != null &&
                        MessageBox.Show(this, result.Warning + "\n\nKeep this comparison?", "Federator",
                                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    {
                        ComparisonStore.Delete(_doc, result.Record.Id, _me, out _);
                        Status("Comparison discarded.");
                        FillVersions(from.Key, to.Key);   // the snapshots are cached now (✓)
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
                finally
                {
                    // Downloaded versions are cached as snapshots now (✓); the .rvt isn't needed any more.
                    if (downloads != null) foreach (var d in downloads) ApsClient.Discard(d);
                    if (downloads != null && downloads.Count > 0) RefillVersions();
                    UpdateCompareEnabled();
                }
            });
        }

        private void DeleteComparison()
        {
            var rec = _record;
            if (rec == null) return;
            if (MessageBox.Show(this, $"Delete the comparison {rec.OldLabel ?? rec.OldTitle} → {rec.NewLabel ?? "Live"}?\nReview comments and actions are kept.",
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
                Status(ReviewNavigator.Show(app.ActiveUIDocument, inst, rows.Select(r => r.Change).ToList(), select, _reviewViews));
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
            _detail.Children.Add(Muted_($"From: {_record.OldLabel ?? _record.OldTitle} ({_record.OldPath})"));
            _detail.Children.Add(Muted_($"To: {_record.NewLabel ?? "live link"} ({_record.NewPath ?? _record.LinkPath})"));
        }

        private void BuildSingle(ChangeRow row)
        {
            var c = row.Change;
            var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            title.Inlines.Add(new System.Windows.Documents.Run(row.KindGlyph + " ") { Foreground = row.KindBrush });
            title.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(row.Element) ? c.Category : row.Element));
            _detail.Children.Add(title);
            _detail.Children.Add(Muted_($"{c.Category} · Id {c.Id} · {(string.IsNullOrEmpty(c.Level) ? "no level" : c.Level)} · {c.Kind}"));
            var thumbRows = new List<ChangeRow> { row };
            var bestSuggestion = SuggestionFor(row);
            if (bestSuggestion != null) thumbRows.Add(bestSuggestion.Replacement);
            var thumb = Thumbnail(thumbRows, 380, 230, "Click to zoom to it in Revit" + (bestSuggestion != null ? " (green = its suggested replacement)" : ""));
            if (thumb != null) { thumb.Margin = new Thickness(0, 6, 0, 2); _detail.Children.Add(thumb); }

            _detail.Children.Add(Section("What changed"));
            var shown = row.Shown;
            if (row.IsNoise) _detail.Children.Add(Muted_(NoiseFilter.IsNoiseCategory(c, _noise)
                ? $"Noise: {c.Category} is marked as a noise category for this link."
                : "Only noise: everything that changed is covered by this link's noise rules."));
            else foreach (var line in ChangeDescriber.Lines(shown)) _detail.Children.Add(Text(line));
            var noiseParams = NoiseFilter.NoiseParameters(c, NoiseFilter.ParameterSet(_noise));
            bool smallMove = c.Aspects.HasFlag(ChangeAspect.Moved) && !shown.Aspects.HasFlag(ChangeAspect.Moved) && !row.IsNoise;
            if (noiseParams.Count > 0 || smallMove)
                _detail.Children.Add(Muted_("Ignored as noise: " + string.Join(", ", noiseParams.Concat(smallMove ? new[] { "move of " + ChangeDescriber.Length(c.MoveDistance) } : new string[0]))));

            var tableParams = row.IsNoise ? c.ParameterChanges : shown.ParameterChanges;
            if (tableParams != null && tableParams.Count > 0) _detail.Children.Add(ParameterTable(tableParams));

            if (c.Kind == ChangeKind.Replaced) BuildReplacedSection(row);
            if (c.Kind == ChangeKind.Deleted)
            {
                bool rejected = _replacementLinks.Rejected.Contains(c.UniqueId ?? "");
                _detail.Children.Add(Section("Replacement"));
                _detail.Children.Add(Muted_(rejected ? "Marked as not a replacement (or its link was broken)."
                                                     : "If the consultant deleted this element and modelled a new one instead, link them: they become one Replaced change."));
                var replaceRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
                var linkB = MakeButton("Link to replacement…", "Pick the new element: in the list, by its badge in the view, or in Revit.");
                linkB.Click += (s, e) => Safe(() => StartLinkPick(row));
                replaceRow.Children.Add(linkB);
                if (!rejected && c.Replacements != null && c.Replacements.Count > 0)
                {
                    var notB = MakeButton("Not a replacement", "Stop suggesting replacements for this deleted element.");
                    notB.Click += (s, e) => Safe(() => RejectReplacements(new List<ChangeRow> { row }));
                    replaceRow.Children.Add(notB);
                }
                _detail.Children.Add(replaceRow);
            }
            if (c.Kind == ChangeKind.Deleted && c.Replacements != null && c.Replacements.Count > 0)
            {
                _detail.Children.Add(Section("Possibly replaced by"));
                foreach (var cand in c.Replacements)
                {
                    bool canLink = _rowByUid.TryGetValue(cand.UniqueId ?? "", out var candRow) && candRow.Change.Kind == ChangeKind.Added;
                    var target = candRow;
                    _detail.Children.Add(CandidateLink(cand.UniqueId, $"{ChangeDescriber.TypeName(cand.Family, cand.Type)} · Id {cand.Id}",
                        $"{cand.Reason} · {cand.Score * 100:0} % match" + (canLink ? "" : " · already linked to another element"),
                        canLink ? (Action)(() => LinkPair(row, target.Change.UniqueId, target.Change.Id)) : null));
                }
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
                    var o2 = old;
                    _detail.Children.Add(CandidateLink(old.Change.UniqueId, $"{old.Element} · Id {old.Change.Id} (deleted)",
                                                       $"{cand.Reason} · {cand.Score * 100:0} % match",
                                                       old.Change.Kind == ChangeKind.Deleted ? (Action)(() => LinkPair(o2, c.UniqueId, c.Id)) : null));
                }
            }
            if (c.Kind == ChangeKind.Added)
            {
                var linkB = MakeButton("Link as replacement of…", "This element replaces a deleted one: pick the deleted element in the list or by its badge.");
                linkB.HorizontalAlignment = HorizontalAlignment.Left;
                linkB.Margin = new Thickness(0, 6, 0, 0);
                linkB.Click += (s, e) => Safe(() => StartLinkPick(row));
                _detail.Children.Add(linkB);
            }

            BuildReview(new List<ChangeRow> { row });
            BuildThread(row);
        }

        private void BuildReplacedSection(ChangeRow row)
        {
            var c = row.Change;
            _detail.Children.Add(Section("Replacement"));
            _replacementLinks.ByOld.TryGetValue(c.OldUniqueId ?? "", out var link);
            _detail.Children.Add(Text($"Id {c.OldId} (deleted)  ⇄  Id {c.Id} (new)", ChangeRow.ReplacedBrush));
            if (link != null) _detail.Children.Add(Muted_($"Linked by {link.Author}, {link.Utc.ToLocalTime():d MMM yyyy HH:mm}. " +
                                                          "References to the old element can be moved to the new one."));
            var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var breakB = MakeButton("Break link", "Not the same element: show the deleted and added elements separately again.");
            breakB.Click += (s, e) => Safe(() => RejectReplacements(new List<ChangeRow> { row }));
            var showB = MakeButton("Show old and new", "Zoom to the old position and the new element.");
            showB.Click += (s, e) => Safe(() => ShowRows(new List<ChangeRow> { row }, true));
            buttons.Children.Add(breakB);
            buttons.Children.Add(showB);
            _detail.Children.Add(buttons);
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
                if (e.ReplacementOp == ReplacementLinks.OpLink) stack.Children.Add(Text($"⇄ Linked: Id {e.LinkedElementId} replaced by Id {e.ReplacedById}", ChangeRow.ReplacedBrush));
                if (e.ReplacementOp == ReplacementLinks.OpReject) stack.Children.Add(Text($"Id {e.LinkedElementId}: not a replacement / link broken"));
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

        private UIElement CandidateLink(string uniqueId, string text, string detail, Action linkIt = null)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            var link = LinkButton(text, "Go to this element in the list");
            link.Click += (s, e) => Safe(() => SelectRow(uniqueId));
            if (linkIt != null)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(link);
                var confirm = LinkButton("⇄ Link", "Confirm: the deleted element was replaced by this one (one Replaced change).");
                confirm.Margin = new Thickness(10, 0, 0, 0);
                confirm.Click += (s, e) => Safe(linkIt);
                line.Children.Add(confirm);
                panel.Children.Add(line);
            }
            else panel.Children.Add(link);
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

        // ═══════════════════════════ Review views + images ═══════════════════════════
        // Review views (federator/ReviewViews) are the go-to views for showing changes; each change is assigned to the
        // first one showing it, and that view's captured image (federator/ViewImageCapture, ui/ViewImageStore) gives the
        // thumbnails in the detail pane and the Replacements window.

        internal bool HasViewImages => _frames.Values.Any(f => f != null);

        private void OpenReviewViews()
        {
            if (_doc == null) { Status("No model is open."); return; }
            RunApi(app =>
            {
                var candidates = ReviewViews.Candidates(_doc);
                var current = ReviewViews.Load(_doc);
                string active = app.ActiveUIDocument?.Document?.Equals(_doc) == true ? app.ActiveUIDocument.ActiveView?.UniqueId : null;
                var dlg = new ReviewViewsWindow(candidates, current, active) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Result == null) return;
                dlg.Result.MaxPixels = current.MaxPixels;
                FederatorSettingsStore.Save(_doc, ReviewViews.Key, dlg.Result, _me);
                _reviewViews = dlg.Result;
                Status($"{_reviewViews.ViewUids.Count} review view(s) saved.");
                if (_record != null) AssignReviewViews(_record, dlg.RefreshNow || _reviewViews.AutoCapture, dlg.RefreshNow);
            });
        }

        private void HostBoxOf(ElementChange c, bool old, out double[] min, out double[] max)
        {
            min = max = null;
            var a = old ? c.OldMin : c.NewMin;
            var b = old ? c.OldMax : c.NewMax;
            if (a == null || b == null) return;
            _toHost.Box(a, b, out min, out max);
        }

        /// <summary>Host box a change is shown by: old position for deleted (and replaced without a new box), else new.</summary>
        private void ShownBoxOf(ElementChange c, out double[] min, out double[] max)
        {
            bool old = c.Kind == ChangeKind.Deleted || c.NewMin == null;
            HostBoxOf(c, old, out min, out max);
        }

        /// <summary>
        /// API context: assigns each change to a review view, loads cached images and (capture) queues images that are
        /// missing or out of date (link version, crop or area changed, older than 30 days); force = recapture all used.
        /// </summary>
        private void AssignReviewViews(ComparisonRecord rec, bool capture, bool force)
        {
            _viewOf.Clear();
            var views = ReviewViews.Resolve(_doc, _reviewViews);
            if (views.Count == 0 || rec == null) { ImagesChanged?.Invoke(); return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var inst = LinkedElementRef.FindInstance(_doc, rec.LinkInstanceUid);
            var targets = ReviewViews.Targets(_doc, views, inst);
            var boxesByView = new Dictionary<string, List<KeyValuePair<double[], double[]>>>(StringComparer.Ordinal);
            foreach (var r in _rows)
            {
                ShownBoxOf(r.Change, out var min, out var max);
                if (min == null) continue;
                var t = ReviewViews.ViewFor(targets, new ARDB.XYZ(min[0], min[1], min[2]), new ARDB.XYZ(max[0], max[1], max[2]));
                if (t == null) continue;
                string uid = t.View.UniqueId;
                if (!string.IsNullOrEmpty(r.Change.UniqueId)) _viewOf[r.Change.UniqueId] = uid;
                if (!boxesByView.TryGetValue(uid, out var list)) boxesByView[uid] = list = new List<KeyValuePair<double[], double[]>>();
                list.Add(new KeyValuePair<double[], double[]>(min, max));
            }
            string liveGuid = _links.FirstOrDefault(l => l.Uid == rec.LinkInstanceUid)?.LiveGuid ?? "";
            var pending = new List<Tuple<ARDB.View, string>>();   // view, why it needs an image
            foreach (var v in views)
            {
                if (!boxesByView.TryGetValue(v.UniqueId, out var boxes)) continue;
                var frame = ViewImageStore.Load(_imageModel, v.UniqueId);
                _frames[v.UniqueId] = frame;
                if (!capture || _captureQueue.Contains(v.UniqueId)) continue;
                string why = force ? "recapture requested"
                    : frame == null ? "no image yet"
                    : (DateTime.UtcNow - frame.CapturedUtc).TotalDays > 30 ? "image older than 30 days"
                    : frame.LinkVersions != null && frame.LinkVersions.TryGetValue(rec.LinkInstanceUid, out var g) && g != liveGuid ? "the link has a newer version"
                    : v.CropBoxActive && frame.CropSignature != ViewImageCapture.CropSignature(v) ? "the view's crop region changed"
                    : !v.CropBoxActive && boxes.Count(b => !frame.Covers(b.Key, b.Value)) > boxes.Count / 50 ? "changes outside the current image"
                    : null;
                if (why != null && (force || !_captureDeclined.Contains(v.UniqueId + "|" + why))) pending.Add(Tuple.Create(v, why));
            }
            int queued = 0;
            if (pending.Count > 0 && (force || AskToCapture(pending)))
                foreach (var p in pending) { _captureQueue.Enqueue(p.Item1.UniqueId); queued++; }
            else foreach (var p in pending) _captureDeclined.Add(p.Item1.UniqueId + "|" + p.Item2);
            Log.Info($"[Federator] review views: {_viewOf.Count:N0} of {_rows.Count:N0} changes in {boxesByView.Count} of {views.Count} view(s), " +
                     $"{queued} image(s) to capture, {clock.ElapsedMilliseconds} ms");
            ImagesChanged?.Invoke();
            if (queued > 0 && !_capturing)
            {
                _captureTotal = _captureQueue.Count;
                _captureDone = 0;
                CaptureNext();
            }
        }

        /// <summary>Asks before (re)capturing view images: it's slow and Revit is busy meanwhile.</summary>
        private bool AskToCapture(List<Tuple<ARDB.View, string>> pending)
        {
            double avg = ViewImageStore.AverageCaptureSeconds(_imageModel);
            string speed = avg > 0
                ? $"On this model a view has taken about {Duration(avg)} so far, so this is roughly {Duration(avg * pending.Count)} in total."
                : "This is the first capture on this model: expect anything from 10 seconds to a few minutes per view on a large model with many links.";
            string list = string.Join("\n", pending.Take(15).Select(p => $"  • {p.Item1.Name} ({p.Item2})")) + (pending.Count > 15 ? $"\n  … and {pending.Count - 15} more" : "");
            var answer = MessageBox.Show(this,
                $"Perseus can capture images of {pending.Count} review view(s), so changes and replacements show as pictures in the panel instead of navigating Revit:\n\n" +
                $"{list}\n\n" +
                $"This is SLOW: Revit exports each view (with everything linked into it) and you can't work in Revit until each view is done. {speed}\n\n" +
                "Capture now? (No = not this session; Review views… > Save and capture images now does it later.)",
                "Capture review view images", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) Status("Review view images not captured: pictures are missing or out of date for some changes.");
            return answer == MessageBoxResult.Yes;
        }

        private static string Duration(double seconds) =>
            seconds < 90 ? $"{Math.Max(1, Math.Round(seconds)):0} s" : $"{seconds / 60:0.#} min";

        private void CaptureNext()
        {
            if (_captureQueue.Count == 0)
            {
                _capturing = false;
                SyncStatusToast.Hide();
                Status(_captureTotal > 0 ? $"Captured {_captureDone} review view image(s)." : "");
                return;
            }
            _capturing = true;
            string uid = _captureQueue.Dequeue();
            var rec = _record;
            RunApi(app =>
            {
                ViewImageCapture.Result result = null;
                try
                {
                    var view = _doc.GetElement(uid) as ARDB.View;
                    if (view != null && rec != null)
                    {
                        string label = $"Capturing review view images ({_captureDone + 1} of {_captureTotal}): {view.Name}";
                        Status(label + "… Revit is busy meanwhile.");
                        SyncStatusToast.Progress("Capturing review view images", (double)_captureDone / Math.Max(1, _captureTotal), view.Name);
                        var boxes = new List<KeyValuePair<double[], double[]>>();
                        foreach (var r in _rows)
                        {
                            if (!_viewOf.TryGetValue(r.Change.UniqueId ?? "", out var v) || v != uid) continue;
                            ShownBoxOf(r.Change, out var min, out var max);
                            if (min != null) boxes.Add(new KeyValuePair<double[], double[]>(min, max));
                        }
                        var started = DateTime.UtcNow;
                        result = ViewImageCapture.Capture(_doc, view, boxes, _reviewViews.MaxPixels, ViewImageStore.TempDir);
                        if (result != null) ViewImageStore.RecordCaptureSeconds(_imageModel, (DateTime.UtcNow - started).TotalSeconds);
                        if (result == null) Log.Info($"[Federator] review view '{view.Name}': no image captured");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Federator] review view image failed: {ex.Message}");
                    Status("A review view image failed: " + ex.Message, true);
                }
                _captureDone++;
                if (result == null) { CaptureNext(); return; }
                ViewImageStore.SaveAsync(_imageModel, result).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => Safe(() =>
                {
                    var frame = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
                    if (frame != null)
                    {
                        _frames[uid] = frame;
                        ImagesChanged?.Invoke();
                        if (_grid.SelectedItems.Count == 1) RebuildDetail();
                    }
                    CaptureNext();
                }))));
            });
        }

        /// <summary>The captured image showing these changes (the first one assigned to a review view with an image), or null.</summary>
        private ViewImageFrame FrameFor(IEnumerable<ChangeRow> rows)
        {
            foreach (var r in rows)
                if (r != null && _viewOf.TryGetValue(r.Change.UniqueId ?? "", out var uid) && _frames.TryGetValue(uid, out var f) && f != null)
                    return f;
            return null;
        }

        private static readonly Brush GhostBrush = Frozen(0x80, 0x86, 0x91);

        private List<ChangeThumbnail.Mark> MarksFor(ChangeRow r)
        {
            var c = r.Change;
            var marks = new List<ChangeThumbnail.Mark>();
            void Add(bool old, Brush brush, bool dashed)
            {
                HostBoxOf(c, old, out var min, out var max);
                if (min != null) marks.Add(new ChangeThumbnail.Mark { Min = min, Max = max, Brush = brush, Dashed = dashed });
            }
            switch (c.Kind)
            {
                case ChangeKind.Deleted: Add(true, ChangeRow.DeletedBrush, true); break;
                case ChangeKind.Added: Add(false, ChangeRow.AddedBrush, false); break;
                case ChangeKind.Replaced: Add(true, ChangeRow.DeletedBrush, true); Add(false, ChangeRow.ReplacedBrush, false); break;
                default:
                    if (c.Aspects.HasFlag(ChangeAspect.Moved)) Add(true, GhostBrush, true);
                    Add(false, ChangeRow.ModifiedBrush, false);
                    break;
            }
            return marks;
        }

        /// <summary>A thumbnail of these changes on their review view image (click = zoom in Revit), or null when there's no image.</summary>
        internal FrameworkElement Thumbnail(IList<ChangeRow> rows, double width, double height, string tip = null)
        {
            var frame = FrameFor(rows);
            if (frame == null) return null;
            var marks = rows.Where(r => r != null).SelectMany(MarksFor).ToList();
            var list = rows.Where(r => r != null).ToList();
            return ChangeThumbnail.Build(frame, marks, width, height, tip, () => Safe(() => ShowRows(list, true)));
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

        // Tick list of the distinct values (empty -> "(none)", listed last); unticked values stay unticked.
        private static void Refill(MultiPick box, IEnumerable<string> values)
        {
            var keys = values.Select(ChangeFilter.OrNone).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(v => v == ChangeFilter.NoValue ? 1 : 0)
                             .ThenBy(v => v, StringComparer.OrdinalIgnoreCase);
            box.SetItems(keys.Select(v => new MultiPick.Item(v)));
        }

        private static MultiPick Pick(Panel parent, string caption, double width)
        {
            var box = new MultiPick(width);
            parent.Children.Add(Captioned(caption, box));
            return box;
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
