using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Modeless "Family mapping" (ribbon: Federator > Family Mapping, commands/ShowFamilyMappingCommand), per consultant link
    /// (picker at the top; triage suggestions come from the comparison open in ui/FederatorWindow): consultant family types on the
    /// left (count in the link, what triage says - mapping/TypeTriage - and the mapping), the mapping of the selected type
    /// on the right: our family type, 1:1 offset (mm, consultant element axes) and rotation (° about its X / Y / Z),
    /// workset. Category filter (hidden categories saved with the mapping). Edits save automatically (debounced; flushed
    /// on switching type / closing). "Test on one" places a single TEMPORARY family at a sample consultant element and
    /// re-places it live while the offset / rotation / type are tuned; it's deleted when moving on and when the window
    /// closes (with any empty Perseus work planes). Placing / linking families for real is the next phase's panel
    /// (mapping/FamilyPlacer.PlaceAll / LinkExisting). Model work runs through queue/OwnershipUiEvent.
    /// </summary>
    internal class FamilyMappingWindow : Window
    {
        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);
        private static readonly Brush Good = Frozen(0x2E, 0xAD, 0x4B);
        private static readonly Brush Bad = Frozen(0xC0, 0x26, 0x26);
        private const string ActiveWorkset = "(active workset)";

        internal static FamilyMappingWindow Instance { get; private set; }

        private readonly ARDB.Document _doc;
        private readonly string _me;
        private string _linkUid, _modelKey, _linkName;
        private Dictionary<string, TypeTriage> _triage = new Dictionary<string, TypeTriage>();
        private IDictionary<string, string> _replacementMap = new Dictionary<string, string>();
        private readonly ComboBox _linkBox;

        private class LinkChoice
        {
            internal string Uid, Name, ModelKey, BaseName;
            internal bool Loaded = true;
            internal int Mapped;
            public override string ToString() =>
                Loaded ? Name : Name + (Uid == null ? "  (not linked here" : "  (not loaded") + $" · {Mapped} mapped)";
        }

        /// <summary>
        /// A name for a consultant model known only by its key (no link instance in this model): the name saved with the
        /// mapping, else a link type still in this model, else a cached / archived version's title, else the file name /
        /// a short cloud id. API context.
        /// </summary>
        private static string NameOfKey(ARDB.Document doc, string key, FamilyMapping mapping)
        {
            if (!string.IsNullOrWhiteSpace(mapping?.ModelName)) return mapping.ModelName;
            try
            {
                foreach (ARDB.RevitLinkType lt in new ARDB.FilteredElementCollector(doc).OfClass(typeof(ARDB.RevitLinkType)))
                {
                    if (!ARDB.ExternalFileUtils.IsExternalFileReference(doc, lt.Id)) continue;
                    var mp = ARDB.ExternalFileUtils.GetExternalFileReference(doc, lt.Id)?.GetAbsolutePath();
                    if (mp == null) continue;
                    string k = mp.CloudPath ? "cloud|" + mp.GetModelGUID().ToString("N")
                                            : "file|" + System.IO.Path.GetFullPath(ARDB.ModelPathUtils.ConvertModelPathToUserVisiblePath(mp)).ToLowerInvariant();
                    if (k == key) return lt.Name;
                }
            }
            catch (Exception ex) { Log.Info($"[Mapping] link types for a model name: {ex.Message}"); }
            var title = VersionCatalog.List(key).Select(v => v.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            if (title != null) return title;
            try
            {
                title = LinkArchive.List(LinkArchive.RootFor(doc), key).Select(e => e.Title).FirstOrDefault(t => !string.IsNullOrEmpty(t));
                if (title != null) return title;
            }
            catch (Exception ex) { Log.Info($"[Mapping] archive for a model name: {ex.Message}"); }
            int bar = key.IndexOf('|');
            string rest = bar < 0 ? key : key.Substring(bar + 1);
            if (key.StartsWith("file|")) return System.IO.Path.GetFileNameWithoutExtension(rest);
            if (key.StartsWith("cloud|")) return "Cloud model " + rest.Substring(0, Math.Min(8, rest.Length));
            return rest;
        }

        /// <summary>Opens the window on the active model (or brings it to the front). API context.</summary>
        internal static void Open(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Family mapping", "Open a project model with consultant links first.");
                return;
            }
            if (Instance != null && Instance.IsLoaded && doc.Equals(Instance._doc)) { Instance.Activate(); return; }
            Instance?.Close();
            var links = new List<LinkChoice>();
            // Mappings saved in this model (by consultant model): unloaded links with one are listed for review, as are mapped
            // consultant models with no link here at all, so you can see what's mapped and which link to load.
            var mapped = new Dictionary<string, int>();
            var mappings = new Dictionary<string, FamilyMapping>();
            foreach (var key in FamilyMapStore.ModelKeys(doc))
            {
                var m = FamilyMapStore.Load(doc, key);
                if (m.Rules.Count > 0) { mapped[key] = m.Rules.Count; mappings[key] = m; }
            }
            foreach (ARDB.RevitLinkInstance inst in new ARDB.FilteredElementCollector(doc).OfClass(typeof(ARDB.RevitLinkInstance)))
            {
                var linkDoc = inst.GetLinkDocument();
                string name = doc.GetElement(inst.GetTypeId())?.Name ?? inst.Name;
                string key = VersionCatalog.ModelKey(doc, inst, linkDoc, name);
                mapped.TryGetValue(key, out int n);
                if (linkDoc == null && n == 0) continue;   // unloaded and nothing mapped: nothing to see
                links.Add(new LinkChoice { Uid = inst.UniqueId, Name = name, BaseName = name, ModelKey = key, Loaded = linkDoc != null, Mapped = n });
            }
            foreach (var kv in mapped.Where(kv => !links.Any(l => l.ModelKey == kv.Key)))
            {
                string name = NameOfKey(doc, kv.Key, mappings[kv.Key]);
                links.Add(new LinkChoice { Uid = null, Name = name, BaseName = name, ModelKey = kv.Key, Loaded = false, Mapped = kv.Value });
            }
            if (links.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Family mapping", "This model has no loaded Revit links to map from, and no saved mappings.");
                return;
            }
            foreach (var g in links.GroupBy(l => l.Name).Where(g => g.Count() > 1)) { int i = 1; foreach (var l in g) l.Name += $"  #{i++}"; }
            // Default: the link open in the federator panel, else the first loaded one. Loaded links first in the list.
            string preferred = FederatorWindow.Instance?.Record?.LinkInstanceUid ?? links.Where(l => l.Loaded).OrderBy(l => l.Name).FirstOrDefault()?.Uid;
            var w = new FamilyMappingWindow(doc, app.Application.Username, links.OrderBy(l => l.Loaded ? 0 : 1).ThenBy(l => l.Name).ToList(), preferred);
            new System.Windows.Interop.WindowInteropHelper(w).Owner = app.MainWindowHandle;
            w.Show();
        }

        private List<FamilyPlacer.SourceType> _sources = new List<FamilyPlacer.SourceType>();
        private List<FamilyPlacer.TargetType> _targets = new List<FamilyPlacer.TargetType>();
        private FamilyMapping _mapping = new FamilyMapping();
        private Dictionary<string, int> _mappedByRule = new Dictionary<string, int>();
        private readonly List<Row> _rows = new List<Row>();

        private readonly DataGrid _grid;
        private readonly TextBox _filter;
        private readonly CheckBox _suggestedOnly;
        private readonly TextBlock _summary, _status, _title, _placementNote;
        private readonly ComboBox _target, _targetCat, _workset;
        private readonly TextBox[] _offset = new TextBox[3], _rotation = new TextBox[3];
        private readonly TextBox _stepMm, _stepDeg, _tolXy, _tolZ;
        private readonly StackPanel _editor;
        private readonly Button _test, _next, _removeTest;
        private readonly DispatcherTimer _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        private readonly MultiPick _categories;
        // Parameter mapping (stage 5): the edited rule's mappings, the two parameter pickers, compatibility + preview.
        private readonly ListBox _paramList;
        private readonly ComboBox _srcParam, _dstParam, _paramMode;
        private readonly TextBlock _paramInfo;
        private ParamDiscovery.SourceParams _srcInfo = new ParamDiscovery.SourceParams();
        private ParamDiscovery.TargetParams _dstInfo = new ParamDiscovery.TargetParams();
        private string _paramsFor, _srcFor;
        private bool _loadingParams;
        private bool _closing;

        private Row _current;
        private FamilyMapRule _editing;
        // The other selected types (multi-select): an edit made in the editor is applied to each of them too.
        private readonly List<KeyValuePair<Row, FamilyMapRule>> _others = new List<KeyValuePair<Row, FamilyMapRule>>();
        private ARDB.ElementId _testId;
        // The "test" is a family the team placed (Pick in model): moves are captured, but Perseus never deletes or re-places it.
        private bool _testIsExisting;
        private string _testSourceUid;       // the consultant element the test stands at (kept while tuning)
        // Where the test was put: consultant frame, the rule's frame and where Revit actually placed it (hand moves are measured from it).
        private mapping.Frame _testSource, _testExpected, _testPlaced;
        // Order the test walks the consultant elements in (◀ / ▶): those visible in the active view first. Per type + view.
        private string _testOrderKey;
        private List<string> _testOrder = new List<string>();
        private int _testInView;
        private TextBlock _testInfo;
        private int _sample;
        private bool _loadingEditor;

        private class Row
        {
            public string Category { get; set; }
            public string Type { get; set; }
            public int Count { get; set; }
            public string Triage { get; set; }
            public string MappedTo { get; set; }
            public string Tolerance { get; set; }
            public string Mapped { get; set; }
            internal FamilyPlacer.SourceType Source;
            internal TypeTriage Stats;
        }

        private FamilyMappingWindow(ARDB.Document doc, string me, List<LinkChoice> links, string preferredLink)
        {
            Instance = this;
            _doc = doc; _me = me;
            Title = "Perseus — Family mapping";
            Width = 1180; Height = 720; MinWidth = 860; MinHeight = 440;
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
                Text = "Map the consultant's family types to local ones: which local type stands for each of theirs, with its offset and rotation. Types people " +
                       "reviewed in triage are suggested; types ignored or marked as noise are not. Tune one type at a time with Test on one (a temporary family, " +
                       "removed when you move on or close). Changes save automatically.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            _filter = new TextBox { Width = 220, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Filter by category, family or type" };
            _suggestedOnly = new CheckBox { Content = "Suggested and mapped only", IsChecked = true, VerticalAlignment = VerticalAlignment.Center,
                                            ToolTip = "Only types triage suggests (reviewed or open changes) and types already mapped." };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            _linkBox = new ComboBox { Width = 300, Margin = new Thickness(0, 0, 14, 0), VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var l in links) _linkBox.Items.Add(l);
            left.Children.Add(new TextBlock { Text = "Link ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            left.Children.Add(_linkBox);
            left.Children.Add(new TextBlock { Text = "Filter ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            left.Children.Add(_filter);
            _categories = new MultiPick(170) { ToolTip = "Consultant categories to list (saved with the mapping, shared)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            left.Children.Add(new TextBlock { Text = "Categories ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted });
            left.Children.Add(_categories);
            left.Children.Add(_suggestedOnly);
            DockPanel.SetDock(left, Dock.Left);
            tools.Children.Add(left);
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            tools.Children.Add(_summary);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 420 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });

            _grid = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush, Background = Brushes.White,
                RowHeaderWidth = 0, CanUserAddRows = false,
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new System.Windows.Data.Binding(nameof(Row.Category)), Width = new DataGridLength(120) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Consultant type", Binding = new System.Windows.Data.Binding(nameof(Row.Type)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "In link", Binding = new System.Windows.Data.Binding(nameof(Row.Count)), Width = new DataGridLength(55) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Triage", Binding = new System.Windows.Data.Binding(nameof(Row.Triage)), Width = new DataGridLength(90) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Mapped to", Binding = new System.Windows.Data.Binding(nameof(Row.MappedTo)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Tolerance XY + Z", Binding = new System.Windows.Data.Binding(nameof(Row.Tolerance)), Width = new DataGridLength(100) });
            Grid.SetColumn(_grid, 0);
            body.Children.Add(_grid);

            _editor = new StackPanel();
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            _editor.Children.Add(_title);
            _editor.Children.Add(Section("Local family type"));
            var catRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var catLabel = new TextBlock { Text = "Category ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, Width = 60 };
            DockPanel.SetDock(catLabel, Dock.Left);
            catRow.Children.Add(catLabel);
            _targetCat = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true,
                                        ToolTip = "Category of the local family types listed below (starts on the consultant type's category)" };
            catRow.Children.Add(_targetCat);
            _editor.Children.Add(catRow);
            var typeRow = new DockPanel();
            var typeLabel = new TextBlock { Text = "Type ", VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, Width = 60 };
            DockPanel.SetDock(typeLabel, Dock.Left);
            typeRow.Children.Add(typeLabel);
            var pickBtn = Btn("Pick in model", "Pick a placed family in the model (this view): its type becomes the local type of this mapping", PickTarget);
            pickBtn.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(pickBtn, Dock.Right);
            typeRow.Children.Add(pickBtn);
            _target = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true };
            // Large projects have thousands of types: only draw the rows on screen.
            _target.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(_target, true);
            VirtualizingPanel.SetVirtualizationMode(_target, VirtualizationMode.Recycling);
            typeRow.Children.Add(_target);
            _editor.Children.Add(typeRow);
            _placementNote = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 11 };
            _editor.Children.Add(_placementNote);

            _editor.Children.Add(Section("Offset (mm): X / Y along the consultant element in plan, Z = up"));
            _stepMm = new TextBox { Text = "50", Width = 50 };
            _editor.Children.Add(Vector(_offset, _stepMm, "Step mm"));
            _editor.Children.Add(Section("Rotation (° about the consultant element's X, then Y, then Z)"));
            _stepDeg = new TextBox { Text = "90", Width = 50 };
            _editor.Children.Add(Vector(_rotation, _stepDeg, "Step °"));

            _editor.Children.Add(Section("Position tolerance (Link Sync flags the local family only beyond it)"));
            var tolRow = new WrapPanel();
            _tolXy = new TextBox { Width = 60, Text = "25", VerticalContentAlignment = VerticalAlignment.Center };
            _tolZ = new TextBox { Width = 60, Text = "25", VerticalContentAlignment = VerticalAlignment.Center };
            foreach (var t in new[] { _tolXy, _tolZ })
            {
                t.LostFocus += (s, e) => Safe(() => { if (!_loadingEditor && !_closing && ReadEditor()) QueueSave(); });
                t.KeyDown += (s, e) => { if (e.Key == Key.Enter) Safe(() => { if (ReadEditor()) QueueSave(); }); };
            }
            tolRow.Children.Add(new TextBlock { Text = "XY ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            tolRow.Children.Add(_tolXy);
            tolRow.Children.Add(new TextBlock { Text = " mm     Z ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            tolRow.Children.Add(_tolZ);
            tolRow.Children.Add(new TextBlock { Text = " mm", VerticalAlignment = VerticalAlignment.Center });
            _editor.Children.Add(tolRow);

            _editor.Children.Add(Section("Parameters (consultant → local)"));
            _editor.Children.Add(new TextBlock
            {
                Text = "Map consultant values onto the local family, checked against the real consultant values (preview below). Values are written when Link Sync places a family; later changes show in Link Sync as 'Parameter changed' (or are written straight away with Always apply).",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 4),
            });
            _paramList = new ListBox { MinHeight = 44, MaxHeight = 150, Margin = new Thickness(0, 0, 0, 4) };
            _editor.Children.Add(_paramList);
            var pick = new WrapPanel();
            _srcParam = new ComboBox { Width = 230, IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true, Margin = new Thickness(0, 0, 4, 4),
                                       ToolTip = "Consultant parameter (instance / type, from the elements of this type in the link)" };
            _dstParam = new ComboBox { Width = 230, IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true, Margin = new Thickness(0, 0, 4, 4),
                                       ToolTip = "Local parameter on the mapped family type (type / instance / project parameters of its category)" };
            pick.Children.Add(_srcParam);
            pick.Children.Add(new TextBlock { Text = "→ ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            pick.Children.Add(_dstParam);
            _editor.Children.Add(pick);
            var pick2 = new WrapPanel();
            _paramMode = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 6, 4), ToolTip = "How changes of the consultant value reach the local family: approved in Link Sync, or written on each Link Sync refresh" };
            _paramMode.Items.Add("Link Sync approval");
            _paramMode.Items.Add("Always apply");
            _paramMode.SelectedIndex = 0;
            pick2.Children.Add(_paramMode);
            pick2.Children.Add(Btn("Add / update", "Map the consultant parameter to the local one (one consultant parameter per local parameter)", AddParamMap));
            pick2.Children.Add(Btn("Remove", "Remove the selected parameter mapping", RemoveParamMap));
            pick2.Children.Add(Btn("Transform…", "Change the consultant value before it's written: constant, default for empty values, lookup table (e.g. FR60 → 60/60/60), expression, prefix / suffix", EditTransform));
            pick2.Children.Add(Btn("Add local parameter…", "The local family has no fitting parameter: add one (project parameter on its category, instance or type) and map the consultant parameter to it", AddLocalParam));
            pick2.Children.Add(Btn("↻", "Read the parameters again (e.g. after placing a test family: its instance parameters show)", () => { _paramsFor = null; _srcFor = null; LoadParams(); }));
            _editor.Children.Add(pick2);
            _paramInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), Foreground = Muted };
            _editor.Children.Add(_paramInfo);

            _editor.Children.Add(Section("Workset for placed families"));
            _workset = new ComboBox();
            _editor.Children.Add(_workset);

            _editor.Children.Add(Section("Try it"));
            var testRow = new WrapPanel();
            _test = Btn("Test on one", "Place a temporary local family at a consultant element of this type (the one selected in Revit if any) and zoom to it. Changing the offset / rotation re-places it; changing its type in Revit changes the mapping. Removed when you move on or close.", TestOnOne);
            var prev = Btn("◀ Previous", "Move the test to the previous consultant element of this type", () => { _sample--; _testSourceUid = null; TestOnOne(); });
            _next = Btn("Next ▶", "Move the test to another consultant element of this type", () => { _sample++; _testSourceUid = null; TestOnOne(); });
            _removeTest = Btn("Remove test", "Delete the test family", RemoveTest);
            testRow.Children.Add(_test); testRow.Children.Add(prev); testRow.Children.Add(_next); testRow.Children.Add(_removeTest);
            _editor.Children.Add(testRow);
            _testInfo = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), FontSize = 11 };
            _editor.Children.Add(_testInfo);
            var findRow = new WrapPanel();
            findRow.Children.Add(Btn("Zoom to test", "Zoom to our test family and the consultant element (this view if it shows them, else a review view, else your federator 3D view)",
                                     () => FindTest(false, true, false)));
            findRow.Children.Add(Btn("Show in 3D", "Open your federator 3D view with a section box around the test and the consultant element", () => FindTest(true, true, false)));
            findRow.Children.Add(Btn("Select local", "Select the local test family in Revit (e.g. to check it in Properties)", () => FindTest(false, false, false, selectOnly: true)));
            findRow.Children.Add(Btn("Select consultant", "Select the consultant element in Revit (Tab-select in the link)", () => FindTest(false, false, true, selectOnly: true)));
            _editor.Children.Add(findRow);

            _editor.Children.Add(Section("Mapping"));
            var apply = new WrapPanel();
            apply.Children.Add(Btn("Clear mapping", "Forget this type's mapping", RemoveMapping));
            apply.Children.Add(new TextBlock { Text = "Changes save automatically.", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            _editor.Children.Add(apply);

            var editorScroll = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(editorScroll, 2);
            body.Children.Add(editorScroll);
            root.Children.Add(body);
            Content = root;

            _filter.TextChanged += (s, e) => Safe(Fill);
            _suggestedOnly.Checked += (s, e) => Safe(Fill);
            _suggestedOnly.Unchecked += (s, e) => Safe(Fill);
            _grid.SelectionChanged += (s, e) => Safe(OnGridSelection);
            _target.SelectionChanged += (s, e) => Safe(() => { if (!_loadingEditor && ReadEditor()) { UpdateNote(); QueueLive(); QueueSave(); LoadParams(); } });
            _targetCat.SelectionChanged += (s, e) => Safe(() => { if (!_loadingEditor && _targetCat.SelectedItem != null) FillTargets(_editing?.TargetSymbolUid); });
            _srcParam.SelectionChanged += (s, e) => Safe(() => { if (!_loadingParams) UpdateParamInfo(); });
            _dstParam.SelectionChanged += (s, e) => Safe(() => { if (!_loadingParams) UpdateParamInfo(); });
            _paramList.SelectionChanged += (s, e) => Safe(ShowParamMap);
            _workset.SelectionChanged += (s, e) => Safe(() => { if (!_loadingEditor && ReadEditor()) QueueSave(); });
            _categories.Changed += () => Safe(() => { Fill(); QueueSave(); });
            _liveTimer.Tick += (s, e) => Safe(() => { _liveTimer.Stop(); if (_testId != null) TestOnOne(false); });
            _saveTimer.Tick += (s, e) => Safe(() => { _saveTimer.Stop(); SaveNow(); });
            Closing += (s, e) => Safe(() =>
            {
                // Pending edits first, then the temporary test family and any Perseus work planes nothing stands on.
                _closing = true;
                _liveTimer.Stop();
                if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
                RemoveTest(cleanPlanes: true);
            });
            Closed += (s, e) =>
            {
                _liveTimer.Stop();
                if (Instance == this) Instance = null;
                var revitApp = _doc?.Application;
                if (revitApp != null) OwnershipUiEvent.Run(a => { try { revitApp.DocumentChanged -= OnDocumentChanged; } catch { } });
                OwnershipUiEvent.Run(a => { try { a.SelectionChanged -= OnRevitSelectionChanged; } catch { } });
            };
            // Watch the test family: a type change made in Revit (type selector, or Edit Type > Duplicate) is taken into the mapping.
            Api(app => app.Application.DocumentChanged += OnDocumentChanged);
            // Follow Revit's selection: a link picks that link, a linked element (Tab / Select links) also its consultant type's row.
            Api(app => app.SelectionChanged += OnRevitSelectionChanged);
            _editor.IsEnabled = false;
            _linkBox.SelectionChanged += (s, e) => Safe(LinkChanged);
            _linkBox.SelectedItem = links.FirstOrDefault(l => l.Uid == preferredLink) ?? links[0];
        }

        /// <summary>Another link picked: its mapping, triage (from the federator panel's comparison of it) and replacement links.</summary>
        private void LinkChanged()
        {
            var l = _linkBox.SelectedItem as LinkChoice;
            if (l == null) return;
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
            RemoveTest();
            _linkUid = l.Uid; _modelKey = l.ModelKey; _linkName = l.Name; _modelName = l.BaseName;
            _linkLoaded = l.Loaded;
            Title = "Perseus — Family mapping · " + l.Name + (l.Loaded ? "" : " (review only: link not loaded)");
            _current = null; _editing = null;
            _editor.IsEnabled = false;
            var fed = FederatorWindow.Instance;
            _triage = fed != null && fed.Record != null ? fed.TriageByType(l.Uid) : new Dictionary<string, TypeTriage>();
            _triageLabel = fed?.TriageSourceLabel(l.Uid);
            Api(app =>
            {
                _replacementMap = l.Uid == null ? new Dictionary<string, string>() : ReplacementLinks.Map(ReplacementLinks.Fold(ReviewStore.Load(_doc), l.Uid));
                Reload();
            });
        }

        private string _triageLabel;
        // The picked link isn't loaded (or isn't linked here): its saved mapping is listed read-only.
        private bool _linkLoaded = true;
        private string _modelName;   // the link's name without the "#2" of duplicates: saved with the mapping

        // ═══════════════ Loading ═══════════════

        private void Reload()
        {
            Status("Reading the link and the local families…");
            Api(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var linkDoc = inst?.GetLinkDocument();
                _linkLoaded = linkDoc != null;
                _targets = FamilyPlacer.TargetTypes(_doc);
                _mapping = FamilyMapStore.Load(_doc, _modelKey);
                // Not loaded: the consultant types are the mapped ones (from the saved rules), nothing to count in the link.
                _sources = linkDoc != null ? FamilyPlacer.SourceTypes(linkDoc)
                    : _mapping.Rules.Select(r => new FamilyPlacer.SourceType { Key = r.SourceKey, Category = r.SourceCategory, Family = r.SourceFamily, Type = r.SourceType })
                                    .GroupBy(x => x.Key).Select(g => g.First()).ToList();
                _mappedByRule = _linkUid == null ? new Dictionary<string, int>()
                    : MappedFamilyStore.All(_doc, _linkUid).GroupBy(r => r.RuleId ?? "").ToDictionary(g => g.Key, g => g.Count());
                _loadingEditor = true;
                _workset.Items.Clear();
                _workset.Items.Add(ActiveWorkset);
                foreach (var w in FamilyPlacer.Worksets(_doc)) _workset.Items.Add(w);
                _workset.IsEnabled = _doc.IsWorkshared;
                FillTargetCategories();
                _loadingEditor = false;
                _srcFor = null; _paramsFor = null;
                var hidden = new HashSet<string>(_mapping.HiddenCategories ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                _categories.SetItems(_sources.GroupBy(x => x.Category).OrderBy(g => g.Key)
                    .Select(g => new MultiPick.Item(g.Key, $"{g.Key}  ({g.Count()})")));
                foreach (var c in _sources.Select(x => x.Category).Distinct()) _categories.SetTicked(c, !hidden.Contains(c));
                BuildRows();
                Fill();
                if (!_linkLoaded)
                {
                    int placed = _mappedByRule.Values.Sum();
                    Status((_linkUid == null ? "This consultant model isn't linked in this model" : "This link isn't loaded") +
                           $": its {_mapping.Rules.Count} saved mapping(s) are shown read-only" + (placed > 0 ? $" ({placed} local families placed from them)" : "") +
                           ". " + (_linkUid == null ? "Link the model to edit or test them." : "Load it in Manage Links (then reopen this panel) to edit or test them."), true);
                    if (_pendingSourceUid != null) _pendingSourceUid = null;
                    return;
                }
                Status($"{_sources.Count} consultant family type(s), {_targets.Count} local family types, {_mapping.Rules.Count} mapped. " +
                       (_triageLabel != null ? $"Suggestions from the triage of {_triageLabel}."
                                             : "No triage for this link: open one of its comparisons in Link Changes to get suggestions (all types are listed meanwhile)."));
                if (_triageLabel == null) _suggestedOnly.IsChecked = false;
                if (_pendingSourceUid != null) { var u = _pendingSourceUid; _pendingSourceUid = null; SelectSourceRow(u); }
            });
        }

        // ═══════════════ Following Revit's selection ═══════════════

        private string _pendingSourceUid;   // consultant element to select once the link switched to has loaded

        private void OnRevitSelectionChanged(object sender, Autodesk.Revit.UI.Events.SelectionChangedEventArgs e)
        {
            try
            {
                if (_closing || _doc == null) return;
                var doc = e.GetDocument();
                if (doc == null || !doc.Equals(_doc)) return;
                string linkUid = null, srcUid = null;
                foreach (var r in e.GetReferences() ?? new List<ARDB.Reference>())
                {
                    if (!(doc.GetElement(r?.ElementId) is ARDB.RevitLinkInstance li)) continue;
                    linkUid = li.UniqueId;
                    if (r.LinkedElementId != ARDB.ElementId.InvalidElementId) srcUid = li.GetLinkDocument()?.GetElement(r.LinkedElementId)?.UniqueId;
                    if (srcUid != null) break;   // prefer a linked element over a bare link
                }
                if (linkUid == null) return;
                Dispatcher.BeginInvoke(new Action(() => Safe(() => FollowRevitSelection(linkUid, srcUid))));
            }
            catch (Exception ex) { Log.Info($"[Mapping] following the Revit selection: {ex.Message}"); }
        }

        private void FollowRevitSelection(string linkUid, string srcUid)
        {
            if (_closing) return;
            var choice = _linkBox.Items.OfType<LinkChoice>().FirstOrDefault(l => l.Uid == linkUid);
            if (choice == null) return;
            if (linkUid != _linkUid)
            {
                _pendingSourceUid = srcUid;
                _linkBox.SelectedItem = choice;   // LinkChanged -> Reload -> then the row
                Status($"Switched to {choice.Name} (selected in Revit)…");
                return;
            }
            if (srcUid != null) SelectSourceRow(srcUid);
        }

        /// <summary>Selects the row of the consultant type of a linked element (un-hiding it from the suggested-only / text filter).</summary>
        private void SelectSourceRow(string srcUid)
        {
            var row = _rows.FirstOrDefault(r => r.Source.Uids.Contains(srcUid));
            if (row == null) { Status("The selected consultant element isn't a family type listed here (not a family, or nested in another)."); return; }
            if (_current != null && _current.Source.Key == row.Source.Key) return;   // already on it (also the test's own consultant element)
            if (_grid.SelectedItems.Count > 1 && _grid.SelectedItems.Cast<Row>().Any(r => r.Source.Key == row.Source.Key)) return;
            if (!_categories.IsTicked(row.Category))
            {
                Status($"{row.Type} is in {row.Category}, which is filtered off: tick it in the category list to see it.", true);
                return;
            }
            var shown = (_grid.ItemsSource as IEnumerable<Row>)?.ToList() ?? new List<Row>();
            if (!shown.Contains(row))
            {
                if (_suggestedOnly.IsChecked == true) _suggestedOnly.IsChecked = false;
                if (!string.IsNullOrEmpty(_filter.Text)) _filter.Text = "";
                Fill();
                shown = (_grid.ItemsSource as IEnumerable<Row>)?.ToList() ?? new List<Row>();
                if (!shown.Contains(row)) return;
            }
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(row);
            Status($"{row.Category}: {row.Type} (selected in Revit).");
        }

        private void BuildRows()
        {
            _rows.Clear();
            foreach (var s in _sources)
            {
                _triage.TryGetValue(s.Key, out var t);
                t = t ?? new TypeTriage();
                var rule = _mapping.Rules.FirstOrDefault(r => r.SourceKey == s.Key);
                _rows.Add(new Row
                {
                    Category = s.Category, Type = s.ToString(), Count = s.Uids.Count, Triage = t.Verdict, Source = s, Stats = t,
                    MappedTo = rule?.TargetName ?? "",
                    Tolerance = ToleranceText(rule),
                    Mapped = rule != null && _mappedByRule.TryGetValue(rule.Id, out int n) ? $"{n} / {s.Uids.Count}" : "",
                });
            }
        }

        private void Fill()
        {
            string q = _filter.Text?.Trim() ?? "";
            bool only = _suggestedOnly.IsChecked == true;
            var keep = _current?.Source.Key;
            var keepAll = new HashSet<string>(_grid.SelectedItems.Cast<Row>().Select(r => r.Source.Key));
            var shown = _rows.Where(r => _categories.IsTicked(r.Category) && (!only || r.Triage == TypeTriage.Suggested || r.MappedTo != "")
                                         && (q.Length == 0 || (r.Category + " " + r.Type).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                             .OrderBy(r => r.MappedTo != "" ? 0 : r.Triage == TypeTriage.Suggested ? 1 : r.Triage == TypeTriage.Unchanged ? 2 : 3)
                             .ThenBy(r => r.Category).ThenByDescending(r => r.Count).ToList();
            _grid.ItemsSource = shown;
            int hiddenCats = _rows.Select(r => r.Category).Distinct().Count(c => !_categories.IsTicked(c));
            _summary.Text = $"{shown.Count} of {_rows.Count} type(s)" + (hiddenCats > 0 ? $" ({hiddenCats} categories off)" : "") +
                            $" · {_rows.Count(r => r.Triage == TypeTriage.Suggested)} suggested by triage · " +
                            $"{_rows.Count(r => r.MappedTo != "")} mapped";
            var again = shown.FirstOrDefault(r => r.Source.Key == keep);
            if (again != null) _grid.SelectedItem = again;
            foreach (var r in shown.Where(r => r != again && keepAll.Contains(r.Source.Key))) _grid.SelectedItems.Add(r);
        }

        /// <summary>"25 + 25" (XY + Z mm) for a mapped type, else empty.</summary>
        private static string ToleranceText(FamilyMapRule rule) => rule == null ? "" : $"{Num(rule.ToleranceXyMm)} + {Num(rule.ToleranceZMm)}";

        /// <summary>
        /// Selection in the list: the first one (or the one being edited, if still selected) fills the editor; with several
        /// selected, each edit made in the editor is applied to all of them (only the value changed), Test on one is off.
        /// </summary>
        private void OnGridSelection()
        {
            var rows = _grid.SelectedItems.Cast<Row>().ToList();
            if (rows.Count == 0) return;
            var primary = _current != null && rows.Contains(_current) ? _current : (_grid.SelectedItem as Row ?? rows[0]);
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
            Select(primary);
            _others.Clear();
            foreach (var r in rows.Where(r => r != primary))
            {
                var rule = _mapping.Rules.FirstOrDefault(x => x.SourceKey == r.Source.Key);
                _others.Add(new KeyValuePair<Row, FamilyMapRule>(r, rule != null ? Copy(rule)
                    : new FamilyMapRule { SourceCategory = r.Source.Category, SourceFamily = r.Source.Family, SourceType = r.Source.Type }));
            }
            if (_others.Count == 0) _title.Text = $"{primary.Source.Category} · {primary.Source}";
            if (_others.Count > 0)
            {
                RemoveTest();
                _title.Text = $"{rows.Count} types selected · edits apply to all";
                var all = new[] { _editing }.Concat(_others.Select(o => o.Value)).ToList();
                var differ = new List<string>();
                if (all.Select(x => x.TargetSymbolUid ?? "").Distinct().Count() > 1) differ.Add("type");
                if (all.Select(x => string.Join(",", x.OffsetMm)).Distinct().Count() > 1) differ.Add("offset");
                if (all.Select(x => string.Join(",", x.RotationDeg)).Distinct().Count() > 1) differ.Add("rotation");
                if (all.Select(x => x.ToleranceXyMm + "/" + x.ToleranceZMm).Distinct().Count() > 1) differ.Add("tolerance");
                if (all.Select(x => x.Workset ?? "").Distinct().Count() > 1) differ.Add("workset");
                Status($"{rows.Count} types selected. Showing {primary.Source}; a value you change is set on all of them" +
                       (differ.Count > 0 ? $" (they differ in {string.Join(", ", differ)})." : "."));
            }
            UpdateNote();
        }

        /// <summary>Copies the values the user just changed on the edited type (before -> now) to the other selected types.</summary>
        private void Propagate(FamilyMapRule before, FamilyMapRule now)
        {
            if (before == null || now == null || _others.Count == 0) return;
            foreach (var o in _others)
            {
                var r = o.Value;
                if (before.TargetSymbolUid != now.TargetSymbolUid) { r.TargetSymbolUid = now.TargetSymbolUid; r.TargetName = now.TargetName; }
                for (int i = 0; i < 3; i++)
                {
                    if (before.OffsetMm[i] != now.OffsetMm[i]) r.OffsetMm[i] = now.OffsetMm[i];
                    if (before.RotationDeg[i] != now.RotationDeg[i]) r.RotationDeg[i] = now.RotationDeg[i];
                }
                if ((before.Workset ?? "") != (now.Workset ?? "")) r.Workset = now.Workset;
                if (before.ToleranceXyMm != now.ToleranceXyMm) r.ToleranceXyMm = now.ToleranceXyMm;
                if (before.ToleranceZMm != now.ToleranceZMm) r.ToleranceZMm = now.ToleranceZMm;
            }
        }

        // ═══════════════ Editor ═══════════════

        private void Select(Row row)
        {
            if (row == null) return;
            if (_current != null && _editing != null && row.Source.Key == _current.Source.Key) { _current = row; return; }
            if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); }
            RemoveTest();
            _current = row;
            _sample = 0;
            var s = row.Source;
            _editing = _mapping.Rules.FirstOrDefault(r => r.SourceKey == s.Key) ?? new FamilyMapRule
            {
                SourceCategory = s.Category, SourceFamily = s.Family, SourceType = s.Type,
            };
            _loadingEditor = true;
            _title.Text = $"{s.Category} · {s}";
            SetTargetCategory(DefaultTargetCategory(s.Category, _editing.TargetSymbolUid));
            FillTargets(_editing.TargetSymbolUid);
            for (int i = 0; i < 3; i++)
            {
                _offset[i].Text = Num(_editing.OffsetMm[i]);
                _rotation[i].Text = Num(_editing.RotationDeg[i]);
            }
            _workset.SelectedItem = string.IsNullOrEmpty(_editing.Workset) || !_workset.Items.Contains(_editing.Workset) ? ActiveWorkset : _editing.Workset;
            _tolXy.Text = Num(_editing.ToleranceXyMm);
            _tolZ.Text = Num(_editing.ToleranceZMm);
            _loadingEditor = false;
            _editor.IsEnabled = _linkLoaded;
            UpdateNote();
            LoadParams();
            if (!_linkLoaded)
            {
                Status($"Read only (link not loaded) · mapped to {(_editing.TargetName ?? "nothing")}" +
                       (_mappedByRule.TryGetValue(_editing.Id, out int m) ? $" · {m} local families placed" : "") +
                       (string.IsNullOrEmpty(_editing.Author) ? "" : $" · last saved by {_editing.Author}" + (_editing.SavedUtc > DateTime.MinValue ? $" {_editing.SavedUtc.ToLocalTime():d MMM yyyy}" : "")) + ".");
                return;
            }
            Status($"{s.Uids.Count} in the link · triage: {row.Stats.Describe()}" +
                   (_mappedByRule.TryGetValue(_editing.Id, out int n) ? $" · {n} local families mapped" : ""));
        }

        /// <summary>Pick a placed local family in the active view: its type becomes the mapping's local type (category switched to it).</summary>
        private void PickTarget()
        {
            if (_editing == null) { Status("Select a consultant type first."); return; }
            string ruleId = _editing.Id;
            Status("Pick a local family in the view (Esc to cancel)…");
            Api(app =>
            {
                ARDB.Reference r;
                try
                {
                    r = app.ActiveUIDocument.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, new LocalFamilyFilter(),
                                                                  "Pick a local family: its type becomes the local type of this mapping (Esc to cancel)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { Status("Pick cancelled."); return; }
                var symbol = (_doc.GetElement(r?.ElementId) as ARDB.FamilyInstance)?.Symbol;
                if (symbol == null) { Status("That isn't a family in this model.", true); return; }
                if (_editing == null || _editing.Id != ruleId) return;   // moved on meanwhile
                string uid = symbol.UniqueId, name = $"{symbol.FamilyName} : {symbol.Name}";
                if (!_targets.Any(t => t.Uid == uid))
                {
                    _targets = FamilyPlacer.TargetTypes(_doc);   // a type loaded / made since the list was read
                    FillTargetCategories();
                }
                var tt = _targets.FirstOrDefault(t => t.Uid == uid);
                if (tt == null) { Status($"{name} can't be used here (in-place, view-based or annotation family).", true); return; }
                // A family the team already placed at a consultant element of this type: its position is the mapping's offset.
                var picked = _doc.GetElement(r.ElementId) as ARDB.FamilyInstance;
                string learnt = LearnFromPlaced(picked, null, out var off, out var rot, out bool nearby);
                if (!nearby && picked != null)
                {
                    // None of this type close by: let the user show which consultant element this family belongs to.
                    Status("No consultant element of this type within 1.5 m: pick the consultant element this family belongs to (Esc to skip)…");
                    var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                    ARDB.Element chosen = null;
                    try
                    {
                        var lr = app.ActiveUIDocument.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.LinkedElement,
                                     "Pick the consultant element this local family belongs to (Esc to skip)");
                        if (lr != null && inst != null && lr.ElementId == inst.Id) chosen = inst.GetLinkDocument()?.GetElement(lr.LinkedElementId);
                        else if (lr != null) learnt = "That element is in another link: offset not taken.";
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                    if (chosen != null) learnt = LearnFromPlaced(picked, chosen, out off, out rot, out _);
                }
                SetTargetCategory(tt.Category);
                FillTargets(uid);
                if (off != null)
                {
                    _loadingEditor = true;
                    try { for (int i = 0; i < 3; i++) { _offset[i].Text = Num(off[i]); _rotation[i].Text = Num(rot[i]); } }
                    finally { _loadingEditor = false; }
                }
                if (!ReadEditor()) return;
                UpdateNote(); QueueLive(); QueueSave(); LoadParams();
                Status($"Local type: {name}" + (tt.Placeable ? "." : $" — needs a host ({tt.Placement}) Perseus can't place: pick a work-plane-, level- or wall / floor-hosted family.") +
                       (learnt != null ? " " + learnt : ""), !tt.Placeable);
            }, needActive: true);
        }

        /// <summary>
        /// The offset and rotation that put the picked local family where it is relative to a consultant element
        /// (MappingMath.Solve): <paramref name="forced"/> when given, else the nearest element of the selected type
        /// (<paramref name="nearby"/> = within 1.5 m; the nearest is logged either way). Returns a status sentence; off / rot null
        /// when not learnt. API context.
        /// </summary>
        private string LearnFromPlaced(ARDB.FamilyInstance ours, ARDB.Element forced, out double[] off, out double[] rot, out bool nearby)
        {
            off = rot = null;
            nearby = false;
            try
            {
                var placed = ours == null ? null : FamilyPlacer.FrameOf(ours);
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var linkDoc = inst?.GetLinkDocument();
                if (placed == null || linkDoc == null || _editing == null) return null;
                var toHost = inst.GetTotalTransform();
                var o = new ARDB.XYZ(placed.O[0], placed.O[1], placed.O[2]);
                ARDB.Element best = null; mapping.Frame bestFrame = null; double bestD = double.MaxValue;
                foreach (var s in forced != null ? new List<ARDB.Element> { forced } : FamilyPlacer.SourceElements(linkDoc, _editing))
                {
                    var f = FamilyPlacer.SourceFrame(s, toHost);
                    double d = o.DistanceTo(new ARDB.XYZ(f.O[0], f.O[1], f.O[2]));
                    if (d < bestD) { bestD = d; best = s; bestFrame = f; }
                }
                if (best == null) { Log.Info($"[Mapping] picked {ours.Id.GetIdValue()}: no consultant elements of this type in the link"); return null; }
                Log.Info($"[Mapping] picked {ours.Id.GetIdValue()} at {Mm(o)}; " + (forced != null ? "chosen" : "nearest") +
                         $" consultant {best.Id.GetIdValue()} ({TypeName(best)}) frame {Mm(bestFrame)}, {bestD * MappingMath.MmPerFoot:N0} mm apart");
                FamilyPlacer.LogSourceGeometry(best, toHost, o);
                nearby = forced != null || bestD <= 1500 / MappingMath.MmPerFoot;
                if (!nearby) return null;
                if (!MappingMath.Solve(bestFrame, placed, (double[])_editing.RotationDeg.Clone(), out off, out rot))
                {
                    off = rot = null;
                    return $"It is mirrored relative to consultant element {best.Id.GetIdValue()}, so its offset wasn't taken.";
                }
                Log.Info($"[Mapping] -> offset {string.Join(",", off)} rotation {string.Join(",", rot)}");
                // The picked family becomes the test: moving it in Revit adjusts the mapping; Select consultant / zoom work on it.
                if (_testId != null && !_testIsExisting && _doc.GetElement(_testId) != null)
                {
                    using (var tx = new ARDB.Transaction(_doc, "Perseus: remove test family")) { tx.Start(); _doc.Delete(_testId); tx.Commit(); }
                }
                _testId = ours.Id;
                _testIsExisting = true;
                _testSourceUid = best.UniqueId;
                _testSource = bestFrame;
                _testExpected = MappingMath.Place(bestFrame, off, rot);
                _testPlaced = placed;
                _testInfo.Text = $"Test: your existing {ours.Symbol?.FamilyName} (Id {ours.Id.GetIdValue()}) at consultant element Id {best.Id.GetIdValue()} · " +
                                 "move it in Revit to adjust the mapping (Perseus won't delete or move it)";
                return $"Offset {Num(off[0])} / {Num(off[1])} / {Num(off[2])} mm and rotation {Num(rot[0])} / {Num(rot[1])} / {Num(rot[2])}° " +
                       $"taken from where it sits relative to consultant element {best.Id.GetIdValue()} ({bestD * MappingMath.MmPerFoot:N0} mm from its centre)." +
                       (forced != null && !FamilyPlacer.SourceElements(linkDoc, _editing).Any(x => x.Id == forced.Id) ? " Note: that element isn't of the selected consultant type." : "");
            }
            catch (Exception ex)
            {
                Log.Info($"[Mapping] learning the offset from the picked family failed: {ex.Message}");
                off = rot = null;
                return null;
            }
        }

        private static string TypeName(ARDB.Element e) =>
            e is ARDB.FamilyInstance fi ? $"{fi.Category?.Name}: {fi.Symbol?.FamilyName}: {fi.Symbol?.Name}" : e.Category?.Name ?? e.GetType().Name;

        private static string Mm(ARDB.XYZ p) => $"({p.X * MappingMath.MmPerFoot:N0}, {p.Y * MappingMath.MmPerFoot:N0}, {p.Z * MappingMath.MmPerFoot:N0})";
        private static string Mm(mapping.Frame f) => Mm(new ARDB.XYZ(f.O[0], f.O[1], f.O[2]));

        private class LocalFamilyFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            public bool AllowElement(ARDB.Element e) => e is ARDB.FamilyInstance;
            public bool AllowReference(ARDB.Reference r, ARDB.XYZ p) => false;
        }

        /// <summary>A category of local family types (Name null = all categories).</summary>
        private class CatItem
        {
            internal readonly string Name;
            private readonly int _count;
            internal CatItem(string name, int count) { Name = name; _count = count; }
            public override string ToString() => (Name ?? "All categories") + "  (" + _count + ")";
        }

        /// <summary>The category list above the local types (keeps the picked one).</summary>
        private void FillTargetCategories()
        {
            string keep = (_targetCat.SelectedItem as CatItem)?.Name;
            bool was = _loadingEditor; _loadingEditor = true;
            try
            {
                var items = new List<CatItem> { new CatItem(null, _targets.Count) };
                items.AddRange(_targets.GroupBy(t => t.Category).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => new CatItem(g.Key, g.Count())));
                _targetCat.ItemsSource = items;
                _targetCat.SelectedItem = items.FirstOrDefault(c => c.Name == keep && keep != null) ?? items[0];
            }
            finally { _loadingEditor = was; }
        }

        /// <summary>The consultant type's own category when there are local types in it, else the mapped type's, else all.</summary>
        private string DefaultTargetCategory(string sourceCategory, string targetUid)
        {
            if (_targets.Any(t => t.Category == sourceCategory)) return sourceCategory;
            return _targets.FirstOrDefault(t => t.Uid == targetUid)?.Category;
        }

        private void SetTargetCategory(string category)
        {
            var items = _targetCat.Items.Cast<CatItem>().ToList();
            if (items.Count == 0) return;
            bool was = _loadingEditor; _loadingEditor = true;
            try { _targetCat.SelectedItem = items.FirstOrDefault(c => c.Name == category && category != null) ?? items[0]; }
            finally { _loadingEditor = was; }
        }

        /// <summary>
        /// The local types of the picked category (preferred placement first). The mapped type stays listed (on top) even
        /// when it's in another category, so changing the category never drops the mapping.
        /// </summary>
        private void FillTargets(string keepUid)
        {
            string cat = (_targetCat.SelectedItem as CatItem)?.Name;
            var items = _targets.Where(t => cat == null || t.Category == cat || (keepUid != null && t.Uid == keepUid))
                                .OrderBy(t => cat != null && t.Category != cat ? 0 : 1)
                                .ThenBy(t => t.Category).ThenBy(t => t.Preference).ThenBy(t => t.Family).ThenBy(t => t.Type)
                                .Select(t => new TargetItem(t, cat != null && t.Category == cat)).ToList();
            bool was = _loadingEditor; _loadingEditor = true;
            try
            {
                _target.ItemsSource = items;
                _target.SelectedItem = keepUid == null ? null : items.FirstOrDefault(i => i.T.Uid == keepUid);
                if (_target.SelectedItem == null) _target.Text = "";
            }
            finally { _loadingEditor = was; }
        }

        private class TargetItem
        {
            internal readonly FamilyPlacer.TargetType T;
            private readonly bool _same;
            internal TargetItem(FamilyPlacer.TargetType t, bool same) { T = t; _same = same; }
            public override string ToString() => (_same ? "" : T.Category + " · ") + T + "   (" + T.Hosting + ")";
        }

        private void UpdateNote()
        {
            var t = (_target.SelectedItem as TargetItem)?.T;
            _placementNote.Text = t == null ? "Pick the family type that stands for this consultant type. Work-plane-based families are listed first (preferred)."
                : t.FaceBased ? "Face-based: placed on a work plane (a Perseus sketch plane at the target height, facing like the consultant element). A work-plane-based version of the family is preferred."
                : t.FullRotation ? "Work-plane-based (preferred): placed on a Perseus sketch plane at the target height, facing like the consultant element; tilted elements get a plane through the point."
                : t.Floating ? "Level-based: placed on the level below; it always stands upright, so only the Z rotation applies. A work-plane-based family can also tilt."
                : t.Hosted ? $"{char.ToUpper(t.HostKind[0]) + t.HostKind.Substring(1)}-hosted: placed in the local {t.HostKind} at the consultant element (nothing is placed where there's no {t.HostKind} of ours). " +
                             $"It takes the {t.HostKind}'s direction and depth; the offset moves it along the {t.HostKind}, rotation doesn't apply."
                : $"Needs a host ({t.Placement}) Perseus can't place. Pick a work-plane-, level- or wall / floor-hosted family.";
            _placementNote.Foreground = t != null && !t.Placeable ? Bad : t != null && t.FaceBased ? Frozen(0xB4, 0x6A, 0x00) : Muted;
            bool ok = t != null && t.Placeable && _others.Count == 0;
            _test.IsEnabled = _next.IsEnabled = ok;
        }

        /// <summary>Reads the editor into _editing; false (with a message) when a number doesn't parse.</summary>
        private bool ReadEditor()
        {
            if (_editing == null) return false;
            var before = Copy(_editing);
            var t = (_target.SelectedItem as TargetItem)?.T;
            _editing.TargetSymbolUid = t?.Uid;
            _editing.TargetName = t?.ToString();
            for (int i = 0; i < 3; i++)
            {
                if (!TryNum(_offset[i].Text, out double o) || !TryNum(_rotation[i].Text, out double r)) { Status("Offsets and rotations must be numbers.", true); return false; }
                _editing.OffsetMm[i] = o;
                _editing.RotationDeg[i] = r;
            }
            var ws = _workset.SelectedItem as string;
            _editing.Workset = ws == null || ws == ActiveWorkset ? "" : ws;
            if (!TryNum(_tolXy.Text, out double tx) || !TryNum(_tolZ.Text, out double tz) || tx < 0 || tz < 0) { Status("Tolerances must be numbers (mm).", true); return false; }
            _editing.ToleranceXyMm = tx;
            _editing.ToleranceZMm = tz;
            Propagate(before, _editing);
            return true;
        }

        private UIElement Vector(TextBox[] boxes, TextBox step, string stepLabel)
        {
            var row = new WrapPanel();
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                boxes[k] = new TextBox { Width = 70, Text = "0", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
                boxes[k].LostFocus += (s, e) => Safe(() => { if (!_loadingEditor && !_closing && ReadEditor()) { QueueLive(); QueueSave(); } });
                boxes[k].KeyDown += (s, e) => { if (e.Key == Key.Enter) Safe(() => { if (ReadEditor()) { QueueLive(); QueueSave(); } }); };
                row.Children.Add(new TextBlock { Text = axes[k] + " ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
                row.Children.Add(boxes[k]);
                row.Children.Add(Nudge("−", () => Bump(boxes[k], step, -1)));
                row.Children.Add(Nudge("+", () => Bump(boxes[k], step, +1)));
                row.Children.Add(new Border { Width = 10 });
            }
            row.Children.Add(new TextBlock { Text = stepLabel + " ", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(step);
            return row;
        }

        private void Bump(TextBox box, TextBox step, int sign)
        {
            if (!TryNum(box.Text, out double v) || !TryNum(step.Text, out double s)) return;
            box.Text = Num(v + sign * s);
            if (ReadEditor()) { QueueLive(); QueueSave(); }
        }

        private void QueueSave()
        {
            if (_editing == null && _categories == null) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Saves the type being edited (when it has our type) and the category filter, into a fresh copy of the mapping
        /// (others may have mapped other types meanwhile). Values are captured now; the write runs in the API context.
        /// </summary>
        private void SaveNow()
        {
            if (_modelKey == null || !_linkLoaded) return;   // review only: never re-save a mapping read without its link
            var rules = new List<KeyValuePair<Row, FamilyMapRule>>();
            if (_editing != null) rules.Add(new KeyValuePair<Row, FamilyMapRule>(_current, _editing));
            rules.AddRange(_others);
            rules = rules.Where(kv => !string.IsNullOrEmpty(kv.Value.TargetSymbolUid))
                         .Select(kv => { var c = Copy(kv.Value); c.Author = _me; c.SavedUtc = DateTime.UtcNow; return new KeyValuePair<Row, FamilyMapRule>(kv.Key, c); }).ToList();
            var hidden = _rows.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !_categories.IsTicked(c))
                              .OrderBy(c => c).ToList();
            string modelKey = _modelKey, modelName = _modelName;
            Api(app =>
            {
                var mapping = FamilyMapStore.Load(_doc, modelKey);
                bool changed = false;
                var saved = new List<KeyValuePair<Row, FamilyMapRule>>();
                foreach (var kv in rules)
                {
                    var rule = kv.Value;
                    var was = mapping.Rules.FirstOrDefault(r => r.SourceKey == rule.SourceKey);
                    if (was == null || !SameRule(was, rule))
                    {
                        mapping.Rules.RemoveAll(r => r.SourceKey == rule.SourceKey);
                        mapping.Rules.Add(rule);
                        changed = true;
                        saved.Add(kv);
                    }
                }
                var oldHidden = (mapping.HiddenCategories ?? new List<string>()).OrderBy(c => c).ToList();
                if (!oldHidden.SequenceEqual(hidden, StringComparer.OrdinalIgnoreCase)) { mapping.HiddenCategories = hidden; changed = true; }
                if (string.IsNullOrWhiteSpace(mapping.ModelName) && mapping.Rules.Count > 0) changed = true;   // older mapping: record the name
                if (!changed) return;
                FamilyMapStore.Save(_doc, modelKey, mapping, _me, modelName);
                if (modelKey != _modelKey) return;
                _mapping = mapping;
                if (saved.Count > 0 && !_closing)
                {
                    foreach (var kv in saved)
                        if (kv.Key != null) { kv.Key.MappedTo = kv.Value.TargetName ?? ""; kv.Key.Tolerance = ToleranceText(kv.Value); }
                    _grid.Items.Refresh();
                    var first = saved[0].Value;
                    Status(saved.Count == 1 ? $"Saved: {first.SourceFamily} : {first.SourceType} → {first.TargetName}." : $"Saved {saved.Count} types.");
                }
            });
        }

        private static bool SameRule(FamilyMapRule a, FamilyMapRule b) =>
            a.TargetSymbolUid == b.TargetSymbolUid && (a.Workset ?? "") == (b.Workset ?? "") &&
            a.OffsetMm.SequenceEqual(b.OffsetMm) && a.RotationDeg.SequenceEqual(b.RotationDeg) &&
            a.ToleranceXyMm == b.ToleranceXyMm && a.ToleranceZMm == b.ToleranceZMm &&
            Newtonsoft.Json.JsonConvert.SerializeObject(a.Parameters ?? new List<ParamMap>()) == Newtonsoft.Json.JsonConvert.SerializeObject(b.Parameters ?? new List<ParamMap>());

        // ═══════════════ Parameter mapping ═══════════════

        /// <summary>Reads the consultant type's parameters (with values) and the local family type's (API context), once per type + target.</summary>
        private void LoadParams()
        {
            if (_current == null || _editing == null) return;
            var src = _current.Source;
            string targetUid = _editing.TargetSymbolUid;
            string key = src.Key + "|" + targetUid;
            if (key == _paramsFor) { FillParamList(); return; }
            _paramsFor = key;
            _paramInfo.Text = "Reading parameters…";
            string linkUid = _linkUid, linkName = _linkName;
            string srcKey = linkUid + "|" + src.Key;
            var known = srcKey == _srcFor ? _srcInfo : null;   // the consultant values only change with the consultant type
            Api(app =>
            {
                var s = known;
                if (s == null)
                {
                    var linkDoc = LinkedElementRef.FindInstance(_doc, linkUid)?.GetLinkDocument();
                    s = linkDoc == null ? new ParamDiscovery.SourceParams() : ParamDiscovery.Source(linkDoc, src.Uids, linkName);
                }
                var t = ParamDiscovery.Target(_doc, string.IsNullOrEmpty(targetUid) ? null : _doc.GetElement(targetUid) as ARDB.FamilySymbol);
                if (_paramsFor != key) return;
                _srcInfo = s; _dstInfo = t; _srcFor = srcKey;
                _loadingParams = true;
                try { _srcParam.ItemsSource = s.Params; _dstParam.ItemsSource = t.Params; }
                finally { _loadingParams = false; }
                FillParamList();
                UpdateParamInfo();
            });
        }

        private class ParamMapItem
        {
            internal ParamMap Map;
            internal string Text;
            public override string ToString() => Text;
        }

        private void FillParamList()
        {
            var keep = (_paramList.SelectedItem as ParamMapItem)?.Map.Id;
            _paramList.Items.Clear();
            foreach (var m in _editing?.Parameters ?? new List<ParamMap>())
            {
                var dst = _dstInfo.Params.FirstOrDefault(p => p.Key == m.Target?.Key);
                var c = string.IsNullOrEmpty(_editing.TargetSymbolUid) ? Compat.Warn("Pick the local family type first.")
                      : dst == null && _dstInfo.Params.Count > 0 ? Compat.Warn($"{m.Target?.Name} isn't on this family type.")
                      : ParamTransforms.Check(m.Source, m.Transform, dst ?? m.Target);
                string tx = ParamTransforms.Describe(m.Transform);
                var item = new ParamMapItem
                {
                    Map = m,
                    Text = $"{c.Symbol}  {(m.Source?.Name ?? (m.Transform?.Constant != null ? "(constant)" : "(expression)"))}  →  {m.Target?.Name}   ({(m.AlwaysApply ? "always apply" : "Link Sync approval")}{(tx.Length > 0 ? " · " + tx : "")})",
                };
                _paramList.Items.Add(item);
                if (m.Id == keep) _paramList.SelectedItem = item;
            }
            if (_paramList.Items.Count == 0) _paramList.Items.Add(new ParamMapItem { Text = "No parameters mapped." });
        }

        private void ShowParamMap()
        {
            var m = (_paramList.SelectedItem as ParamMapItem)?.Map;
            if (m == null) return;
            _loadingParams = true;
            try
            {
                _srcParam.SelectedItem = _srcInfo.Params.FirstOrDefault(p => p.Key == m.Source?.Key);
                _dstParam.SelectedItem = _dstInfo.Params.FirstOrDefault(p => p.Key == m.Target?.Key);
                _paramMode.SelectedIndex = m.AlwaysApply ? 1 : 0;
            }
            finally { _loadingParams = false; }
            UpdateParamInfo();
        }

        /// <summary>Compatibility of the picked pair + what the consultant values become.</summary>
        private void UpdateParamInfo()
        {
            var src = _srcParam.SelectedItem as ParamRef;
            var dst = _dstParam.SelectedItem as ParamRef;
            var tf = dst == null ? null : _editing?.Parameters?.FirstOrDefault(m => m.Target?.Key == dst.Key)?.Transform;
            if (tf != null && src == null && tf.Constant == null && !ParamTransforms.ExpressionOnly(null, tf)) tf = null;
            string note = _dstInfo.NoInstance && !string.IsNullOrEmpty(_editing?.TargetSymbolUid)
                ? "\nNo local family of this type is placed yet: its own instance parameters aren't listed (type and project parameters are). Test on one, then ↻."
                : "";
            if ((src == null && tf?.Constant == null && !ParamTransforms.ExpressionOnly(null, tf)) || dst == null)
            {
                _paramInfo.Foreground = Muted;
                _paramInfo.Text = (string.IsNullOrEmpty(_editing?.TargetSymbolUid) ? "Pick the local family type first." :
                                   $"Pick a consultant parameter ({_srcInfo.Params.Count}) and a local one ({_dstInfo.Params.Count(x => !x.ReadOnly)} writable).") + note;
                return;
            }
            var c = ParamTransforms.Check(src, tf, dst);
            bool perElement = ParamTransforms.UsesRow(tf) || ParamTransforms.ExpressionOnly(src, tf);
            var values = perElement ? Aligned(src) : PerKey(src);
            var p = ParamTransforms.Preview(src, tf, dst, values, 6, perElement ? Rows() : null);
            var lines = new List<string> { $"{c.Symbol} {c.Message}" };
            if (!ParamTransforms.IsIdentity(tf)) lines.Add("Transform: " + ParamTransforms.Describe(tf));
            if (c.Level != CompatLevel.Error)
            {
                lines.Add($"{p.Total:N0} consultant value(s): {p.Converted:N0} map, {p.Empty:N0} empty, {p.Failing:N0} fail" +
                          (src != null && dst.IsType && !src.IsType && src.Computed == null ? $", {p.Distinct:N0} distinct" : "") + ".");
                foreach (var sm in p.Samples) lines.Add($"   {sm.Item1}  →  {sm.Item2}");
                if (p.Problems.Count > 0) lines.Add("Fails: " + string.Join("; ", p.Problems));
            }
            _paramInfo.Text = string.Join("\n", lines) + note;
            _paramInfo.Foreground = c.Level == CompatLevel.Error ? Bad : c.Level == CompatLevel.Warn || p.Failing > 0 ? Frozen(0xB4, 0x6A, 0x00) : Ink;
        }

        private void AddParamMap()
        {
            if (_editing == null) return;
            var src = _srcParam.SelectedItem as ParamRef;
            var dst = _dstParam.SelectedItem as ParamRef;
            if (dst == null) { Status("Pick a local parameter (and a consultant one, or leave it empty for a constant value).", true); return; }
            _editing.Parameters = _editing.Parameters ?? new List<ParamMap>();
            var existing = _editing.Parameters.FirstOrDefault(m => m.Target?.Key == dst.Key);
            var tf = existing?.Transform;
            if (src == null && tf?.Constant == null && !ParamTransforms.ExpressionOnly(null, tf))
            {
                // No consultant parameter: a constant value (an expression of other parameters: Transform…).
                string k = TextPrompt.Ask(new System.Windows.Interop.WindowInteropHelper(this).Handle, "Constant value",
                    $"No consultant parameter is picked. Write the same value into {dst.Name} on every local family of this mapping (for a value made from other consultant parameters, cancel and use Transform…):", "");
                if (k == null) return;
                tf = new ParamTransform { Constant = k };
            }
            else if (src != null && tf?.Constant != null) tf = null;   // a consultant parameter picked: no longer a constant
            var c = ParamTransforms.Check(src, tf, dst);
            if (c.Level == CompatLevel.Error) { Status("Can't map: " + c.Message, true); return; }
            if (existing != null) { existing.Source = src; existing.Transform = tf; existing.AlwaysApply = _paramMode.SelectedIndex == 1; }
            else _editing.Parameters.Add(new ParamMap { Source = src, Target = dst, Transform = tf, AlwaysApply = _paramMode.SelectedIndex == 1 });
            ParamsChanged($"{src?.Name ?? (tf?.Constant != null ? $"Constant '{tf.Constant}'" : "Expression")} → {dst.Name} mapped" + (c.Level == CompatLevel.Warn ? " (with a warning)." : "."));
        }

        private void RemoveParamMap()
        {
            var m = (_paramList.SelectedItem as ParamMapItem)?.Map;
            if (m == null || _editing == null) { Status("Select a parameter mapping first."); return; }
            var answer = MessageBox.Show(this,
                $"Remove the mapping to {m.Target?.Name}.\n\nAlso clear the values it wrote on the local families?\n\n" +
                "Yes = clear them (values edited by hand since are kept)\nNo = leave the values as they are\nCancel = keep the mapping",
                "Remove parameter mapping", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            var edited = new HashSet<string>(new[] { _editing.Id }.Concat(_others.Select(o => o.Value.Id)));
            _editing.Parameters.RemoveAll(x => x.Id == m.Id);
            ParamsChanged($"{(m.Source?.Name ?? "(value)")} → {m.Target?.Name} removed.");
            if (answer != MessageBoxResult.Yes) return;
            // Mapping ids are copied between selected types: clear everywhere unless an unselected rule still uses it.
            bool usedElsewhere = _mapping?.Rules?.Any(r => !edited.Contains(r.Id) && r.Parameters?.Any(x => x.Id == m.Id) == true) == true;
            string linkUid = _linkUid;
            Api(app =>
            {
                var r = ParamValues.Clear(_doc, linkUid, m, usedElsewhere ? edited : null);
                Status($"{m.Target?.Name}: {r.Cleared:N0} value(s) cleared" +
                       (r.HandEdited > 0 ? $", {r.HandEdited:N0} edited by hand kept" : "") +
                       (r.Skipped > 0 ? $", {r.Skipped:N0} checked out by others skipped" : "") +
                       (r.Failed > 0 ? $", {r.Failed:N0} couldn't be cleared" : "") + ".", r.Failed > 0);
            }, needActive: true);
        }

        /// <summary>Preview values of a consultant parameter: one per element (instance) or per type.</summary>
        private List<SourceValue> PerKey(ParamRef src) =>
            src != null && _srcInfo.Values.TryGetValue(src.Key, out var v) ? v : new List<SourceValue>();

        /// <summary>One value per consultant element (lined up with Rows()), for expressions reading other parameters.</summary>
        private List<SourceValue> Aligned(ParamRef src) =>
            _srcInfo.Elements.Select(e => src != null && e.Values.TryGetValue(src.Key, out var v) ? v : new SourceValue()).ToList();

        private List<Dictionary<string, string>> Rows() => _srcInfo.Elements.Select(e => e.Row).ToList();

        /// <summary>Edits the transform of the selected mapping (or of the picked pair, mapping it first) with a live preview.</summary>
        private void EditTransform()
        {
            if (_editing == null) return;
            var m = (_paramList.SelectedItem as ParamMapItem)?.Map;
            var dst = _dstParam.SelectedItem as ParamRef;
            if (m == null && dst != null) m = _editing.Parameters?.FirstOrDefault(x => x.Target?.Key == dst.Key);
            if (m == null)
            {
                var src0 = _srcParam.SelectedItem as ParamRef;
                if (dst == null) { Status("Select a parameter mapping (or pick a local parameter) first.", true); return; }
                m = new ParamMap { Source = src0, Target = dst, AlwaysApply = _paramMode.SelectedIndex == 1 };
                if (src0 == null) m.Transform = new ParamTransform { Constant = "" };
            }
            var target = _dstInfo.Params.FirstOrDefault(p => p.Key == m.Target?.Key) ?? m.Target;
            var dlg = new TransformDialog(m.Source, target, m.Transform, PerKey(m.Source), Aligned(m.Source), Rows(), _srcInfo.RowNames) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var tf = dlg.Result;
            if (m.Source == null && tf?.Constant == null && !ParamTransforms.ExpressionOnly(null, tf))
            { Status("Without a consultant parameter the mapping needs a constant value or an expression.", true); return; }
            var c = ParamTransforms.Check(m.Source, tf, target);
            if (c.Level == CompatLevel.Error) { Status("Not changed: " + c.Message, true); return; }
            m.Transform = ParamTransforms.IsIdentity(tf) ? null : tf;
            _editing.Parameters = _editing.Parameters ?? new List<ParamMap>();
            if (!_editing.Parameters.Contains(m)) _editing.Parameters.Add(m);
            ParamsChanged($"{m.Target?.Name}: " + (m.Transform == null ? "copied as it is." : ParamTransforms.Describe(m.Transform) + "."));
            UpdateParamInfo();
        }

        /// <summary>Adds a local parameter for the picked consultant parameter (mapping/ParamCreator) and maps it.</summary>
        private void AddLocalParam()
        {
            var src = _srcParam.SelectedItem as ParamRef;
            if (src == null) { Status("Pick the consultant parameter first: the new local parameter is made for it.", true); return; }
            var tt = _targets.FirstOrDefault(t => t.Uid == _editing?.TargetSymbolUid);
            if (tt == null) { Status("Pick the local family type first.", true); return; }
            var dlg = new AddParameterDialog(src, tt.Category) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            CreateParam(new ParamCreator.Request { Name = dlg.ParamName, Spec = dlg.Spec, IsType = dlg.IsTypeParam, Category = tt.Category, SourceName = src.Name }, src);
        }

        private void CreateParam(ParamCreator.Request req, ParamRef src)
        {
            Status($"Adding '{req.Name}'…");
            Api(app =>
            {
                var r = ParamCreator.Ensure(_doc, req);
                if (r.Conflict != null)
                {
                    if (MessageBox.Show(this, r.Conflict, "Add local parameter", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    { req.Force = true; CreateParam(req, src); }
                    else Status("Not added.");
                    return;
                }
                if (r.Error != null) { Status("Couldn't add the parameter: " + r.Error, true); return; }
                if (_editing == null) return;
                var c = ParamCompat.Check(src, r.Ref);
                _editing.Parameters = _editing.Parameters ?? new List<ParamMap>();
                if (c.Level != CompatLevel.Error)
                {
                    var existing = _editing.Parameters.FirstOrDefault(m => m.Target?.Key == r.Ref.Key);
                    if (existing != null) existing.Source = src;
                    else _editing.Parameters.Add(new ParamMap { Source = src, Target = r.Ref, AlwaysApply = _paramMode.SelectedIndex == 1 });
                }
                string what = r.Outcome == "created" ? "added" : r.Outcome == "extended" ? "already in the model: added to this category" : "already in the model";
                ParamsChanged($"'{r.Ref.Name}' {what}" + (c.Level == CompatLevel.Error ? $", but not mapped: {c.Message}" : $"; mapped from {src.Name}."));
                _paramsFor = null;
                LoadParams();
            }, needActive: true);
        }

        /// <summary>Parameter mappings edited: the other selected types get the same list (multi-select), then saved.</summary>
        private void ParamsChanged(string message)
        {
            foreach (var o in _others) o.Value.Parameters = CloneParams(_editing.Parameters);
            FillParamList();
            QueueSave();
            Status(message + (_others.Count > 0 ? $" Applied to all {_others.Count + 1} selected types." : "") +
                   (string.IsNullOrEmpty(_editing.TargetSymbolUid) ? " (Saved once a local family type is picked.)" : ""));
        }

        private void QueueLive()
        {
            if (_testId == null || _testIsExisting) return;
            _liveTimer.Stop();
            _liveTimer.Start();
        }

        // ═══════════════ Actions ═══════════════

        /// <param name="fresh">New test (zoom to it); false = re-place while tuning (same consultant element, view left alone).</param>
        private void TestOnOne() => TestOnOne(true);

        private void TestOnOne(bool fresh)
        {
            if (_editing == null || !ReadEditor() || string.IsNullOrEmpty(_editing.TargetSymbolUid)) { Status("Pick the local family type first.", true); return; }
            var rule = Copy(_editing);
            int sample = _sample;
            var old = _testId;
            string keepSource = fresh ? null : _testSourceUid;
            Api(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var linkDoc = inst?.GetLinkDocument();
                var symbol = _doc.GetElement(rule.TargetSymbolUid) as ARDB.FamilySymbol;
                if (linkDoc == null || symbol == null) { Status("The link isn't loaded or the local family type is gone.", true); return; }
                var sources = FamilyPlacer.SourceElements(linkDoc, rule);
                if (sources.Count == 0) { Status("No consultant elements of this type in the link.", true); return; }
                var uidoc = app.ActiveUIDocument;
                // Elements visible in the active view come first (worked out once per type + view, so ◀ / ▶ keep their place).
                var view = uidoc.ActiveView;
                string orderKey = rule.SourceKey + "|" + _linkUid + "|" + (view?.Id.GetIdValue() ?? 0);
                if (orderKey != _testOrderKey)
                {
                    HashSet<long> visible = null;
                    try { visible = ReviewNavigator.VisibleLinkedIds(_doc, view, inst); } catch (Exception ex) { Log.Info($"[Mapping] visible consultant elements: {ex.Message}"); }
                    visible = visible ?? new HashSet<long>();
                    _testOrder = sources.OrderBy(x => visible.Contains(x.Id.GetIdValue()) ? 0 : 1).Select(x => x.UniqueId).ToList();
                    _testInView = sources.Count(x => visible.Contains(x.Id.GetIdValue()));
                    _testOrderKey = orderKey;
                    if (keepSource == null) sample = 0;   // new view: start at the first one in it
                }
                var rank = new Dictionary<string, int>();
                for (int k = 0; k < _testOrder.Count; k++) rank[_testOrder[k]] = k;
                sources = sources.OrderBy(x => rank.TryGetValue(x.UniqueId, out int k) ? k : int.MaxValue).ToList();
                // The consultant element selected in Revit, when it's of this type; else the n-th sample.
                ARDB.Element source = keepSource == null ? null : sources.FirstOrDefault(x => x.UniqueId == keepSource);
                if (source == null)
                try
                {
                    foreach (var r in uidoc.Selection.GetReferences())
                        if (r.ElementId == inst.Id && r.LinkedElementId != ARDB.ElementId.InvalidElementId)
                        {
                            var el = linkDoc.GetElement(r.LinkedElementId);
                            if (el != null && sources.Any(x => x.Id == el.Id)) { source = el; break; }
                        }
                }
                catch { /* GetReferences unavailable */ }
                int n = ((sample % sources.Count) + sources.Count) % sources.Count;
                source = source ?? sources[n];
                int index = sources.FindIndex(x => x.Id == source.Id);
                _sample = index;
                string warning;
                ARDB.FamilyInstance fi;
                FamilyPlacer.Quiet quiet;
                ARDB.ElementId placedId = null;
                using (var tx = new ARDB.Transaction(_doc, "Perseus: test mapped family"))
                {
                    tx.Start();
                    quiet = new FamilyPlacer.Quiet().On(tx);
                    if (old != null && !_testIsExisting && _doc.GetElement(old) != null) _doc.Delete(old);
                    fi = FamilyPlacer.Place(_doc, inst, rule, symbol, source, FamilyPlacer.Levels(_doc), FamilyPlacer.WorksetByName(_doc, rule.Workset), _me, out warning);
                    placedId = fi?.Id;
                    tx.Commit();
                }
                // Revit can refuse it when the transaction commits (an error it resolved by deleting the family, or a roll back).
                if (fi != null && !fi.IsValidObject) fi = null;
                if (fi == null && placedId != null)
                {
                    warning = "Revit refused it" + (quiet.Errors.Count > 0 ? ": " + string.Join(" ", quiet.Errors) : "") +
                              (warning != null ? $" ({warning})" : "");
                    Log.Info($"[Mapping] test family refused by Revit: {warning}");
                }
                if (_testIsExisting) { old = null; _testIsExisting = false; }   // the team's family stays, it's just no longer the test
                if (quiet.RolledBack && old != null && _doc.GetElement(old) == null) old = null;
                _testId = fi?.Id ?? (quiet.RolledBack && old != null ? old : null);
                _testSourceUid = source.UniqueId;
                if (fi == null) { Status("Not placed: " + warning, true); _testInfo.Text = ""; return; }
                _testSource = FamilyPlacer.SourceFrame(source, inst.GetTotalTransform());
                _testExpected = MappingMath.Place(_testSource, rule.OffsetMm, rule.RotationDeg);
                _testPlaced = FamilyPlacer.FrameOf(fi);
                var srcPos = FamilyPlacer.SourceFrame(source, inst.GetTotalTransform()).O;
                var ourPos = FamilyPlacer.Position(fi);
                string level = (source.get_Parameter(ARDB.BuiltInParameter.FAMILY_LEVEL_PARAM) ?? source.get_Parameter(ARDB.BuiltInParameter.SCHEDULE_LEVEL_PARAM))?.AsValueString();
                _testInfo.Text = $"Test: our {symbol.FamilyName} (Id {fi.Id.GetIdValue()}) at consultant element Id {source.Id.GetIdValue()}" +
                                 (string.IsNullOrEmpty(level) ? "" : $" on {level}") + $" · sample {index + 1} of {sources.Count}" +
                                 (_testInView == 0 ? " (none in this view)" : index < _testInView ? $" ({index + 1} of {_testInView} in this view)" : $" (outside this view; {_testInView} in it)") +
                                 (ourPos != null ? $" · {MappingMath.Distance(srcPos, ourPos) * MappingMath.MmPerFoot:N0} mm from its insertion point" : "");
                if (fresh) ShowTest(app, inst, fi, source, false, false, false);
                Status(fresh ? "Test placed. Adjust the offset / rotation (it re-places in place), or move / rotate it or change its type in Revit (the mapping follows), ◀ / ▶ to check other elements. Changes save automatically."
                             : "Test re-placed." + (warning != null ? "" : ""), warning != null);
                if (warning != null) Status("⚠ " + warning, true);
            }, needActive: true);
        }

        /// <summary>
        /// The user changed the test family's type in Revit (type selector, or a new type via Edit Type > Duplicate): use
        /// that type in the rule being edited. Our own test transactions (re-placing) are ignored. Runs in Revit's
        /// DocumentChanged (no transactions here); the editor is updated just after.
        /// </summary>
        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                var id = _testId;
                if (id == null || _editing == null) return;
                var doc = e.GetDocument();
                if (doc == null || !doc.Equals(_doc)) return;
                if (e.GetTransactionNames().Any(n => n.StartsWith("Perseus", StringComparison.Ordinal))) return;
                if (!e.GetModifiedElementIds().Contains(id)) return;
                var fi = doc.GetElement(id) as ARDB.FamilyInstance;
                var symbol = fi?.Symbol;
                if (symbol == null) return;
                if (symbol.UniqueId == _editing.TargetSymbolUid) { CaptureTestMove(fi); return; }
                _testPlaced = FamilyPlacer.FrameOf(fi);   // a new type may stand differently: measure later moves from here
                bool known = _targets.Any(t => t.Uid == symbol.UniqueId);
                var targets = known ? null : FamilyPlacer.TargetTypes(doc);   // a type made just now (Duplicate)
                string uid = symbol.UniqueId, name = $"{symbol.FamilyName} : {symbol.Name}";
                string rule = _editing.Id;
                Dispatcher.BeginInvoke(new Action(() => Safe(() => TakeTestType(rule, uid, name, targets, !known))));
            }
            catch (Exception ex) { Log.Info($"[Mapping] watching the test family: {ex.Message}"); }
        }

        /// <summary>
        /// The user moved / rotated the test family in Revit: work out the offset and rotation that place it there and put them
        /// in the editor (saved; not re-placed, it already stands there). DocumentChanged context.
        /// </summary>
        private void CaptureTestMove(ARDB.FamilyInstance fi)
        {
            if (_testSource == null || _testExpected == null || _testPlaced == null || _others.Count > 0) return;
            var now = FamilyPlacer.FrameOf(fi);
            if (now == null) return;
            var d = MappingMath.Delta(now, _testPlaced);
            if (d.TotalMm < 0.05 && d.AngleDeg < 0.005) return;   // a parameter edit, not a move
            var rule = MappingMath.Corrected(_testExpected, _testPlaced, now);
            if (!MappingMath.Solve(_testSource, rule, (double[])_editing.RotationDeg.Clone(), out var off, out var rot))
            {
                Status("The test family was mirrored in Revit: a mapping can only move and rotate it, so this wasn't taken. Undo the mirror, or use the rotation fields.", true);
                _testPlaced = now;
                return;
            }
            string ruleId = _editing.Id;
            var placedNow = now;
            Dispatcher.BeginInvoke(new Action(() => Safe(() =>
            {
                if (_editing == null || _editing.Id != ruleId) return;
                _loadingEditor = true;
                try { for (int i = 0; i < 3; i++) { _offset[i].Text = Num(off[i]); _rotation[i].Text = Num(rot[i]); } }
                finally { _loadingEditor = false; }
                if (!ReadEditor()) return;
                _testExpected = MappingMath.Place(_testSource, _editing.OffsetMm, _editing.RotationDeg);
                _testPlaced = placedNow;
                QueueSave();
                Status($"Moved in Revit: offset {Num(off[0])} / {Num(off[1])} / {Num(off[2])} mm, rotation {Num(rot[0])} / {Num(rot[1])} / {Num(rot[2])}° taken into the mapping (saved).");
                Log.Info($"[Mapping] test family moved in Revit -> offset {string.Join(",", off)} rotation {string.Join(",", rot)}");
            })));
        }

        private void TakeTestType(string ruleId, string uid, string name, List<FamilyPlacer.TargetType> targets, bool isNew)
        {
            if (_editing == null || _editing.Id != ruleId || _current == null) return;
            if (targets != null) { _targets = targets; FillTargetCategories(); }
            FillTargets(uid);
            if (_target.SelectedItem == null) { Status($"The test family is now {name}, which Perseus can't place: not taken into the mapping.", true); return; }
            if (ReadEditor()) QueueSave();
            UpdateNote();
            Status($"Type changed in Revit: {(isNew ? "new type " : "")}{name} is now this mapping's type (saved).");
            Log.Info($"[Mapping] test family type changed in Revit -> {name}{(isNew ? " (new type)" : "")}");
        }

        /// <summary>Zoom / select the test family and its consultant element.</summary>
        private void FindTest(bool force3D, bool zoom, bool selectConsultant, bool selectOnly = false)
        {
            if (_testId == null || _testSourceUid == null) { Status("Place a test first (Test on one)."); return; }
            var id = _testId; var srcUid = _testSourceUid;
            Api(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var fi = _doc.GetElement(id);
                var source = inst?.GetLinkDocument()?.GetElement(srcUid);
                if (fi == null || source == null) { Status("The test family or its consultant element is gone: Test on one again.", true); return; }
                ShowTest(app, inst, fi, source, force3D, selectOnly, selectConsultant);
            }, needActive: true);
        }

        private void ShowTest(UIApplication app, ARDB.RevitLinkInstance inst, ARDB.Element ours, ARDB.Element source, bool force3D, bool selectOnly, bool selectConsultant)
        {
            var uidoc = app.ActiveUIDocument;
            try
            {
                if (selectConsultant) uidoc.Selection.SetReferences(new List<ARDB.Reference> { new ARDB.Reference(source).CreateLinkReference(inst) });
                else uidoc.Selection.SetElementIds(new List<ARDB.ElementId> { ours.Id });
            }
            catch (Exception ex) { Log.Info($"[Mapping] select: {ex.Message}"); }
            if (selectOnly) { Status(selectConsultant ? "Consultant element selected." : "Our test family selected."); return; }
            // Box around both (consultant box moved into host coordinates).
            var a = ours.get_BoundingBox(null);
            var b = source.get_BoundingBox(null);
            var t = inst.GetTotalTransform();
            var pts = new List<ARDB.XYZ>();
            if (a != null) { pts.Add(a.Min); pts.Add(a.Max); }
            if (b != null) for (int i = 0; i < 8; i++)
                pts.Add(t.OfPoint(new ARDB.XYZ((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z)));
            if (pts.Count == 0) { Status("No location to zoom to.", true); return; }
            var min = new ARDB.XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
            var max = new ARDB.XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
            ReviewViewSettings views = null;
            try { views = ReviewViews.Load(_doc); } catch { }
            string where = ReviewNavigator.ShowBox(uidoc, inst, min, max, views, force3D);
            Status($"Showing the test and its consultant element {where}.");
        }

        /// <summary>Deletes the temporary test family; on close also the Perseus work planes nothing stands on any more.</summary>
        private void RemoveTest() => RemoveTest(false);

        private void RemoveTest(bool cleanPlanes)
        {
            var id = _testId;
            if (_testIsExisting) id = null;   // the team's own family: only forget it
            _testIsExisting = false;
            _testId = null;
            _testSourceUid = null;
            _testInfo.Text = "";
            if (id == null && !cleanPlanes) return;
            Api(app =>
            {
                bool hasTest = id != null && _doc.GetElement(id) != null;
                var planes = cleanPlanes ? FamilyPlacer.UnusedPerseusPlanes(_doc, id) : new List<ARDB.ElementId>();
                if (!hasTest && planes.Count == 0) return;
                using (var tx = new ARDB.Transaction(_doc, "Perseus: remove test family"))
                {
                    tx.Start();
                    if (hasTest) _doc.Delete(id);
                    foreach (var pid in planes) { try { if (_doc.GetElement(pid) != null) _doc.Delete(pid); } catch { } }
                    tx.Commit();
                }
                if (planes.Count > 0) Log.Info($"[Mapping] removed {planes.Count} empty Perseus work plane(s)");
                if (!hasTest) return;
                Status("Test family removed.");
            });
        }

        private void RemoveMapping()
        {
            if (_editing == null) return;
            _saveTimer.Stop();
            var keys = new HashSet<string>(new[] { _editing.SourceKey }.Concat(_others.Select(o => o.Value.SourceKey)));
            if (keys.Count > 1 && MessageBox.Show(this, $"Clear the mapping of {keys.Count} types?", "Family mapping", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            foreach (var o in _others) { o.Value.TargetSymbolUid = null; o.Value.TargetName = null; }
            _loadingEditor = true;
            _target.SelectedItem = null;
            _target.Text = "";
            _loadingEditor = false;
            _editing.TargetSymbolUid = null; _editing.TargetName = null;
            UpdateNote();
            RemoveTest();
            Api(app =>
            {
                var mapping = FamilyMapStore.Load(_doc, _modelKey);
                int n = mapping.Rules.RemoveAll(r => keys.Contains(r.SourceKey));
                if (n == 0) { Status(keys.Count == 1 ? "This type isn't mapped." : "None of these types is mapped."); return; }
                FamilyMapStore.Save(_doc, _modelKey, mapping, _me, _modelName);
                _mapping = mapping;
                BuildRows();
                Fill();
                Status(n == 1 ? "Mapping cleared." : $"Mapping cleared for {n} types.");
            });
        }

        private void RefreshCounts()
        {
            _mappedByRule = MappedFamilyStore.All(_doc, _linkUid).GroupBy(r => r.RuleId ?? "").ToDictionary(g => g.Key, g => g.Count());
            BuildRows();
            Fill();
        }

        // ═══════════════ Helpers ═══════════════

        private static FamilyMapRule Copy(FamilyMapRule r) => new FamilyMapRule
        {
            Id = r.Id, Enabled = r.Enabled, SourceCategory = r.SourceCategory, SourceFamily = r.SourceFamily, SourceType = r.SourceType,
            TargetSymbolUid = r.TargetSymbolUid, TargetName = r.TargetName, OffsetMm = (double[])r.OffsetMm.Clone(),
            RotationDeg = (double[])r.RotationDeg.Clone(), Workset = r.Workset, Author = r.Author, SavedUtc = r.SavedUtc,
            ToleranceXyMm = r.ToleranceXyMm, ToleranceZMm = r.ToleranceZMm, Parameters = CloneParams(r.Parameters),
        };

        private static List<ParamMap> CloneParams(List<ParamMap> list) =>
            Newtonsoft.Json.JsonConvert.DeserializeObject<List<ParamMap>>(Newtonsoft.Json.JsonConvert.SerializeObject(list ?? new List<ParamMap>())) ?? new List<ParamMap>();

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
                    Log.Warn($"[Mapping] {ex}");
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
            catch (Exception ex) { Log.Warn($"[Mapping] UI: {ex}"); Status("Something went wrong: " + ex.Message, true); }
        }

        private static bool TryNum(string s, out double v) =>
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
            double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        private static string Num(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.CurrentCulture);

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 14, 0, 4) };

        private static Button Nudge(string text, Action click)
        {
            var b = new Button { Content = text, Width = 22, Margin = new Thickness(0, 0, 1, 0), Background = Brushes.White, Padding = new Thickness(0) };
            b.Click += (s, e) => { try { click(); } catch (Exception ex) { Log.Exception(ex); } };
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
