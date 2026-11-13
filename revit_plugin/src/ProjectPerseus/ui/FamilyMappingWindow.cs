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
            internal string Uid, Name, ModelKey;
            public override string ToString() => Name;
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
            foreach (ARDB.RevitLinkInstance inst in new ARDB.FilteredElementCollector(doc).OfClass(typeof(ARDB.RevitLinkInstance)))
            {
                var linkDoc = inst.GetLinkDocument();
                if (linkDoc == null) continue;   // unloaded: nothing to map from
                string name = doc.GetElement(inst.GetTypeId())?.Name ?? inst.Name;
                links.Add(new LinkChoice { Uid = inst.UniqueId, Name = name, ModelKey = VersionCatalog.ModelKey(doc, inst, linkDoc, name) });
            }
            if (links.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Family mapping", "This model has no loaded Revit links to map from.");
                return;
            }
            foreach (var g in links.GroupBy(l => l.Name).Where(g => g.Count() > 1)) { int i = 1; foreach (var l in g) l.Name += $"  #{i++}"; }
            // Default: the link open in the federator panel, else the first.
            string preferred = FederatorWindow.Instance?.Record?.LinkInstanceUid;
            var w = new FamilyMappingWindow(doc, app.Application.Username, links.OrderBy(l => l.Name).ToList(), preferred);
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
        private readonly ComboBox _target, _workset;
        private readonly TextBox[] _offset = new TextBox[3], _rotation = new TextBox[3];
        private readonly TextBox _stepMm, _stepDeg;
        private readonly StackPanel _editor;
        private readonly Button _test, _next, _removeTest;
        private readonly DispatcherTimer _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        private readonly MultiPick _categories;
        private bool _closing;

        private Row _current;
        private FamilyMapRule _editing;
        private ARDB.ElementId _testId;
        private string _testSourceUid;       // the consultant element the test stands at (kept while tuning)
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
                Text = "Map the consultant's family types to ours: which of our types stands for each of theirs, with its offset and rotation. Types people " +
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
            _linkBox = new ComboBox { Width = 240, Margin = new Thickness(0, 0, 14, 0), VerticalContentAlignment = VerticalAlignment.Center };
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
                AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush, Background = Brushes.White,
                RowHeaderWidth = 0, CanUserAddRows = false,
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "Category", Binding = new System.Windows.Data.Binding(nameof(Row.Category)), Width = new DataGridLength(120) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Consultant type", Binding = new System.Windows.Data.Binding(nameof(Row.Type)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "In link", Binding = new System.Windows.Data.Binding(nameof(Row.Count)), Width = new DataGridLength(55) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Triage", Binding = new System.Windows.Data.Binding(nameof(Row.Triage)), Width = new DataGridLength(90) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Mapped to", Binding = new System.Windows.Data.Binding(nameof(Row.MappedTo)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            Grid.SetColumn(_grid, 0);
            body.Children.Add(_grid);

            _editor = new StackPanel();
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
            _editor.Children.Add(_title);
            _editor.Children.Add(Section("Our family type"));
            _target = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, StaysOpenOnEdit = true };
            _editor.Children.Add(_target);
            _placementNote = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontSize = 11 };
            _editor.Children.Add(_placementNote);

            _editor.Children.Add(Section("Offset (mm): X / Y along the consultant element in plan, Z = up"));
            _stepMm = new TextBox { Text = "50", Width = 50 };
            _editor.Children.Add(Vector(_offset, _stepMm, "Step mm"));
            _editor.Children.Add(Section("Rotation (° about the consultant element's X, then Y, then Z)"));
            _stepDeg = new TextBox { Text = "90", Width = 50 };
            _editor.Children.Add(Vector(_rotation, _stepDeg, "Step °"));

            _editor.Children.Add(Section("Workset for placed families"));
            _workset = new ComboBox();
            _editor.Children.Add(_workset);

            _editor.Children.Add(Section("Try it"));
            var testRow = new WrapPanel();
            _test = Btn("Test on one", "Place a temporary family of ours at a consultant element of this type (the one selected in Revit if any) and zoom to it. Changing the offset / rotation re-places it; changing its type in Revit changes the mapping. Removed when you move on or close.", TestOnOne);
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
            findRow.Children.Add(Btn("Select ours", "Select our test family in Revit (e.g. to check it in Properties)", () => FindTest(false, false, false, selectOnly: true)));
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
            _grid.SelectionChanged += (s, e) => Safe(() => Select(_grid.SelectedItem as Row));
            _target.SelectionChanged += (s, e) => Safe(() => { if (!_loadingEditor && ReadEditor()) { UpdateNote(); QueueLive(); QueueSave(); } });
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
            };
            // Watch the test family: a type change made in Revit (type selector, or Edit Type > Duplicate) is taken into the mapping.
            Api(app => app.Application.DocumentChanged += OnDocumentChanged);
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
            _linkUid = l.Uid; _modelKey = l.ModelKey; _linkName = l.Name;
            Title = "Perseus — Family mapping · " + l.Name;
            _current = null; _editing = null;
            _editor.IsEnabled = false;
            var fed = FederatorWindow.Instance;
            _triage = fed != null && fed.Record != null ? fed.TriageByType(l.Uid) : new Dictionary<string, TypeTriage>();
            _triageLabel = fed?.TriageSourceLabel(l.Uid);
            Api(app =>
            {
                _replacementMap = ReplacementLinks.Map(ReplacementLinks.Fold(ReviewStore.Load(_doc), l.Uid));
                Reload();
            });
        }

        private string _triageLabel;

        // ═══════════════ Loading ═══════════════

        private void Reload()
        {
            Status("Reading the link and our families…");
            Api(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var linkDoc = inst?.GetLinkDocument();
                if (linkDoc == null) { Status("The link isn't loaded: load it in Manage Links first.", true); return; }
                _sources = FamilyPlacer.SourceTypes(linkDoc);
                _targets = FamilyPlacer.TargetTypes(_doc);
                _mapping = FamilyMapStore.Load(_doc, _modelKey);
                _mappedByRule = MappedFamilyStore.All(_doc, _linkUid).GroupBy(r => r.RuleId ?? "").ToDictionary(g => g.Key, g => g.Count());
                _loadingEditor = true;
                _workset.Items.Clear();
                _workset.Items.Add(ActiveWorkset);
                foreach (var w in FamilyPlacer.Worksets(_doc)) _workset.Items.Add(w);
                _workset.IsEnabled = _doc.IsWorkshared;
                _loadingEditor = false;
                var hidden = new HashSet<string>(_mapping.HiddenCategories ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                _categories.SetItems(_sources.GroupBy(x => x.Category).OrderBy(g => g.Key)
                    .Select(g => new MultiPick.Item(g.Key, $"{g.Key}  ({g.Count()})")));
                foreach (var c in _sources.Select(x => x.Category).Distinct()) _categories.SetTicked(c, !hidden.Contains(c));
                BuildRows();
                Fill();
                Status($"{_sources.Count} consultant family type(s), {_targets.Count} of our family types, {_mapping.Rules.Count} mapped. " +
                       (_triageLabel != null ? $"Suggestions from the triage of {_triageLabel}."
                                             : "No triage for this link: open one of its comparisons in Link Changes to get suggestions (all types are listed meanwhile)."));
                if (_triageLabel == null) _suggestedOnly.IsChecked = false;
            });
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
                    Mapped = rule != null && _mappedByRule.TryGetValue(rule.Id, out int n) ? $"{n} / {s.Uids.Count}" : "",
                });
            }
        }

        private void Fill()
        {
            string q = _filter.Text?.Trim() ?? "";
            bool only = _suggestedOnly.IsChecked == true;
            var keep = _current?.Source.Key;
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
            _target.Items.Clear();
            // Our types of the same category first.
            foreach (var t in _targets.OrderBy(t => t.Category == s.Category ? 0 : 1).ThenBy(t => t.Category).ThenBy(t => t.Preference).ThenBy(t => t.Family).ThenBy(t => t.Type))
                _target.Items.Add(new TargetItem(t, t.Category == s.Category));
            _target.SelectedItem = _target.Items.Cast<TargetItem>().FirstOrDefault(i => i.T.Uid == _editing.TargetSymbolUid);
            for (int i = 0; i < 3; i++)
            {
                _offset[i].Text = Num(_editing.OffsetMm[i]);
                _rotation[i].Text = Num(_editing.RotationDeg[i]);
            }
            _workset.SelectedItem = string.IsNullOrEmpty(_editing.Workset) || !_workset.Items.Contains(_editing.Workset) ? ActiveWorkset : _editing.Workset;
            _loadingEditor = false;
            _editor.IsEnabled = true;
            UpdateNote();
            Status($"{s.Uids.Count} in the link · triage: {row.Stats.Describe()}" +
                   (_mappedByRule.TryGetValue(_editing.Id, out int n) ? $" · {n} of ours mapped" : ""));
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
                : $"Needs a host ({t.Placement}): can't be placed floating. Pick a work-plane- or level-based family.";
            if (t != null && t.FaceBased) _placementNote.Foreground = Frozen(0xB4, 0x6A, 0x00);
            _placementNote.Foreground = t != null && !t.Floating ? Bad : t != null && t.FaceBased ? Frozen(0xB4, 0x6A, 0x00) : Muted;
            bool ok = t != null && t.Floating;
            _test.IsEnabled = _next.IsEnabled = ok;
        }

        /// <summary>Reads the editor into _editing; false (with a message) when a number doesn't parse.</summary>
        private bool ReadEditor()
        {
            if (_editing == null) return false;
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
            if (_modelKey == null) return;
            FamilyMapRule rule = null;
            if (_editing != null && !string.IsNullOrEmpty(_editing.TargetSymbolUid))
            {
                rule = Copy(_editing);
                rule.Author = _me;
                rule.SavedUtc = DateTime.UtcNow;
            }
            var hidden = _rows.Select(r => r.Category).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !_categories.IsTicked(c))
                              .OrderBy(c => c).ToList();
            string modelKey = _modelKey;
            var row = _current;
            Api(app =>
            {
                var mapping = FamilyMapStore.Load(_doc, modelKey);
                bool changed = false;
                if (rule != null)
                {
                    var was = mapping.Rules.FirstOrDefault(r => r.SourceKey == rule.SourceKey);
                    if (was == null || !SameRule(was, rule))
                    {
                        mapping.Rules.RemoveAll(r => r.SourceKey == rule.SourceKey);
                        mapping.Rules.Add(rule);
                        changed = true;
                    }
                }
                var oldHidden = (mapping.HiddenCategories ?? new List<string>()).OrderBy(c => c).ToList();
                if (!oldHidden.SequenceEqual(hidden, StringComparer.OrdinalIgnoreCase)) { mapping.HiddenCategories = hidden; changed = true; }
                if (!changed) return;
                FamilyMapStore.Save(_doc, modelKey, mapping, _me);
                if (modelKey != _modelKey) return;
                _mapping = mapping;
                if (rule != null && row != null && !_closing)
                {
                    row.MappedTo = rule.TargetName ?? "";
                    _grid.Items.Refresh();
                    Status($"Saved: {rule.SourceFamily} : {rule.SourceType} → {rule.TargetName}.");
                }
            });
        }

        private static bool SameRule(FamilyMapRule a, FamilyMapRule b) =>
            a.TargetSymbolUid == b.TargetSymbolUid && (a.Workset ?? "") == (b.Workset ?? "") &&
            a.OffsetMm.SequenceEqual(b.OffsetMm) && a.RotationDeg.SequenceEqual(b.RotationDeg);

        private void QueueLive()
        {
            if (_testId == null) return;
            _liveTimer.Stop();
            _liveTimer.Start();
        }

        // ═══════════════ Actions ═══════════════

        /// <param name="fresh">New test (zoom to it); false = re-place while tuning (same consultant element, view left alone).</param>
        private void TestOnOne() => TestOnOne(true);

        private void TestOnOne(bool fresh)
        {
            if (_editing == null || !ReadEditor() || string.IsNullOrEmpty(_editing.TargetSymbolUid)) { Status("Pick our family type first.", true); return; }
            var rule = Copy(_editing);
            int sample = _sample;
            var old = _testId;
            string keepSource = fresh ? null : _testSourceUid;
            Api(app =>
            {
                var inst = LinkedElementRef.FindInstance(_doc, _linkUid);
                var linkDoc = inst?.GetLinkDocument();
                var symbol = _doc.GetElement(rule.TargetSymbolUid) as ARDB.FamilySymbol;
                if (linkDoc == null || symbol == null) { Status("The link isn't loaded or our family type is gone.", true); return; }
                var sources = FamilyPlacer.SourceElements(linkDoc, rule);
                if (sources.Count == 0) { Status("No consultant elements of this type in the link.", true); return; }
                // The consultant element selected in Revit, when it's of this type; else the n-th sample.
                ARDB.Element source = keepSource == null ? null : sources.FirstOrDefault(x => x.UniqueId == keepSource);
                var uidoc = app.ActiveUIDocument;
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
                using (var tx = new ARDB.Transaction(_doc, "Perseus: test mapped family"))
                {
                    tx.Start();
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new FamilyPlacer.Quiet());
                    tx.SetFailureHandlingOptions(fo);
                    if (old != null && _doc.GetElement(old) != null) _doc.Delete(old);
                    fi = FamilyPlacer.Place(_doc, inst, rule, symbol, source, FamilyPlacer.Levels(_doc), FamilyPlacer.WorksetByName(_doc, rule.Workset), _me, out warning);
                    tx.Commit();
                }
                _testId = fi?.Id;
                _testSourceUid = source.UniqueId;
                if (fi == null) { Status("Not placed: " + warning, true); _testInfo.Text = ""; return; }
                var srcPos = FamilyPlacer.SourceFrame(source, inst.GetTotalTransform()).O;
                var ourPos = FamilyPlacer.Position(fi);
                string level = (source.get_Parameter(ARDB.BuiltInParameter.FAMILY_LEVEL_PARAM) ?? source.get_Parameter(ARDB.BuiltInParameter.SCHEDULE_LEVEL_PARAM))?.AsValueString();
                _testInfo.Text = $"Test: our {symbol.FamilyName} (Id {fi.Id.GetIdValue()}) at consultant element Id {source.Id.GetIdValue()}" +
                                 (string.IsNullOrEmpty(level) ? "" : $" on {level}") + $" · sample {index + 1} of {sources.Count}" +
                                 (ourPos != null ? $" · {MappingMath.Distance(srcPos, ourPos) * MappingMath.MmPerFoot:N0} mm from its insertion point" : "");
                if (fresh) ShowTest(app, inst, fi, source, false, false, false);
                Status(fresh ? "Test placed. Adjust the offset / rotation (it re-places in place), or change its type in Revit (the mapping follows), ◀ / ▶ to check other elements. Changes save automatically."
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
                if (symbol == null || symbol.UniqueId == _editing.TargetSymbolUid) return;
                bool known = _targets.Any(t => t.Uid == symbol.UniqueId);
                var targets = known ? null : FamilyPlacer.TargetTypes(doc);   // a type made just now (Duplicate)
                string uid = symbol.UniqueId, name = $"{symbol.FamilyName} : {symbol.Name}";
                string rule = _editing.Id;
                Dispatcher.BeginInvoke(new Action(() => Safe(() => TakeTestType(rule, uid, name, targets, !known))));
            }
            catch (Exception ex) { Log.Info($"[Mapping] watching the test family: {ex.Message}"); }
        }

        private void TakeTestType(string ruleId, string uid, string name, List<FamilyPlacer.TargetType> targets, bool isNew)
        {
            if (_editing == null || _editing.Id != ruleId || _current == null) return;
            if (targets != null) _targets = targets;
            _loadingEditor = true;
            try
            {
                var s = _current.Source;
                _target.Items.Clear();
                foreach (var t in _targets.OrderBy(t => t.Category == s.Category ? 0 : 1).ThenBy(t => t.Category).ThenBy(t => t.Preference).ThenBy(t => t.Family).ThenBy(t => t.Type))
                    _target.Items.Add(new TargetItem(t, t.Category == s.Category));
                _target.SelectedItem = _target.Items.Cast<TargetItem>().FirstOrDefault(i => i.T.Uid == uid);
            }
            finally { _loadingEditor = false; }
            if (_target.SelectedItem == null) { Status($"The test family is now {name}, which can't be placed floating: not taken into the mapping.", true); return; }
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
            string key = _editing.SourceKey;
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
                if (mapping.Rules.RemoveAll(r => r.SourceKey == key) == 0) { Status("This type isn't mapped."); return; }
                FamilyMapStore.Save(_doc, _modelKey, mapping, _me);
                _mapping = mapping;
                BuildRows();
                Fill();
                Status("Mapping cleared.");
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
        };

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
