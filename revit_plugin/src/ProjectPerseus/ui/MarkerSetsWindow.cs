using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using ProjectPerseus.logging;
using ProjectPerseus.markers;
using ProjectPerseus.models;
using ProjectPerseus.queue;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modeless "Parameter Markers" panel: the library of marker sets on the left, the selected set's query
    /// (categories + filter), parameter and legend on the right. Edits apply live (debounced): the set is saved to
    /// MarkerSetStore, which makes MarkerEngine re-evaluate the view. Model reads (parameter names, distinct values)
    /// run through queue/OwnershipUiEvent, the plugin's general "run this in an API context" event.
    /// </summary>
    internal class MarkerSetsWindow : Window
    {
        internal static MarkerSetsWindow Instance { get; private set; }

        private static readonly string[] Palette =
        {
            "#E6007E", "#00A0E0", "#F28C00", "#2EAD4B", "#7B4FD6", "#D4B000",
            "#D23A3A", "#00A39A", "#2F6FDE", "#F06BA8", "#8C5A2B", "#6B7280",
        };
        private static readonly string[] MatchNames = { "equals", "contains", "matches regex", "has any value", "is empty" };
        private static readonly MarkerSet.MatchKind[] MatchKinds =
        {
            MarkerSet.MatchKind.Equals, MarkerSet.MatchKind.Contains, MarkerSet.MatchKind.Regex,
            MarkerSet.MatchKind.AnyValue, MarkerSet.MatchKind.Empty,
        };

        private static readonly MarkerSet.DisplayKind[] DisplayKinds =
        {
            MarkerSet.DisplayKind.Ring, MarkerSet.DisplayKind.Square, MarkerSet.DisplayKind.Outline, MarkerSet.DisplayKind.IconOnly,
        };

        private static string DisplayName(MarkerSet.DisplayKind kind) =>
            kind == MarkerSet.DisplayKind.IconOnly ? "Icon only" : kind.ToString();

        private static readonly Brush Ink = Frozen(Color.FromRgb(0x2F, 0x3D, 0x57));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xC0, 0x26, 0x26));
        private static readonly Brush Line = Frozen(Color.FromRgb(0xE2, 0xE6, 0xEE));

        private readonly List<KeyValuePair<string, string>> _categories; // (BuiltInCategory name, display name)

        private readonly CheckBox _showMarkers, _showLegend;
        private readonly ListBox _setList;
        private readonly StackPanel _editor;
        private readonly TextBlock _emptyEditor;
        private readonly TextBox _name, _categorySearch, _filter;
        private readonly CheckBox _includeLinks;
        private readonly ListBox _categoryList;
        private readonly StackPanel _paramRows;                 // the set's default parameters, one editable box each
        private readonly List<ComboBox> _paramBoxes = new List<ComboBox>();
        private readonly StackPanel _entryRows;
        private readonly TextBlock _viewStatus, _modelStatus, _selectionStatus;
        private readonly Button _clearSelection;

        // Legend row selection (Ctrl/Shift+click): colour, icon, display, parameter, match and on/off changes made on a
        // selected row apply to every selected row.
        private readonly HashSet<MarkerSet.Entry> _selected = new HashSet<MarkerSet.Entry>();
        private MarkerSet.Entry _anchor;
        private readonly List<KeyValuePair<MarkerSet.Entry, Border>> _rowBorders = new List<KeyValuePair<MarkerSet.Entry, Border>>();
        private readonly List<Action> _entryParamRefreshers = new List<Action>();
        private static readonly Brush SelectedFill = Frozen(Color.FromRgb(0xE8, 0xF1, 0xFC));
        private static readonly Brush SelectedEdge = Frozen(Color.FromRgb(0x9C, 0xC3, 0xF0));

        private MarkerSet _set;
        private bool _loading;
        private MarkerEngine.Inspection _inspection;
        private readonly List<TextBlock> _entryCounts = new List<TextBlock>();

        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _inspectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };

        /// <summary>Opens the panel, or brings it to the front. Needs an API context (reads the model's categories).</summary>
        internal static void Open(Autodesk.Revit.UI.UIApplication app)
        {
            if (Instance != null)
            {
                if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
                Instance.Activate();
                return;
            }
            var cats = MarkerEngine.ModelCategories(app.ActiveUIDocument?.Document);
            var w = new MarkerSetsWindow(cats);
            new WindowInteropHelper(w).Owner = app.MainWindowHandle;
            w.Show();
        }

        private MarkerSetsWindow(List<KeyValuePair<string, string>> categories)
        {
            Instance = this;
            _categories = categories;

            Title = "Perseus — Parameter Markers";
            Width = 1140;
            Height = 640;
            MinWidth = 720;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Ink;
            Background = Brushes.White;

            var root = new Grid { Margin = new Thickness(12) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Left: library ──
            var left = new DockPanel();
            var toggles = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            _showMarkers = new CheckBox { Content = "Show markers", IsChecked = MarkerSetStore.ShowMarkers, FontWeight = FontWeights.SemiBold };
            _showLegend = new CheckBox { Content = "Show legend on the view", IsChecked = MarkerSetStore.ShowLegend, Margin = new Thickness(0, 4, 0, 0) };
            _showMarkers.Click += (s, e) => MarkerSetStore.ShowMarkers = _showMarkers.IsChecked == true;
            _showLegend.Click += (s, e) => MarkerSetStore.ShowLegend = _showLegend.IsChecked == true;
            toggles.Children.Add(_showMarkers);
            toggles.Children.Add(_showLegend);
            DockPanel.SetDock(toggles, Dock.Top);
            left.Children.Add(toggles);

            var libButtons = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
            libButtons.Children.Add(MakeButton("New", (s, e) => NewSet()));
            libButtons.Children.Add(MakeButton("Duplicate", (s, e) => DuplicateSet()));
            libButtons.Children.Add(MakeButton("Delete", (s, e) => DeleteSet()));
            var fileButtons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
            fileButtons.Children.Add(MakeButton("Import…", (s, e) => ImportSets()));
            fileButtons.Children.Add(MakeButton("Export…", (s, e) => ExportSets()));
            var bottomLeft = new StackPanel();
            bottomLeft.Children.Add(libButtons);
            bottomLeft.Children.Add(fileButtons);
            DockPanel.SetDock(bottomLeft, Dock.Bottom);
            left.Children.Add(bottomLeft);

            _setList = new ListBox { BorderBrush = Line };
            _setList.SelectionChanged += (s, e) => SelectSet((_setList.SelectedItem as ListBoxItem)?.Tag as MarkerSet);
            left.Children.Add(_setList);
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // ── Right: editor ──
            _editor = new StackPanel();
            _emptyEditor = new TextBlock
            {
                Text = "Select a marker set on the left, or click New.",
                Foreground = Muted,
                Margin = new Thickness(0, 20, 0, 0),
            };

            _name = new TextBox { FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(4, 3, 4, 3) };
            _name.TextChanged += (s, e) => { if (Editing()) { _set.Name = _name.Text; RefreshListItem(_set); ScheduleApply(); } };
            _editor.Children.Add(_name);

            // Query: categories + filter
            _editor.Children.Add(Section("Which elements"));
            var queryGrid = new Grid();
            queryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            queryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            queryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

            var catPanel = new DockPanel();
            _categorySearch = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Search categories" };
            _categorySearch.TextChanged += (s, e) => FillCategoryList();
            var catLabel = Hint("Categories");
            DockPanel.SetDock(catLabel, Dock.Top);
            DockPanel.SetDock(_categorySearch, Dock.Top);
            catPanel.Children.Add(catLabel);
            catPanel.Children.Add(_categorySearch);
            _categoryList = new ListBox { Height = 120, BorderBrush = Line };
            _includeLinks = new CheckBox
            {
                Content = "Include elements in linked models",
                Margin = new Thickness(0, 6, 0, 0),
                ToolTip = "Also ring matching elements inside Revit links shown in the view. Use row[\"Model\"] in the filter to pick a link.",
            };
            _includeLinks.Click += (s, e) => { if (Editing()) { _set.IncludeLinks = _includeLinks.IsChecked == true; ScheduleApply(); ScheduleInspect(); } };
            DockPanel.SetDock(_includeLinks, Dock.Bottom);
            catPanel.Children.Add(_includeLinks);
            catPanel.Children.Add(_categoryList);
            Grid.SetColumn(catPanel, 0);
            queryGrid.Children.Add(catPanel);

            var filterPanel = new StackPanel();
            filterPanel.Children.Add(Hint("Filter (optional) — only elements where this is true"));
            _filter = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 46,
                Padding = new Thickness(3),
            };
            _filter.TextChanged += (s, e) => { if (Editing()) { _set.Filter = _filter.Text; ScheduleApply(); ScheduleInspect(); } };
            filterPanel.Children.Add(_filter);
            filterPanel.Children.Add(new TextBlock
            {
                Foreground = Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Text = "Python-style, using any parameter or Family / Type / Category / Level / Model, e.g.\n" +
                       "  \"Double\" in row[\"Family\"]\n" +
                       "  row[\"Level\"] == \"L02\" and row[\"Fire Rating\"] != \"\"",
            });
            Grid.SetColumn(filterPanel, 2);
            queryGrid.Children.Add(filterPanel);
            _editor.Children.Add(queryGrid);

            // Parameters
            _editor.Children.Add(Section("Parameters"));
            _editor.Children.Add(Hint("Legend entries pick one of these. Auto-fill adds an entry for each value of each parameter. " +
                                      "Entries that don't pick one read the first."));
            _paramRows = new StackPanel();
            _editor.Children.Add(_paramRows);
            _modelStatus = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            _editor.Children.Add(_modelStatus);

            // Legend
            var legendHeader = new DockPanel { Margin = new Thickness(0, 14, 0, 4) };
            var legendButtons = new StackPanel { Orientation = Orientation.Horizontal };
            legendButtons.Children.Add(MakeButton("+ Add entry", (s, e) => AddEntry(null)));
            var auto = MakeButton("Auto-fill from values", (s, e) => AutoFill());
            auto.Margin = new Thickness(6, 0, 0, 0);
            auto.ToolTip = "Add an entry (with its own colour) for each distinct value of each parameter in the model.";
            legendButtons.Children.Add(auto);
            var selectAll = MakeButton("Select all", (s, e) => SelectAllEntries());
            selectAll.Margin = new Thickness(6, 0, 0, 0);
            selectAll.ToolTip = "Select every entry, then change a colour, icon, display, parameter or match once for all of them.";
            legendButtons.Children.Add(selectAll);
            _clearSelection = MakeButton("Clear selection", (s, e) => { _selected.Clear(); _anchor = null; PaintSelection(); });
            _clearSelection.Margin = new Thickness(4, 0, 0, 0);
            legendButtons.Children.Add(_clearSelection);
            DockPanel.SetDock(legendButtons, Dock.Right);
            legendHeader.Children.Add(legendButtons);
            legendHeader.Children.Add(new TextBlock { Text = "Legend", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            _editor.Children.Add(legendHeader);
            _editor.Children.Add(Hint("Per parameter, the first entry an element's value matches marks it; entries on different " +
                                      "parameters mark independently (e.g. mag lock and drop bolt on one door). " +
                                      "Display: ring, square, the element's outline traced thick, or icon only. Count = elements in the current view."));
            _selectionStatus = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            _editor.Children.Add(_selectionStatus);
            _entryRows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            _editor.Children.Add(_entryRows);

            _viewStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            _editor.Children.Add(_viewStatus);

            var editorHost = new Grid();
            editorHost.Children.Add(new ScrollViewer
            {
                Content = _editor,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0),
            });
            editorHost.Children.Add(_emptyEditor);
            Grid.SetColumn(editorHost, 2);
            root.Children.Add(editorHost);

            // ── Footer ──
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var close = MakeButton("Close", (s, e) => Close());
            close.MinWidth = 80;
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(new TextBlock
            {
                Text = "Changes apply as you edit. Sets are saved on this computer; use Export/Import to share them.",
                Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetRow(footer, 1);
            Grid.SetColumnSpan(footer, 3);
            root.Children.Add(footer);

            Content = root;

            _applyTimer.Tick += (s, e) => { _applyTimer.Stop(); MarkerSetStore.NotifyChanged(); };
            _inspectTimer.Tick += (s, e) => { _inspectTimer.Stop(); RequestInspection(); };
            MarkerEngine.Evaluated += OnEvaluated;
            Closed += (s, e) =>
            {
                if (_applyTimer.IsEnabled) { _applyTimer.Stop(); MarkerSetStore.NotifyChanged(); }
                _inspectTimer.Stop();
                MarkerEngine.Evaluated -= OnEvaluated;
                Instance = null;
            };

            FillSetList();
            if (_setList.Items.Count > 0) _setList.SelectedIndex = 0;
            else SelectSet(null);
        }

        // ── Library ──

        private void FillSetList()
        {
            var selected = _set;
            _setList.Items.Clear();
            foreach (var set in MarkerSetStore.Sets)
            {
                var item = new ListBoxItem { Tag = set, Padding = new Thickness(4, 3, 4, 3) };
                item.Content = BuildListItemContent(set);
                _setList.Items.Add(item);
                if (set == selected) item.IsSelected = true;
            }
        }

        private UIElement BuildListItemContent(MarkerSet set)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var check = new CheckBox { IsChecked = set.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show this set" };
            check.Click += (s, e) => { set.Enabled = check.IsChecked == true; MarkerSetStore.NotifyChanged(); };
            row.Children.Add(check);
            foreach (var entry in set.Entries.Where(x => x.Enabled).Take(4))
            {
                row.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 9, Height = 9, Margin = new Thickness(4, 0, 0, 0),
                    Stroke = Frozen(MarkerEngine.ParseColor(entry.Color)), StrokeThickness = 2,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            row.Children.Add(new TextBlock { Text = set.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        private void RefreshListItem(MarkerSet set)
        {
            foreach (ListBoxItem item in _setList.Items)
                if (item.Tag == set) item.Content = BuildListItemContent(set);
        }

        private void NewSet()
        {
            var set = new MarkerSet { Name = "New marker set" };
            if (_categories.Any(c => c.Key == "OST_Doors")) set.Categories.Add("OST_Doors");
            set.Entries.Add(new MarkerSet.Entry { Label = "", Match = MarkerSet.MatchKind.AnyValue, Color = Palette[0] });
            MarkerSetStore.Add(set);
            _set = set;
            FillSetList();
            _name.Focus();
            _name.SelectAll();
        }

        private void DuplicateSet()
        {
            if (_set == null) return;
            var copy = _set.Clone();
            copy.Name = _set.Name + " (copy)";
            MarkerSetStore.Add(copy);
            _set = copy;
            FillSetList();
        }

        private void DeleteSet()
        {
            if (_set == null) return;
            if (MessageBox.Show(this, $"Delete the marker set \"{_set.Name}\"?", "Perseus — Parameter Markers",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            int index = _setList.SelectedIndex;
            MarkerSetStore.Remove(_set);
            _set = null;
            FillSetList();
            if (_setList.Items.Count > 0) _setList.SelectedIndex = Math.Min(index, _setList.Items.Count - 1);
            else SelectSet(null);
        }

        private void ImportSets()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Perseus marker sets (*.json)|*.json|All files|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                int n = MarkerSetStore.Import(dlg.FileName);
                FillSetList();
                MessageBox.Show(this, $"Imported {n} marker set(s).", "Perseus — Parameter Markers");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't import that file:\n{ex.Message}", "Perseus — Parameter Markers",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportSets()
        {
            if (MarkerSetStore.Sets.Count == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Perseus marker sets (*.json)|*.json",
                FileName = _set != null ? SafeFileName(_set.Name) + ".json" : "marker_sets.json",
            };
            if (dlg.ShowDialog(this) != true) return;
            var which = MessageBox.Show(this,
                "Export only the selected set?\n\nYes = selected set, No = all sets.",
                "Perseus — Parameter Markers", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (which == MessageBoxResult.Cancel) return;
            try
            {
                var sets = which == MessageBoxResult.Yes && _set != null ? new List<MarkerSet> { _set } : MarkerSetStore.Sets;
                MarkerSetStore.Export(dlg.FileName, sets);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't export:\n{ex.Message}", "Perseus — Parameter Markers",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ── Editor ──

        private bool Editing() => !_loading && _set != null;

        private void SelectSet(MarkerSet set)
        {
            _set = set;
            _inspection = null;
            _selected.Clear();
            _anchor = null;
            _emptyEditor.Visibility = set == null ? Visibility.Visible : Visibility.Collapsed;
            _editor.Visibility = set == null ? Visibility.Collapsed : Visibility.Visible;
            if (set == null) return;

            _loading = true;
            try
            {
                _name.Text = set.Name;
                _filter.Text = set.Filter;
                _includeLinks.IsChecked = set.IncludeLinks;
                BuildParameterRows();
                _categorySearch.Text = "";
                FillCategoryList();
                BuildEntryRows();
                _modelStatus.Text = "Reading the model…";
            }
            finally { _loading = false; }
            UpdateViewStatus();
            RequestInspection();
        }

        private void FillCategoryList()
        {
            if (_set == null) return;
            string q = _categorySearch.Text.Trim();
            _categoryList.Items.Clear();
            var chosen = new HashSet<string>(_set.Categories);
            var ordered = _categories
                .Where(c => q.Length == 0 || c.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || chosen.Contains(c.Key))
                .OrderByDescending(c => chosen.Contains(c.Key))
                .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var c in ordered)
            {
                var key = c.Key;
                var check = new CheckBox { Content = c.Value, IsChecked = chosen.Contains(key) };
                check.Click += (s, e) =>
                {
                    if (_set == null) return;
                    if (check.IsChecked == true) { if (!_set.Categories.Contains(key)) _set.Categories.Add(key); }
                    else _set.Categories.Remove(key);
                    ScheduleApply();
                    ScheduleInspect();
                };
                _categoryList.Items.Add(check);
            }
            // Categories saved in the set but not in this model (e.g. imported): keep them visible.
            foreach (var missing in _set.Categories.Where(k => _categories.All(c => c.Key != k)).ToList())
            {
                var key = missing;
                var check = new CheckBox { Content = key + " (not in this model)", IsChecked = true };
                check.Click += (s, e) => { if (check.IsChecked != true) { _set.Categories.Remove(key); ScheduleApply(); } };
                _categoryList.Items.Insert(0, check);
            }
        }

        // ── Parameters ──

        private void BuildParameterRows()
        {
            _paramRows.Children.Clear();
            _paramBoxes.Clear();
            if (_set == null) return;
            if (_set.Parameters.Count == 0) _set.Parameters.Add("");
            for (int i = 0; i < _set.Parameters.Count; i++)
            {
                int index = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Width = 320, Text = _set.Parameters[i] ?? "" };
                if (_inspection != null) box.ItemsSource = _inspection.ParameterNames;
                // Entries that picked this parameter follow it when it's retyped (also through a momentarily blank box).
                string start = (_set.Parameters[i] ?? "").Trim();
                var linked = start.Length == 0
                    ? new List<MarkerSet.Entry>()
                    : _set.Entries.Where(x => string.Equals((x.Parameter ?? "").Trim(), start, StringComparison.OrdinalIgnoreCase)).ToList();
                box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => OnParameterEdited(index, box.Text, linked)));
                row.Children.Add(box);
                if (_set.Parameters.Count > 1)
                    row.Children.Add(SmallButton("✕", "Remove this parameter (entries that picked it keep reading it)", () => RemoveParameter(index)));
                if (index == 0 && _set.Parameters.Count > 1)
                    row.Children.Add(new TextBlock
                    {
                        Text = "also used by entries that don't pick one", Foreground = Muted, FontSize = 11,
                        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    });
                _paramBoxes.Add(box);
                _paramRows.Children.Add(row);
            }
            var add = MakeButton("+ Add parameter", (s, e) =>
            {
                if (_set == null) return;
                _set.Parameters.Add("");
                BuildParameterRows();
            });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 2, 0, 0);
            _paramRows.Children.Add(add);
        }

        private void OnParameterEdited(int index, string text, List<MarkerSet.Entry> linked)
        {
            if (!Editing() || index >= _set.Parameters.Count || (_set.Parameters[index] ?? "") == text) return;
            string old = (_set.Parameters[index] ?? "").Trim();
            _set.Parameters[index] = text;
            foreach (var en in linked)
                if (string.Equals((en.Parameter ?? "").Trim(), old, StringComparison.OrdinalIgnoreCase)) en.Parameter = text.Trim();
            foreach (var refresh in _entryParamRefreshers) refresh();
            ScheduleApply();
            ScheduleInspect();
        }

        private void RemoveParameter(int index)
        {
            if (_set == null || index >= _set.Parameters.Count) return;
            string removed = (_set.Parameters[index] ?? "").Trim();
            // Entries reading the first parameter implicitly keep reading it.
            if (index == 0 && removed.Length > 0)
                foreach (var en in _set.Entries.Where(x => string.IsNullOrWhiteSpace(x.Parameter))) en.Parameter = removed;
            _set.Parameters.RemoveAt(index);
            BuildParameterRows();
            foreach (var refresh in _entryParamRefreshers) refresh();
            ScheduleApply();
            ScheduleInspect();
        }

        // ── Legend entries ──

        private void BuildEntryRows()
        {
            _entryRows.Children.Clear();
            _entryCounts.Clear();
            _rowBorders.Clear();
            _entryParamRefreshers.Clear();
            if (_set == null) { PaintSelection(); return; }
            _selected.RemoveWhere(x => !_set.Entries.Contains(x));
            if (_anchor != null && !_set.Entries.Contains(_anchor)) _anchor = null;

            var header = EntryGrid();
            AddCell(header, 1, Hint("Colour"));
            AddCell(header, 2, Hint("Icon"));
            AddCell(header, 3, Hint("Display"));
            AddCell(header, 4, Hint("Parameter"));
            AddCell(header, 5, Hint("When value"));
            AddCell(header, 6, Hint("Value"));
            AddCell(header, 7, Hint("Label"));
            AddCell(header, 8, Hint("In view"));
            _entryRows.Children.Add(new Border { Child = header, BorderThickness = new Thickness(1) }); // aligns with the rows

            for (int i = 0; i < _set.Entries.Count; i++)
                _entryRows.Children.Add(BuildEntryRow(_set.Entries[i], i));
            UpdateEntryCounts();
            PaintSelection();
        }

        private static Grid EntryGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            // enable | colour | icon | display | parameter | match | value | label | count | tools
            foreach (var w in new[] { 24.0, 40, 52, 112, -1, 112, -1.2, -1, 52, 84 })
                g.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w < 0 ? new GridLength(-w, GridUnitType.Star) : new GridLength(w),
                });
            return g;
        }

        private static void AddCell(Grid g, int col, UIElement e)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        /// <summary>The entries an edit on <paramref name="entry"/> applies to: all selected ones if it is one of several.</summary>
        private List<MarkerSet.Entry> Targets(MarkerSet.Entry entry) =>
            _selected.Count > 1 && _selected.Contains(entry)
                ? _set.Entries.Where(_selected.Contains).ToList()
                : new List<MarkerSet.Entry> { entry };

        /// <summary>After an edit: redraw the rows when other rows (or this row's swatches) changed, then apply.</summary>
        private void Applied(int count, bool repaint = false)
        {
            if (count > 1 || repaint) Dispatcher.BeginInvoke(new Action(BuildEntryRows), DispatcherPriority.Background);
            RefreshListItem(_set);
            ScheduleApply();
        }

        private UIElement BuildEntryRow(MarkerSet.Entry entry, int index)
        {
            var g = EntryGrid();

            var enabled = new CheckBox { IsChecked = entry.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Use this entry" };
            enabled.Click += (s, e) =>
            {
                bool on = enabled.IsChecked == true;
                var t = Targets(entry);
                foreach (var x in t) x.Enabled = on;
                Applied(t.Count);
            };
            AddCell(g, 0, enabled);

            // Colour swatch → colour dialog
            var colour = new Button { Width = 32, Height = 22, Padding = new Thickness(0), ToolTip = "Colour", Cursor = Cursors.Hand };
            var c0 = MarkerEngine.ParseColor(entry.Color);
            colour.Content = new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Stroke = Frozen(c0), StrokeThickness = 3 };
            colour.Click += (s, e) =>
            {
                var t = Targets(entry);
                var c = MarkerEngine.ParseColor(entry.Color);
                using (var dlg = new System.Windows.Forms.ColorDialog
                {
                    FullOpen = true,
                    Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B),
                    CustomColors = Palette.Select(h => { var p = MarkerEngine.ParseColor(h); return p.B << 16 | p.G << 8 | p.R; }).ToArray(),
                })
                {
                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    string hex = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                    foreach (var x in t) x.Color = hex;
                }
                Applied(t.Count, repaint: true);
            };
            AddCell(g, 1, colour);

            // Icon → menu of icons
            var icon = new Button { Width = 44, Height = 22, Padding = new Thickness(0), ToolTip = "Icon", Cursor = Cursors.Hand };
            icon.Content = string.IsNullOrEmpty(entry.Icon)
                ? (object)new TextBlock { Text = "none", Foreground = Muted, FontSize = 10 }
                : MarkerIcons.Badge(entry.Icon, MarkerEngine.ParseColor(entry.Color), 18);
            icon.Click += (s, e) =>
            {
                var t = Targets(entry);
                var menu = new ContextMenu { PlacementTarget = icon, Placement = PlacementMode.Bottom };
                if (t.Count > 1)
                    menu.Items.Add(new MenuItem { Header = $"Icon for the {t.Count} selected entries", IsEnabled = false, FontWeight = FontWeights.SemiBold });
                Action<string> choose = key =>
                {
                    foreach (var x in t) x.Icon = key;
                    Applied(t.Count, repaint: true);
                };
                var none = new MenuItem { Header = "No icon" };
                none.Click += (s2, e2) => choose("");
                menu.Items.Add(none);
                foreach (var ic in MarkerIcons.All)
                {
                    var key = ic.Key;
                    var item = new MenuItem { Header = ic.Name, Icon = MarkerIcons.Badge(key, MarkerEngine.ParseColor(entry.Color), 18) };
                    item.Click += (s2, e2) => choose(key);
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            };
            AddCell(g, 2, icon);

            // Display: ring / square / outline / icon only
            var display = new ComboBox
            {
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Ring or Square: drawn around the element, with the icon on it.\n" +
                          "Outline: the element's own lines in this view, traced thick in the colour.\n" +
                          "Icon only: just the icon (a dot if no icon) on the element's centre.",
            };
            foreach (var kind in DisplayKinds)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal };
                item.Children.Add(ViolationOverlay.LegendSwatch(kind, "", MarkerEngine.ParseColor(entry.Color)));
                item.Children.Add(new TextBlock { Text = DisplayName(kind), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                display.Items.Add(new ComboBoxItem { Content = item, Tag = kind });
            }
            display.SelectedIndex = Math.Max(0, Array.IndexOf(DisplayKinds, entry.Display));
            display.SelectionChanged += (s, e) =>
            {
                if (display.SelectedIndex < 0) return;
                var kind = DisplayKinds[display.SelectedIndex];
                var t = Targets(entry);
                foreach (var x in t) x.Display = kind;
                Applied(t.Count);
            };
            AddCell(g, 3, display);

            // Parameter: one of the set's parameters (an entry imported with another one keeps it, listed last)
            var param = new ComboBox
            {
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "Which of the set's parameters this entry reads (add parameters above).\n" +
                          "Entries reading different parameters mark independently, so one element can get several marks.",
            };
            bool fillingParam = false;
            Action fillParam = () =>
            {
                fillingParam = true;
                try
                {
                    var choices = _set.DefaultParameters;
                    string current = _set.ParameterFor(entry);
                    if (current.Length > 0 && !choices.Contains(current, StringComparer.OrdinalIgnoreCase)) choices.Add(current);
                    param.ItemsSource = choices;
                    param.SelectedIndex = choices.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                    param.IsEnabled = choices.Count > 0;
                }
                finally { fillingParam = false; }
            };
            fillParam();
            _entryParamRefreshers.Add(fillParam);
            param.SelectionChanged += (s, e) =>
            {
                if (fillingParam || !(param.SelectedItem is string chosen)) return;
                var t = Targets(entry);
                foreach (var x in t) x.Parameter = chosen;
                Applied(t.Count);
                ScheduleInspect(); // values for this parameter's suggestions
            };
            AddCell(g, 4, param);

            // Match kind
            var match = new ComboBox { ItemsSource = MatchNames, SelectedIndex = Math.Max(0, Array.IndexOf(MatchKinds, entry.Match)), Margin = new Thickness(4, 0, 4, 0) };
            AddCell(g, 5, match);

            // Value (editable, suggestions = distinct values of the entry's parameter in the model)
            var value = new ComboBox { IsEditable = true, Text = entry.Value, Margin = new Thickness(0, 0, 4, 0) };
            if (_inspection != null) value.ItemsSource = SuggestedValues(entry);
            value.DropDownOpened += (s, e) => { if (_inspection != null) value.ItemsSource = SuggestedValues(entry); };
            value.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) =>
            {
                if (entry.Value == value.Text) return;
                entry.Value = value.Text;
                ScheduleApply();
            }));
            Action syncValueEnabled = () =>
            {
                bool needsValue = entry.Match != MarkerSet.MatchKind.AnyValue && entry.Match != MarkerSet.MatchKind.Empty;
                value.IsEnabled = needsValue;
                value.Opacity = needsValue ? 1 : 0.4;
            };
            syncValueEnabled();
            match.SelectionChanged += (s, e) =>
            {
                if (match.SelectedIndex < 0) return;
                var kind = MatchKinds[match.SelectedIndex];
                var t = Targets(entry);
                foreach (var x in t) x.Match = kind;
                syncValueEnabled();
                Applied(t.Count);
            };
            AddCell(g, 6, value);

            var label = new TextBox { Text = entry.Label, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Shown in the legend (defaults to the value)" };
            label.TextChanged += (s, e) => { entry.Label = label.Text; ScheduleApply(); };
            AddCell(g, 7, label);

            var count = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
            _entryCounts.Add(count);
            AddCell(g, 8, count);

            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(SmallButton("▲", "Move up (among entries on the same parameter, earlier entries win)", () => MoveEntry(index, -1)));
            tools.Children.Add(SmallButton("▼", "Move down", () => MoveEntry(index, +1)));
            tools.Children.Add(SmallButton("✕", "Remove (all selected entries, if this is one of them)", () =>
            {
                foreach (var x in Targets(entry)) { _set.Entries.Remove(x); _selected.Remove(x); }
                BuildEntryRows();
                RefreshListItem(_set);
                ScheduleApply();
            }));
            AddCell(g, 9, tools);

            var border = new Border
            {
                Child = g, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            };
            border.PreviewMouseLeftButtonDown += (s, e) => OnRowMouseDown(entry, border, e);
            _rowBorders.Add(new KeyValuePair<MarkerSet.Entry, Border>(entry, border));
            return border;
        }

        // ── Entry selection ──

        // Ctrl+click toggles a row, Shift+click selects a range (Ctrl+Shift adds it); neither reaches the control under
        // the mouse. A plain click on an unselected row, or on a selected row's background, selects just that row; a
        // plain click on a control in a selected row keeps the selection so the change applies to all of it.
        private void OnRowMouseDown(MarkerSet.Entry entry, Border row, MouseButtonEventArgs e)
        {
            if (_set == null) return;
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
            if (shift && _anchor != null && _set.Entries.Contains(_anchor))
            {
                int a = _set.Entries.IndexOf(_anchor), b = _set.Entries.IndexOf(entry);
                if (!ctrl) _selected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _selected.Add(_set.Entries[i]);
                e.Handled = true;
            }
            else if (ctrl || shift)
            {
                if (!_selected.Remove(entry)) _selected.Add(entry);
                _anchor = entry;
                e.Handled = true;
            }
            else if (!_selected.Contains(entry) || !IsInControl(e.OriginalSource as DependencyObject, row))
            {
                _selected.Clear();
                _selected.Add(entry);
                _anchor = entry;
            }
            PaintSelection();
        }

        private static bool IsInControl(DependencyObject d, Border row)
        {
            while (d != null && d != row)
            {
                if (d is ButtonBase || d is ComboBox || d is TextBoxBase) return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return d == null; // left the row's tree (e.g. a drop-down's popup): treat as a control
        }

        private void SelectAllEntries()
        {
            if (_set == null) return;
            foreach (var x in _set.Entries) _selected.Add(x);
            _anchor = _set.Entries.FirstOrDefault();
            PaintSelection();
        }

        private void PaintSelection()
        {
            foreach (var kv in _rowBorders)
            {
                bool on = _selected.Contains(kv.Key);
                kv.Value.Background = on ? SelectedFill : Brushes.Transparent;
                kv.Value.BorderBrush = on ? SelectedEdge : Brushes.Transparent;
            }
            int n = _selected.Count;
            _clearSelection.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (n > 1)
            {
                _selectionStatus.Text = $"{n} entries selected: changing the colour, icon, display, parameter, match or on/off of any of them changes all {n}.";
                _selectionStatus.Foreground = Ink;
            }
            else
            {
                _selectionStatus.Text = "Ctrl+click or Shift+click entries (or Select all) to change their colour, icon, display… in one go.";
                _selectionStatus.Foreground = Muted;
            }
        }

        private void MoveEntry(int index, int delta)
        {
            int to = index + delta;
            if (_set == null || to < 0 || to >= _set.Entries.Count) return;
            var e = _set.Entries[index];
            _set.Entries.RemoveAt(index);
            _set.Entries.Insert(to, e);
            BuildEntryRows();
            ScheduleApply();
        }

        private string NextColour()
        {
            var used = new HashSet<string>(_set.Entries.Select(x => x.Color), StringComparer.OrdinalIgnoreCase);
            return Palette.FirstOrDefault(p => !used.Contains(p)) ?? Palette[_set.Entries.Count % Palette.Length];
        }

        private void AddEntry(string value)
        {
            if (_set == null) return;
            _set.Entries.Add(new MarkerSet.Entry
            {
                Match = MarkerSet.MatchKind.Equals,
                Value = value ?? "",
                Label = value ?? "",
                Color = NextColour(),
            });
            BuildEntryRows();
            RefreshListItem(_set);
            ScheduleApply();
        }

        private const int MaxAutoFillPerParameter = 24;

        private void AutoFill()
        {
            if (_set == null) return;
            var parms = _set.DefaultParameters;
            if (parms.Count == 0)
            {
                MessageBox.Show(this, "Add a parameter first; the values are read from the model.", "Perseus — Parameter Markers");
                return;
            }
            if (_set.Categories.Count == 0)
            {
                MessageBox.Show(this, "Pick one or more categories first.", "Perseus — Parameter Markers");
                return;
            }
            if (_inspection == null || _inspection.Error != null || parms.Any(p => !_inspection.ValuesByParameter.ContainsKey(p)))
            {
                MessageBox.Show(this, _inspection?.Error ?? "Still reading the values from the model; try again in a moment.", "Perseus — Parameter Markers");
                RequestInspection();
                return;
            }

            int added = 0;
            var notes = new List<string>();
            foreach (var p in parms)
            {
                var existing = new HashSet<string>(
                    _set.Entries.Where(x => x.Match == MarkerSet.MatchKind.Equals &&
                                            string.Equals(_set.ParameterFor(x), p, StringComparison.OrdinalIgnoreCase))
                                .Select(x => (x.Value ?? "").Trim()),
                    StringComparer.OrdinalIgnoreCase);
                int here = 0, skipped = 0;
                foreach (var kv in _inspection.ValuesOf(p))
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || existing.Contains(kv.Key)) continue;
                    if (here >= MaxAutoFillPerParameter) { skipped++; continue; }
                    _set.Entries.Add(new MarkerSet.Entry
                    {
                        Match = MarkerSet.MatchKind.Equals, Value = kv.Key, Label = kv.Key, Color = NextColour(),
                        Parameter = parms.Count > 1 ? p : "",
                    });
                    here++;
                }
                added += here;
                if (skipped > 0) notes.Add($"\"{p}\": {skipped} more value(s) not added (at most {MaxAutoFillPerParameter} per parameter at a time).");
            }
            if (added == 0)
                MessageBox.Show(this, "No new values to add: every value already has an entry (or the parameters have no values).",
                                "Perseus — Parameter Markers");
            else if (notes.Count > 0)
                MessageBox.Show(this, $"Added {added} entries.\n\n" + string.Join("\n", notes), "Perseus — Parameter Markers");
            BuildEntryRows();
            RefreshListItem(_set);
            ScheduleApply();
        }

        private List<string> SuggestedValues(MarkerSet.Entry entry) =>
            _inspection == null || _set == null
                ? new List<string>()
                : _inspection.ValuesOf(_set.ParameterFor(entry)).Where(kv => kv.Key.Length > 0).Select(kv => kv.Key).Take(200).ToList();

        // ── Apply / inspect / status ──

        private void ScheduleApply()
        {
            if (_loading) return;
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        private void ScheduleInspect()
        {
            if (_loading) return;
            _inspectTimer.Stop();
            _inspectTimer.Start();
        }

        private void RequestInspection()
        {
            var set = _set;
            if (set == null) return;
            var snapshot = set.Clone(); // read on the API thread while the user keeps typing
            snapshot.Id = set.Id;
            OwnershipUiEvent.Run(app =>
            {
                MarkerEngine.Inspection info;
                try { info = MarkerEngine.Inspect(app.ActiveUIDocument?.Document, snapshot); }
                catch (Exception ex)
                {
                    Log.Warn($"[MarkerSetsWindow] inspection failed: {ex.Message}");
                    info = new MarkerEngine.Inspection { Error = ex.Message };
                }
                if (Instance == this && _set == set) ShowInspection(info);
            });
        }

        private void ShowInspection(MarkerEngine.Inspection info)
        {
            _inspection = info;
            _loading = true;
            try
            {
                foreach (var box in _paramBoxes)
                {
                    string typed = box.Text;
                    box.ItemsSource = info.ParameterNames;
                    box.Text = typed;
                }
            }
            finally { _loading = false; }

            if (info.Error != null) { _modelStatus.Text = info.Error; _modelStatus.Foreground = ErrorBrush; return; }
            _modelStatus.Foreground = Muted;
            if (_set.Categories.Count == 0) { _modelStatus.Text = "Pick one or more categories."; return; }

            string where = info.Models > 1 ? $"this model and {info.Models - 1} linked model(s)" : "the model";
            string matched = string.IsNullOrWhiteSpace(_set.Filter)
                ? $"{info.Matched:N0} element(s) in {where}"
                : $"{info.Matched:N0} of {info.Considered:N0} element(s) in {where} pass the filter";
            var parms = _set.DefaultParameters;
            if (parms.Count == 0) { _modelStatus.Text = matched + ". Choose a parameter."; return; }
            _modelStatus.Text = matched + ". " + string.Join("  ", parms.Select(p => ValueSummary(info, p, parms.Count > 1)));
        }

        private static string ValueSummary(MarkerEngine.Inspection info, string parameter, bool named)
        {
            var all = info.ValuesOf(parameter);
            var nonEmpty = all.Where(kv => kv.Key.Length > 0).ToList();
            int blank = all.Where(kv => kv.Key.Length == 0).Sum(kv => kv.Value);
            int take = named ? 4 : 6;
            string values = nonEmpty.Count == 0
                ? "no values"
                : string.Join(", ", nonEmpty.Take(take).Select(kv => $"{kv.Key} ({kv.Value})")) + (nonEmpty.Count > take ? $", +{nonEmpty.Count - take} more" : "");
            return (named ? parameter + ": " : "Values: ") + values + (blank > 0 ? $"; {blank} blank/missing." : ".");
        }

        private void OnEvaluated()
        {
            try { UpdateViewStatus(); UpdateEntryCounts(); }
            catch (Exception ex) { Log.Warn($"[MarkerSetsWindow] status update failed: {ex.Message}"); }
        }

        private void UpdateEntryCounts()
        {
            var r = _set == null ? null : MarkerEngine.GetResult(_set.Id);
            for (int i = 0; i < _entryCounts.Count; i++)
                _entryCounts[i].Text = r != null && r.Error == null && i < r.EntryCounts.Length ? r.EntryCounts[i].ToString("N0") : "–";
        }

        private void UpdateViewStatus()
        {
            if (_set == null) return;
            _viewStatus.Foreground = Muted;
            string note = MarkerEngine.ViewNote;
            if (!_set.Enabled) { _viewStatus.Text = "This set is switched off (tick it in the list to show it)."; return; }
            if (note != null && MarkerEngine.GetResult(_set.Id) == null) { _viewStatus.Text = note; return; }

            var r = MarkerEngine.GetResult(_set.Id);
            if (r == null) { _viewStatus.Text = ""; return; }
            if (r.Error != null) { _viewStatus.Text = r.Error; _viewStatus.Foreground = ErrorBrush; return; }

            string text = $"In this view: {r.Considered:N0} element(s)";
            if (!string.IsNullOrWhiteSpace(_set.Filter)) text += $", {r.Matched:N0} pass the filter";
            text += $", {r.Marked:N0} marked" + (r.MarkedInLinks > 0 ? $" ({r.MarkedInLinks:N0} in linked models)." : ".");
            if (r.ParamMissing > 0 && !string.IsNullOrWhiteSpace(_set.Parameter))
                text += $" {r.ParamMissing:N0} don't have a \"{_set.Parameter}\" parameter.";
            if (note != null) text += " " + note;
            _viewStatus.Text = text;
        }

        // ── Small UI helpers ──

        private static Button MakeButton(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += click;
            return b;
        }

        private static Button SmallButton(string text, string tip, Action click)
        {
            var b = new Button
            {
                Content = text, ToolTip = tip, Width = 24, Height = 22, Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0), FontSize = 10,
            };
            b.Click += (s, e) => click();
            return b;
        }

        private static TextBlock Section(string text) => new TextBlock
        {
            Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 14, 0, 4),
        };

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap,
        };

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
