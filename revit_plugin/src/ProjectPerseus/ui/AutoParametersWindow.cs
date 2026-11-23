using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// In-Revit editor for the Auto Parameters rules held by django-perseus (same data as the web page
    /// /source/&lt;guid&gt;/id-rules/): ID and value rules, category prefixes and the Perseus_ID categories.
    /// Edits one layer at a time: "This model" (the source's own rules; the project's rules are listed as inherited,
    /// read-only, with "Override in this model") or "Project defaults". Loads GET ?layer=..., saves PUT ?layer=... with
    /// the shared browser-pairing session (auth/PairingSession). Rules are kept as the server's JSON objects so fields
    /// this window doesn't show survive a round trip. HTTP on background tasks; no Revit API calls inside.
    /// </summary>
    internal class AutoParametersWindow : Window
    {
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush ErrorBrush = Frozen(0xB9, 0x1C, 0x1C);
        private static readonly Brush OkBrush = Frozen(0x15, 0x80, 0x3D);
        private static readonly Brush InheritedBrush = Frozen(0x0E, 0x74, 0x90);
        private static readonly FontFamily Mono = new FontFamily("Consolas");

        private static readonly (string Key, string Label)[] FillOptions =
            { ("always", "Always keep current"), ("empty", "Only fill when empty (hand edits kept)") };
        private static readonly (string Key, string Label)[] IncrementOptions =
            { ("keep", "Keep existing numbers"), ("next", "Next after highest (no gap filling)"), ("fill", "Lowest free number (fill gaps)") };
        private static readonly string[] TokenMenu =
        {
            "{Increment|4}", "{CategoryPrefix}", "{Workset}", "{Level.Name}", "{Room.Number}", "{Room.Name}",
            "{ToRoom.Number|FromRoom.Number}", "{Type.Type Mark}", "{Host.Mark}", "{Project.Project Number}", "{Element.Mark}",
        };

        private readonly string _baseUrl, _sourceGuid;
        private readonly List<string> _modelCategories;
        private readonly HashSet<string> _reserved;   // Perseus's own parameter names in this model (naming/ParameterNames)
        private string _layer = "source";
        private JObject _payload;                 // last editor payload from the server
        private bool _dirty, _busy, _loadingForm;
        private List<(string Key, string Label)> _groups = new List<(string, string)> { ("other", "Other") };

        private readonly ObservableCollection<RuleItem> _items = new ObservableCollection<RuleItem>();
        private readonly ObservableCollection<PrefixRow> _prefixes = new ObservableCollection<PrefixRow>();
        private readonly List<string> _ruleCategories = new List<string>();
        private readonly List<string> _pidCategories = new List<string>();

        // chrome
        private readonly ComboBox _scope;
        private readonly TextBlock _status, _header;
        private readonly Button _save, _reload;
        // rules tab
        private readonly ListBox _list;
        private readonly Button _add, _addValue, _dup, _del, _up, _down;
        private readonly StackPanel _detail;
        private readonly TextBlock _inheritedNote, _validation;
        private readonly Button _override;
        private readonly CheckBox _enabled, _readOnly;
        private readonly ComboBox _kind, _group, _fill, _increment;
        private readonly TextBox _name, _pattern, _templateSource, _transform, _uniqueWith, _order;
        private readonly FrameworkElement _valueOnly, _idOnly;
        private readonly RadioButton _allCats, _someCats;
        private readonly CategoryPicker _catPicker;
        // perseus_id tab
        private readonly RadioButton _pidInherit, _pidAll, _pidSome;
        private readonly CategoryPicker _pidPicker;
        private readonly TextBlock _pidNote;
        private readonly DataGrid _prefixGrid;

        internal bool Saved { get; private set; }

        private class RuleItem
        {
            internal JObject Rule;
            internal bool Inherited;             // project rule shown while editing the model layer
            internal bool Overridden;            // project rule the model overrides (shown dimmed)
            internal bool OverridesProject;      // model rule with the name of a project rule
            internal string Name => (string)Rule["parameter_name"] ?? "";
            internal bool IsValue => string.Equals((string)Rule["kind"], "value", StringComparison.OrdinalIgnoreCase);
            internal bool Enabled => Rule["enabled"]?.Type != JTokenType.Boolean || (bool)Rule["enabled"];
            public override string ToString() => Name;
        }

        private class PrefixRow : INotifyPropertyChanged
        {
            private string _category = "", _prefix = "";
            public string Category { get => _category; set { _category = value ?? ""; Changed(nameof(Category)); } }
            public string Prefix { get => _prefix; set { _prefix = value ?? ""; Changed(nameof(Prefix)); } }
            public event PropertyChangedEventHandler PropertyChanged;
            private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        internal AutoParametersWindow(string baseUrl, string sourceGuid, string modelTitle, IEnumerable<string> modelCategories,
                                      IEnumerable<string> reservedNames = null)
        {
            _reserved = new HashSet<string>(reservedNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _baseUrl = baseUrl.TrimEnd('/');
            _sourceGuid = sourceGuid;
            _modelCategories = (modelCategories ?? Enumerable.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

            Title = "Perseus — Auto Parameters";
            Width = 1060; Height = 720; MinWidth = 820; MinHeight = 520;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 12.5;

            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            // ── top bar ──────────────────────────────────────────────────────
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            var topRight = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(topRight, Dock.Right);
            top.Children.Add(topRight);
            topRight.Children.Add(new TextBlock { Text = "Editing:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _scope = new ComboBox { Width = 260, VerticalAlignment = VerticalAlignment.Center };
            _scope.Items.Add(new ComboBoxItem { Content = "This model", Tag = "source" });
            _scope.SelectedIndex = 0;
            _scope.SelectionChanged += (s, e) => OnScopeChanged();
            topRight.Children.Add(_scope);
            _header = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                      Text = string.IsNullOrEmpty(modelTitle) ? "Auto Parameters" : $"Auto Parameters — {modelTitle}",
                                      TextTrimming = TextTrimming.CharacterEllipsis };
            top.Children.Add(_header);

            // ── bottom bar ───────────────────────────────────────────────────
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            _reload = Btn("Reload", (s, e) => { if (ConfirmDiscard()) Load(); });
            _save = Btn("Save", (s, e) => Save());
            _save.FontWeight = FontWeights.SemiBold;
            var close = Btn("Close", (s, e) => Close());
            // The web app's guide to tokens, Jinja templates, transforms and recipes (django-perseus templating_help_view).
            var guide = Btn("Templating guide", (s, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{_baseUrl}/help/templating/") { UseShellExecute = true }); }
                catch (Exception ex) { SetStatus($"Could not open the browser: {ex.Message}", true); }
            });
            buttons.Children.Add(guide); buttons.Children.Add(_reload); buttons.Children.Add(_save); buttons.Children.Add(close);
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Muted };
            bottom.Children.Add(_status);

            // ── tabs ─────────────────────────────────────────────────────────
            var tabs = new TabControl();
            root.Children.Add(tabs);

            // Rules tab
            var rulesGrid = new Grid();
            rulesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 220 });
            rulesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rulesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabs.Items.Add(new TabItem { Header = "Rules", Content = rulesGrid });

            var left = new DockPanel { Margin = new Thickness(6) };
            Grid.SetColumn(left, 0);
            rulesGrid.Children.Add(left);
            var listButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(listButtons, Dock.Bottom);
            left.Children.Add(listButtons);
            _add = SmallBtn("+ ID rule", (s, e) => AddRule("id"));
            _addValue = SmallBtn("+ Value rule", (s, e) => AddRule("value"));
            _dup = SmallBtn("Duplicate", (s, e) => Duplicate());
            _del = SmallBtn("Delete", (s, e) => Delete());
            _up = SmallBtn("▲", (s, e) => Move(-1));
            _down = SmallBtn("▼", (s, e) => Move(1));
            foreach (var b in new[] { _add, _addValue, _dup, _del, _up, _down }) listButtons.Children.Add(b);
            _list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.SelectionChanged += (s, e) => ShowSelected();
            left.Children.Add(_list);

            var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(splitter, 1);
            rulesGrid.Children.Add(splitter);

            var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(detailScroll, 2);
            rulesGrid.Children.Add(detailScroll);
            _detail = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
            detailScroll.Content = _detail;

            _inheritedNote = new TextBlock { Foreground = InheritedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            _override = SmallBtn("Override in this model", (s, e) => OverrideSelected());
            _override.HorizontalAlignment = HorizontalAlignment.Left;
            _override.Margin = new Thickness(0, 0, 0, 10);
            _detail.Children.Add(_inheritedNote);
            _detail.Children.Add(_override);

            _enabled = new CheckBox { Content = "Enabled", Margin = new Thickness(0, 0, 0, 8) };
            _enabled.Click += (s, e) => FromForm();
            _detail.Children.Add(_enabled);

            _kind = Combo(new[] { ("id", "ID — unique, numbered ({Increment|N})"), ("value", "Value — copied / computed, not unique") });
            _detail.Children.Add(Field("Kind", _kind));

            _name = Text();
            _detail.Children.Add(Field("Parameter name", _name, "The Revit instance parameter Perseus creates and fills."));

            _pattern = Text(multiline: true, mono: true);
            var tokenBtn = SmallBtn("Insert token ▾", null);
            var tokenMenu = new ContextMenu();
            foreach (var t in TokenMenu)
            {
                var mi = new MenuItem { Header = t, FontFamily = Mono };
                string token = t;
                mi.Click += (s, e) => InsertToken(token);
                tokenMenu.Items.Add(mi);
            }
            tokenBtn.Click += (s, e) => { tokenMenu.PlacementTarget = tokenBtn; tokenMenu.IsOpen = true; };
            tokenBtn.HorizontalAlignment = HorizontalAlignment.Left;
            var patternBox = new StackPanel();
            patternBox.Children.Add(_pattern);
            patternBox.Children.Add(tokenBtn);
            _detail.Children.Add(Field("Pattern", patternBox,
                "Tokens: {CategoryPrefix} {Workset} {Increment|N} and {Level|Host|Room|Space|ToRoom|FromRoom|Type|Group|Project|Element.Parameter}; " +
                "{A|B} = first with a value. Value rules may be a Jinja template ({{ Element.Mark }}, {% if %}…{% endif %}). " +
                "Examples and recipes: the Templating guide button below."));

            // value-only
            var valuePanel = new StackPanel();
            _templateSource = Text(mono: true);
            valuePanel.Children.Add(Field("Template from (optional)", _templateSource,
                "Render the template held in this parameter instead, e.g. Type.Tag Template; the pattern is then the default for empty ones."));
            _transform = Text(mono: true);
            valuePanel.Children.Add(Field("Transform (optional)", _transform,
                "Expression over value (the filled-in pattern) and row[\"Level.Name\"], e.g. re.sub(r'\\D', '', value)."));
            _fill = Combo(FillOptions);
            valuePanel.Children.Add(Field("Fill", _fill));
            _valueOnly = valuePanel;
            _detail.Children.Add(valuePanel);

            // id-only
            var idPanel = new StackPanel();
            _uniqueWith = Text(mono: true);
            idPanel.Children.Add(Field("Unique with (optional)", _uniqueWith,
                "Tokens the number only has to be unique with, not printed, e.g. {Element.Door Tag Room Number} numbers doors D01, D02… per room."));
            _increment = Combo(IncrementOptions);
            idPanel.Children.Add(Field("New numbers", _increment));
            _order = Text(mono: true);
            idPanel.Children.Add(Field("Numbering order (Renumber)", _order,
                "Keys X, Y, Z or Id; '-' = descending, '~N' = round to N mm, e.g. 'X, -Y' or '-Y~500, X'. Blank = ElementId order."));
            _idOnly = idPanel;
            _detail.Children.Add(idPanel);

            _group = Combo(_groups.ToArray());
            _detail.Children.Add(Field("Parameter group (Properties palette)", _group));

            _readOnly = new CheckBox { Content = "Locked — greyed out in Revit, only Perseus writes it (switching an existing parameter happens on the next Audit)",
                                       Margin = new Thickness(0, 4, 0, 10) };
            _readOnly.Click += (s, e) => FromForm();
            _detail.Children.Add(_readOnly);

            var cats = new StackPanel();
            _allCats = new RadioButton { Content = "All model categories", GroupName = "rulecats", Margin = new Thickness(0, 0, 0, 2) };
            _someCats = new RadioButton { Content = "Only these categories:", GroupName = "rulecats" };
            _allCats.Checked += (s, e) => { _catPicker.IsEnabled = false; FromForm(); };
            _someCats.Checked += (s, e) => { _catPicker.IsEnabled = true; FromForm(); };
            _catPicker = new CategoryPicker(_ruleCategories, () => FromForm()) { Height = 200, Margin = new Thickness(18, 4, 0, 0) };
            cats.Children.Add(_allCats); cats.Children.Add(_someCats); cats.Children.Add(_catPicker);
            _detail.Children.Add(Field("Applies to", cats));

            _validation = new TextBlock { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            _detail.Children.Add(_validation);

            foreach (var tb in new[] { _name, _pattern, _templateSource, _transform, _uniqueWith, _order })
                tb.TextChanged += (s, e) => FromForm();
            foreach (var cb in new[] { _kind, _group, _fill, _increment })
                cb.SelectionChanged += (s, e) => FromForm();

            // Category prefixes tab
            var prefixPanel = new DockPanel { Margin = new Thickness(6) };
            var prefixHelp = new TextBlock { Text = "{CategoryPrefix} in a pattern becomes the prefix of the element's category, e.g. Doors → D, Walls → W.",
                                             Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(prefixHelp, Dock.Top);
            prefixPanel.Children.Add(prefixHelp);
            var prefixButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(prefixButtons, Dock.Bottom);
            prefixPanel.Children.Add(prefixButtons);
            var prefixGrid = _prefixGrid = new DataGrid
            {
                ItemsSource = _prefixes, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended,
            };
            // Free text, not a drop-down: a prefix may name a category this model doesn't have (project defaults).
            prefixGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Category (Revit name, e.g. Doors)", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new System.Windows.Data.Binding(nameof(PrefixRow.Category)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
            });
            prefixGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Prefix", Width = new DataGridLength(140),
                Binding = new System.Windows.Data.Binding(nameof(PrefixRow.Prefix)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
            });
            prefixGrid.CellEditEnding += (s, e) => MarkDirty();
            prefixButtons.Children.Add(SmallBtn("+ Prefix", (s, e) => { _prefixes.Add(new PrefixRow()); MarkDirty(); }));
            prefixButtons.Children.Add(SmallBtn("Delete selected", (s, e) =>
            {
                foreach (var r in prefixGrid.SelectedItems.OfType<PrefixRow>().ToList()) _prefixes.Remove(r);
                MarkDirty();
            }));
            prefixPanel.Children.Add(prefixGrid);
            tabs.Items.Add(new TabItem { Header = "Category prefixes", Content = prefixPanel });

            // Perseus_ID tab
            var pid = new StackPanel { Margin = new Thickness(10) };
            pid.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8),
                Text = "Perseus_ID holds each element's UniqueId so the web app can match it. Choose which categories receive it." });
            _pidInherit = new RadioButton { GroupName = "pid", Margin = new Thickness(0, 0, 0, 2) };
            _pidAll = new RadioButton { Content = "All categories", GroupName = "pid", Margin = new Thickness(0, 0, 0, 2) };
            _pidSome = new RadioButton { Content = "Only these categories:", GroupName = "pid" };
            _pidPicker = new CategoryPicker(_pidCategories, () => MarkDirty()) { Height = 320, Margin = new Thickness(18, 4, 0, 0) };
            foreach (var rb in new[] { _pidInherit, _pidAll, _pidSome })
                rb.Checked += (s, e) => { _pidPicker.IsEnabled = _pidSome.IsChecked == true; if (!_loadingForm) MarkDirty(); };
            _pidNote = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            pid.Children.Add(_pidInherit); pid.Children.Add(_pidAll); pid.Children.Add(_pidSome); pid.Children.Add(_pidPicker); pid.Children.Add(_pidNote);
            tabs.Items.Add(new TabItem { Header = "Perseus_ID", Content = new ScrollViewer { Content = pid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            Closing += (s, e) => { if (!_busy && !ConfirmDiscard()) e.Cancel = true; if (_busy) e.Cancel = true; };
            Loaded += (s, e) => Load();
            ShowSelected();
        }

        // ── load / save ─────────────────────────────────────────────────────

        private void Load()
        {
            SetBusy(true, "Loading rules from the server…");
            string layer = _layer;
            string url = $"{_baseUrl}/api/source/{_sourceGuid}/id-rules/?layer={layer}";
            Task.Run(() =>
            {
                try
                {
                    string json = WebHelper.Get(url, AuthService.GetAuthTokenSafely(), null, AuthService.GetAuthSchemeSafely());
                    var payload = JObject.Parse(json);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { Apply(payload); SetBusy(false, LoadedText(payload)); }
                        catch (Exception ex)
                        {
                            Log.Error($"[AutoParametersWindow] showing the loaded {layer} rules failed");
                            Log.Exception(ex);
                            SetBusy(false, $"Could not show the {layer} rules: {ex.Message}", true);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    string msg = PairingSession.Describe(ex);
                    Log.Warn($"[AutoParametersWindow] load failed: {msg}");
                    Dispatcher.BeginInvoke(new Action(() =>
                        SetBusy(false, msg.Contains("404") || msg.Contains("Not Found")
                            ? "This model isn't registered on the server yet (open / sync it with Perseus first), or the server is too old for this editor."
                            : $"Could not load the rules: {msg}", true)));
                }
            });
        }

        /// <summary>payload["a"]["b"]... that gives null when a step is missing or not an object (the server sends
        /// "inherited": null for the project layer; indexing a JValue throws).</summary>
        private static JToken At(JToken t, params string[] path)
        {
            foreach (var k in path)
            {
                t = (t as JObject)?[k];
                if (t == null || t.Type == JTokenType.Null) return null;
            }
            return t;
        }

        private static string LoadedText(JObject payload)
        {
            string at = (string)At(payload, "rules", "updated_at");
            return string.IsNullOrEmpty(at) ? "Loaded. Nothing saved for this layer yet." : $"Loaded. Last saved {FormatTime(at)}.";
        }

        private void Save()
        {
            CommitPending();
            var body = BuildBody();
            var problems = _items.Where(i => !i.Inherited).Select(i => (i.Name, Problem(i.Rule))).Where(p => p.Item2 != null).ToList();
            if (problems.Count > 0)
            {
                SetStatus("Fix these first:\n" + string.Join("\n", problems.Select(p => $"• {(string.IsNullOrEmpty(p.Name) ? "(unnamed)" : p.Name)}: {p.Item2}")), true);
                return;
            }
            SetBusy(true, "Saving…");
            string layer = _layer;
            string url = $"{_baseUrl}/api/source/{_sourceGuid}/id-rules/?layer={layer}";
            string json = body.ToString(Formatting.None);
            Task.Run(() =>
            {
                try
                {
                    string response = PairingSession.Put(_baseUrl, _sourceGuid, url, json,
                        (text, isError) => Dispatcher.BeginInvoke(new Action(() => SetStatus(text, isError))));
                    if (response == null)
                    {
                        Dispatcher.BeginInvoke(new Action(() => SetBusy(false, null)));
                        return;
                    }
                    var payload = JObject.Parse(response);
                    Log.Info($"[AutoParametersWindow] saved {layer} layer for {_sourceGuid}: {body["rules"]?.Count() ?? 0} rule(s)");
                    // The plugin applies the effective rules (project + model): refresh them for the assigner.
                    ids.IdRulesCache.LoadAsync(_sourceGuid, _baseUrl);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Saved = true;
                        _dirty = false;
                        Apply(payload);
                        SetBusy(false, null);
                        SetStatus("Saved. New and edited elements follow the new rules now; run Audit to apply them to existing elements.", false, ok: true);
                    }));
                }
                catch (Exception ex)
                {
                    string msg = PairingSession.Describe(ex);
                    Log.Warn($"[AutoParametersWindow] save failed: {msg}");
                    Dispatcher.BeginInvoke(new Action(() => { SetBusy(false, null); SetStatus("Not saved:\n" + msg, true); }));
                }
            });
        }

        private JObject BuildBody()
        {
            var rules = new JArray(_items.Where(i => !i.Inherited).Select(i => (JObject)i.Rule.DeepClone()));
            var prefixes = new JArray(_prefixes.Where(p => !string.IsNullOrWhiteSpace(p.Category) || !string.IsNullOrWhiteSpace(p.Prefix))
                .Select(p => new JObject { ["category"] = p.Category.Trim(), ["prefix"] = p.Prefix.Trim() }));
            JToken pidAll = _pidAll.IsChecked == true ? (JToken)true : _pidSome.IsChecked == true ? (JToken)false : JValue.CreateNull();
            return new JObject
            {
                ["rules"] = rules,
                ["category_prefixes"] = prefixes,
                ["perseus_id"] = new JObject { ["apply_to_all"] = pidAll, ["categories"] = new JArray(_pidPicker.Checked) },
            };
        }

        private void Apply(JObject payload)
        {
            _payload = payload;
            _loadingForm = true;
            try
            {
                _layer = (string)payload["layer"] ?? _layer;
                var groups = (payload["parameter_groups"] as JArray)?.OfType<JArray>()
                    .Select(a => ((string)a[0], (string)a[1])).Where(g => !string.IsNullOrEmpty(g.Item1)).ToList();
                if (groups != null && groups.Count > 0)
                {
                    _groups = groups;
                    FillCombo(_group, _groups.ToArray());
                }

                // Scope picker: add "Project defaults" when the model belongs to a project.
                var proj = payload["project"] as JObject;
                if (proj != null && _scope.Items.Count == 1)
                    _scope.Items.Add(new ComboBoxItem { Content = $"Project defaults — {(string)proj["name"]}", Tag = "project" });
                foreach (ComboBoxItem it in _scope.Items)
                    if ((string)it.Tag == _layer) _scope.SelectedItem = it;

                var own = (At(payload, "rules", "rules") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                var inherited = (At(payload, "inherited", "rules") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
                var ownNames = new HashSet<string>(own.Select(r => (string)r["parameter_name"] ?? ""), StringComparer.OrdinalIgnoreCase);
                var inhNames = new HashSet<string>(inherited.Select(r => (string)r["parameter_name"] ?? ""), StringComparer.OrdinalIgnoreCase);

                _items.Clear();
                foreach (var r in own)
                    _items.Add(new RuleItem { Rule = r, OverridesProject = inhNames.Contains((string)r["parameter_name"] ?? "") });
                foreach (var r in inherited)
                    _items.Add(new RuleItem { Rule = r, Inherited = true, Overridden = ownNames.Contains((string)r["parameter_name"] ?? "") });

                _prefixes.Clear();
                foreach (var p in (At(payload, "rules", "category_prefixes") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    _prefixes.Add(new PrefixRow { Category = (string)p["category"], Prefix = (string)p["prefix"] });

                // Categories offered in the pickers: the model's + any already named in rules / prefixes.
                var all = new HashSet<string>(_modelCategories, StringComparer.OrdinalIgnoreCase);
                foreach (var r in own.Concat(inherited))
                    foreach (var c in (r["categories"] as JArray)?.Select(t => (string)t) ?? Enumerable.Empty<string>()) if (!string.IsNullOrWhiteSpace(c)) all.Add(c);
                var pidCats = (At(payload, "rules", "perseus_id", "categories") as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
                foreach (var c in pidCats) if (!string.IsNullOrWhiteSpace(c)) all.Add(c);
                var sorted = all.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                _ruleCategories.Clear(); _ruleCategories.AddRange(sorted);
                _pidCategories.Clear(); _pidCategories.AddRange(sorted);

                // Perseus_ID
                var pidAll = At(payload, "rules", "perseus_id", "apply_to_all");
                bool isProject = _layer == "project";
                _pidInherit.Content = isProject ? "Not set — same categories as the enabled ID rules" : "Not set here — use the project's choice (else the ID rules' categories)";
                _pidPicker.SetItems(_pidCategories, pidCats);
                if (pidAll == null || pidAll.Type == JTokenType.Null) _pidInherit.IsChecked = true;
                else if ((bool)pidAll) _pidAll.IsChecked = true;
                else _pidSome.IsChecked = true;
                _pidPicker.IsEnabled = _pidSome.IsChecked == true;
                var inhPid = At(payload, "inherited", "perseus_id", "apply_to_all");
                _pidNote.Text = isProject || inhPid == null || inhPid.Type == JTokenType.Null ? "" :
                    "The project sets: " + ((bool)inhPid ? "all categories." :
                        string.Join(", ", (At(payload, "inherited", "perseus_id", "categories") as JArray)?.Select(t => (string)t) ?? Enumerable.Empty<string>()) + ".");

                _dirty = false;
                RefreshList(_items.FirstOrDefault());
            }
            finally { _loadingForm = false; }
            ShowSelected();
        }

        // ── list ────────────────────────────────────────────────────────────

        private void RefreshList(RuleItem select)
        {
            _list.Items.Clear();
            var own = _items.Where(i => !i.Inherited).ToList();
            var inh = _items.Where(i => i.Inherited).ToList();
            if (own.Count == 0)
                _list.Items.Add(Header(_layer == "project" ? "No project rules yet" : "No rules of this model's own yet"));
            foreach (var i in own) _list.Items.Add(Row(i));
            if (inh.Count > 0)
            {
                _list.Items.Add(Header("From the project (read-only here)"));
                foreach (var i in inh) _list.Items.Add(Row(i));
            }
            var target = _list.Items.OfType<ListBoxItem>().FirstOrDefault(li => li.Tag == select);
            _list.SelectedItem = target ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault(li => li.Tag is RuleItem);
        }

        private static ListBoxItem Header(string text) => new ListBoxItem
        {
            Content = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 6, 0, 2) },
            IsEnabled = false, Focusable = false,
        };

        private ListBoxItem Row(RuleItem i)
        {
            var sp = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            var line1 = new DockPanel();
            var badge = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0),
                Background = i.IsValue ? Frozen(0xE0, 0xF2, 0xFE) : Frozen(0xFC, 0xE7, 0xF3),
                Child = new TextBlock { Text = i.IsValue ? "VALUE" : "ID", FontSize = 10, FontWeight = FontWeights.Bold,
                                        Foreground = i.IsValue ? Frozen(0x03, 0x69, 0xA1) : Frozen(0x9D, 0x17, 0x4D) },
            };
            DockPanel.SetDock(badge, Dock.Left);
            line1.Children.Add(badge);
            string suffix = i.Overridden ? "  (overridden by this model)" : i.OverridesProject ? "  (overrides project)" : !i.Enabled ? "  (disabled)" : "";
            line1.Children.Add(new TextBlock { Text = (string.IsNullOrEmpty(i.Name) ? "(unnamed)" : i.Name) + suffix, FontWeight = FontWeights.SemiBold,
                                               TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(line1);
            sp.Children.Add(new TextBlock { Text = (string)i.Rule["pattern"] ?? "", FontFamily = Mono, FontSize = 11, Foreground = Muted,
                                            TextTrimming = TextTrimming.CharacterEllipsis });
            if (!i.Enabled || i.Overridden || i.Inherited) sp.Opacity = i.Inherited && !i.Overridden ? 0.85 : 0.55;
            return new ListBoxItem { Content = sp, Tag = i };
        }

        private RuleItem Selected => (_list.SelectedItem as ListBoxItem)?.Tag as RuleItem;

        private void UpdateSelectedRow()
        {
            if (!(_list.SelectedItem is ListBoxItem li) || !(li.Tag is RuleItem i)) return;
            var fresh = Row(i);
            li.Content = fresh.Content;   // keeps selection / focus in the form
        }

        private void AddRule(string kind)
        {
            if (_busy) return;
            CommitPending();
            var rule = new JObject
            {
                ["kind"] = kind,
                ["parameter_name"] = UniqueName(kind == "id" ? "New ID" : "New Value"),
                ["pattern"] = kind == "id" ? "{CategoryPrefix}{Increment|4}" : "{Level.Name}",
                ["transform"] = "", ["fill"] = "always", ["parameter_group"] = "other",
                ["apply_to_all"] = false, ["categories"] = new JArray(), ["enabled"] = true,
                ["numbering_order"] = "", ["unique_with"] = "", ["increment_mode"] = "keep",
                ["read_only"] = false, ["template_source"] = "",
            };
            var item = new RuleItem { Rule = rule };
            int insertAt = _items.Count(x => !x.Inherited);
            _items.Insert(insertAt, item);
            MarkDirty();
            RecomputeOverrides();
            RefreshList(item);
            _name.Focus();
            _name.SelectAll();
        }

        private void Duplicate()
        {
            var sel = Selected;
            if (sel == null || _busy) return;
            CommitPending();
            var copy = (JObject)sel.Rule.DeepClone();
            copy["parameter_name"] = UniqueName(sel.Name + " copy");
            var item = new RuleItem { Rule = copy };
            _items.Insert(_items.Count(x => !x.Inherited), item);
            MarkDirty();
            RecomputeOverrides();
            RefreshList(item);
        }

        private void OverrideSelected()
        {
            var sel = Selected;
            if (sel == null || !sel.Inherited || _busy) return;
            var existing = _items.FirstOrDefault(x => !x.Inherited && string.Equals(x.Name, sel.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { RefreshList(existing); return; }
            var item = new RuleItem { Rule = (JObject)sel.Rule.DeepClone() };
            _items.Insert(_items.Count(x => !x.Inherited), item);
            MarkDirty();
            RecomputeOverrides();
            RefreshList(item);
        }

        private void Delete()
        {
            var sel = Selected;
            if (sel == null || sel.Inherited || _busy) return;
            string note = sel.OverridesProject ? "\n\nThe project's rule of the same name applies again once this override is gone." : "";
            if (MessageBox.Show(this, $"Delete the rule '{sel.Name}'?{note}\n\nExisting values in the model are left as they are.",
                    "Auto Parameters", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            int idx = _items.IndexOf(sel);
            _items.Remove(sel);
            MarkDirty();
            RecomputeOverrides();
            var own = _items.Where(x => !x.Inherited).ToList();
            RefreshList(own.Count == 0 ? null : own[Math.Min(idx, own.Count - 1)]);
        }

        private void Move(int delta)
        {
            var sel = Selected;
            if (sel == null || sel.Inherited || _busy) return;
            var own = _items.Where(x => !x.Inherited).ToList();
            int i = own.IndexOf(sel), j = i + delta;
            if (j < 0 || j >= own.Count) return;
            _items.Move(_items.IndexOf(sel), _items.IndexOf(own[j]));
            MarkDirty();
            RefreshList(sel);
        }

        private void RecomputeOverrides()
        {
            var ownNames = new HashSet<string>(_items.Where(x => !x.Inherited).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            var inhNames = new HashSet<string>(_items.Where(x => x.Inherited).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var x in _items)
            {
                if (x.Inherited) x.Overridden = ownNames.Contains(x.Name);
                else x.OverridesProject = inhNames.Contains(x.Name);
            }
        }

        private string UniqueName(string stem)
        {
            var names = new HashSet<string>(_items.Where(x => !x.Inherited).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(stem)) return stem;
            for (int n = 2; ; n++) if (!names.Contains($"{stem} {n}")) return $"{stem} {n}";
        }

        // ── detail form ─────────────────────────────────────────────────────

        private void ShowSelected()
        {
            var sel = Selected;
            _loadingForm = true;
            try
            {
                bool has = sel != null;
                foreach (UIElement c in _detail.Children) c.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                _dup.IsEnabled = has && !_busy;
                _del.IsEnabled = _up.IsEnabled = _down.IsEnabled = has && !sel.Inherited && !_busy;
                if (!has) return;

                var r = sel.Rule;
                _inheritedNote.Visibility = sel.Inherited || sel.OverridesProject ? Visibility.Visible : Visibility.Collapsed;
                _inheritedNote.Text = sel.Inherited
                    ? (sel.Overridden ? "From the project — this model overrides it with its own rule of the same name."
                                      : "From the project's defaults. Edit it under 'Project defaults', or override it for this model only.")
                    : "Overrides the project's rule of the same name for this model.";
                _override.Visibility = sel.Inherited && !sel.Overridden ? Visibility.Visible : Visibility.Collapsed;

                _enabled.IsChecked = sel.Enabled;
                SelectKey(_kind, sel.IsValue ? "value" : "id");
                _name.Text = (string)r["parameter_name"] ?? "";
                _pattern.Text = (string)r["pattern"] ?? "";
                _templateSource.Text = (string)r["template_source"] ?? "";
                _transform.Text = (string)r["transform"] ?? "";
                SelectKey(_fill, (string)r["fill"] ?? "always");
                _uniqueWith.Text = (string)r["unique_with"] ?? "";
                SelectKey(_increment, (string)r["increment_mode"] ?? "keep");
                _order.Text = (string)r["numbering_order"] ?? "";
                SelectKey(_group, (string)r["parameter_group"] ?? "other");
                _readOnly.IsChecked = r["read_only"]?.Type == JTokenType.Boolean && (bool)r["read_only"];
                bool all = r["apply_to_all"]?.Type != JTokenType.Boolean || (bool)r["apply_to_all"];
                _allCats.IsChecked = all; _someCats.IsChecked = !all;
                _catPicker.SetItems(_ruleCategories, (r["categories"] as JArray)?.Select(t => (string)t) ?? Enumerable.Empty<string>());
                _catPicker.IsEnabled = !all;

                bool editable = !sel.Inherited && !_busy;
                foreach (var c in new Control[] { _enabled, _kind, _name, _pattern, _templateSource, _transform, _fill, _uniqueWith,
                                                   _increment, _order, _group, _readOnly, _allCats, _someCats })
                    c.IsEnabled = editable;
                foreach (var tb in new[] { _name, _pattern, _templateSource, _transform, _uniqueWith, _order }) tb.IsReadOnly = !editable;
                _catPicker.ReadOnly = !editable;
                UpdateKindPanels();
                _validation.Text = sel.Inherited ? "" : Problem(r) ?? "";
            }
            finally { _loadingForm = false; }
        }

        private void FromForm()
        {
            if (_loadingForm) return;
            var sel = Selected;
            if (sel == null || sel.Inherited) return;
            var r = sel.Rule;
            string kind = SelectedKey(_kind) ?? "id";
            bool kindChanged = !string.Equals((string)r["kind"], kind, StringComparison.OrdinalIgnoreCase);
            r["enabled"] = _enabled.IsChecked == true;
            r["kind"] = kind;
            r["parameter_name"] = _name.Text.Trim();
            r["pattern"] = _pattern.Text;
            r["template_source"] = _templateSource.Text.Trim();
            r["transform"] = _transform.Text.Trim();
            r["fill"] = SelectedKey(_fill) ?? "always";
            r["unique_with"] = _uniqueWith.Text.Trim();
            r["increment_mode"] = SelectedKey(_increment) ?? "keep";
            r["numbering_order"] = _order.Text.Trim();
            r["parameter_group"] = SelectedKey(_group) ?? "other";
            r["read_only"] = _readOnly.IsChecked == true;
            r["apply_to_all"] = _allCats.IsChecked == true;
            r["categories"] = new JArray(_catPicker.Checked);
            if (kindChanged) UpdateKindPanels();
            RecomputeOverrides();
            _validation.Text = Problem(r) ?? "";
            MarkDirty();
            UpdateSelectedRow();
        }

        private void UpdateKindPanels()
        {
            bool isValue = SelectedKey(_kind) == "value";
            _valueOnly.Visibility = isValue ? Visibility.Visible : Visibility.Collapsed;
            _idOnly.Visibility = isValue ? Visibility.Collapsed : Visibility.Visible;
        }

        private void InsertToken(string token)
        {
            if (Selected == null || Selected.Inherited) return;
            int at = _pattern.CaretIndex;
            _pattern.Text = _pattern.Text.Insert(at, token);
            _pattern.CaretIndex = at + token.Length;
            _pattern.Focus();
        }

        // Quick local checks mirroring the server (django-perseus _validate_id_rules_payload); the server has the last word.
        private string Problem(JObject r)
        {
            string name = ((string)r["parameter_name"] ?? "").Trim();
            string pattern = (string)r["pattern"] ?? "";
            bool isValue = string.Equals((string)r["kind"], "value", StringComparison.OrdinalIgnoreCase);
            if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9 _\-]{0,63}$"))
                return "Parameter name must start with a letter and use letters, digits, spaces, _ or - (max 64).";
            if (string.Equals(name, "Perseus_ID", StringComparison.OrdinalIgnoreCase) || _reserved.Contains(name))
                return $"'{name}' is one of Perseus's own parameters (see Parameter Naming).";
            if (_items.Count(x => !x.Inherited && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 1)
                return "Another rule already uses this parameter name.";
            bool jinja = isValue && (pattern.Contains("{{") || pattern.Contains("{%") || pattern.Contains("{#"));
            int inc = jinja ? 0 : Regex.Matches(pattern, @"\{\s*Increment\|").Count;
            if (!isValue && inc != 1) return "An ID rule needs exactly one {Increment|N} (or make it a Value rule).";
            if (isValue && inc > 0) return "A Value rule can't use {Increment|N}.";
            if (isValue && string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace((string)r["template_source"])) return "Pattern is empty.";
            if (!jinja && pattern.Count(c => c == '{') != pattern.Count(c => c == '}')) return "Unmatched brace in the pattern.";
            bool all = r["apply_to_all"]?.Type != JTokenType.Boolean || (bool)r["apply_to_all"];
            if (!all && !((r["categories"] as JArray)?.Any() ?? false)) return "Choose at least one category (or All model categories).";
            return null;
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private void OnScopeChanged()
        {
            if (_loadingForm || _payload == null) return;
            string layer = (string)((_scope.SelectedItem as ComboBoxItem)?.Tag) ?? "source";
            if (layer == _layer) return;
            if (!ConfirmDiscard())
            {
                _loadingForm = true;
                foreach (ComboBoxItem it in _scope.Items) if ((string)it.Tag == _layer) _scope.SelectedItem = it;
                _loadingForm = false;
                return;
            }
            _layer = layer;
            Load();
        }

        private bool ConfirmDiscard()
        {
            CommitPending();
            if (!_dirty) return true;
            return MessageBox.Show(this, "Discard your unsaved changes?", "Auto Parameters",
                       MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        private void CommitPending()
        {
            // A prefix cell still being edited only reaches its row when the edit ends; end it.
            try { _prefixGrid.CommitEdit(DataGridEditingUnit.Row, true); } catch (InvalidOperationException) { }
        }

        private void MarkDirty()
        {
            if (_loadingForm) return;
            _dirty = true;
            SetStatus("Unsaved changes.", false);
        }

        private void SetBusy(bool busy, string text, bool error = false)
        {
            _busy = busy;
            _save.IsEnabled = _reload.IsEnabled = _scope.IsEnabled = !busy;
            _add.IsEnabled = _addValue.IsEnabled = !busy;
            if (text != null) SetStatus(text, error);
            ShowSelected();
        }

        private void SetStatus(string text, bool error, bool ok = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? ErrorBrush : ok ? OkBrush : Muted;
        }

        private static string FormatTime(string iso) =>
            DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("d MMM yyyy HH:mm") : iso;

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, MinWidth = 90, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            if (click != null) b.Click += click;
            return b;
        }

        private static Button SmallBtn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4) };
            if (click != null) b.Click += click;
            return b;
        }

        private static TextBox Text(bool multiline = false, bool mono = false)
        {
            var t = new TextBox { Padding = new Thickness(3) };
            if (mono) t.FontFamily = Mono;
            if (multiline)
            {
                t.AcceptsReturn = true; t.TextWrapping = TextWrapping.Wrap; t.MinHeight = 54; t.MaxHeight = 160;
                t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            return t;
        }

        private static ComboBox Combo((string Key, string Label)[] options)
        {
            var c = new ComboBox();
            FillCombo(c, options);
            return c;
        }

        private static void FillCombo(ComboBox c, (string Key, string Label)[] options)
        {
            string keep = SelectedKey(c);
            c.Items.Clear();
            foreach (var o in options) c.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Key });
            if (keep != null) SelectKey(c, keep);
        }

        private static string SelectedKey(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;

        private static void SelectKey(ComboBox c, string key)
        {
            foreach (ComboBoxItem it in c.Items)
                if (string.Equals((string)it.Tag, key, StringComparison.OrdinalIgnoreCase)) { c.SelectedItem = it; return; }
            if (!string.IsNullOrEmpty(key))
            {
                var extra = new ComboBoxItem { Content = key, Tag = key };   // unknown server value: keep it
                c.Items.Add(extra);
                c.SelectedItem = extra;
            }
        }

        private static FrameworkElement Field(string label, UIElement editor, string help = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            sp.Children.Add(editor);
            if (!string.IsNullOrEmpty(help))
                sp.Children.Add(new TextBlock { Text = help, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            return sp;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>Filterable tick list of category names.</summary>
        private class CategoryPicker : DockPanel
        {
            private readonly TextBox _filter = new TextBox { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(3) };
            private readonly ListBox _box = new ListBox();
            private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Action _changed;
            private List<string> _all;
            private bool _readOnly, _filling;

            internal CategoryPicker(List<string> all, Action changed)
            {
                _all = all;
                _changed = changed;
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                DockPanel.SetDock(head, Dock.Top);
                var count = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(count, Dock.Right);
                head.Children.Add(count);
                head.Children.Add(_filter);
                Children.Add(head);
                Children.Add(_box);
                _filter.TextChanged += (s, e) => Fill();
                _countLabel = count;
            }

            private readonly TextBlock _countLabel;

            internal IEnumerable<string> Checked => _all.Where(c => _checked.Contains(c))
                .Concat(_checked.Where(c => !_all.Contains(c, StringComparer.OrdinalIgnoreCase)));

            internal bool ReadOnly { get => _readOnly; set { _readOnly = value; Fill(); } }

            internal void SetItems(List<string> all, IEnumerable<string> chosen)
            {
                _all = all;
                _checked.Clear();
                foreach (var c in chosen) if (!string.IsNullOrWhiteSpace(c)) _checked.Add(c);
                Fill();
            }

            private void Fill()
            {
                _filling = true;
                try
                {
                    _box.Items.Clear();
                    string f = _filter.Text.Trim();
                    // Ticked first, then the rest, filtered.
                    var names = _all.Where(c => _checked.Contains(c)).Concat(_all.Where(c => !_checked.Contains(c)))
                        .Where(c => f.Length == 0 || c.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                    foreach (var name in names)
                    {
                        var cb = new CheckBox { Content = name, IsChecked = _checked.Contains(name), IsEnabled = !_readOnly };
                        string n = name;
                        cb.Click += (s, e) =>
                        {
                            if (_filling) return;
                            if (cb.IsChecked == true) _checked.Add(n); else _checked.Remove(n);
                            _countLabel.Text = $"{_checked.Count} ticked";
                            _changed?.Invoke();
                        };
                        _box.Items.Add(cb);
                    }
                    _countLabel.Text = $"{_checked.Count} ticked";
                }
                finally { _filling = false; }
            }
        }
    }
}
