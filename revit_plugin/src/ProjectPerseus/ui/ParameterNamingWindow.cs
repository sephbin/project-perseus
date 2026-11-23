using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// In-Revit editor for the parameter naming convention held by django-perseus (same data as the web pages
    /// /source/&lt;guid&gt;/parameter-naming/, /project/&lt;id&gt;/parameter-naming/, /parameter-naming/): prefix, parameter group
    /// and a name per Perseus parameter key. One layer at a time: This model / Project / Server-wide (GET / PUT
    /// /api/source/&lt;guid&gt;/parameter-naming/?layer=...; saves via auth/PairingSession). A project whose convention
    /// differs from today's server-wide one can adopt it. Renaming the parameters already in the model needs the Revit
    /// API, so "Rename parameters…" closes the window with RenameRequested and commands/EditParameterNamingCommand does it.
    /// HTTP on background tasks; no Revit API calls inside.
    /// </summary>
    internal class ParameterNamingWindow : Window
    {
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush ErrorBrush = Frozen(0xB9, 0x1C, 0x1C);
        private static readonly Brush OkBrush = Frozen(0x15, 0x80, 0x3D);
        private static readonly Brush InfoBrush = Frozen(0x0E, 0x74, 0x90);
        private static readonly FontFamily Mono = new FontFamily("Consolas");
        private static readonly Dictionary<string, string> ScopeLabel = new Dictionary<string, string>
            { ["default"] = "built-in", ["server"] = "server-wide", ["project"] = "project", ["source"] = "this model" };

        private readonly string _baseUrl, _sourceGuid;
        private string _layer = "source";
        private JObject _payload;
        private bool _dirty, _loading;

        private readonly ComboBox _scope, _group;
        private readonly TextBlock _status, _lead, _differs;
        private readonly CheckBox _prefixSet;
        private readonly TextBox _prefix;
        private readonly Grid _rows;
        private readonly Button _save, _reload, _adopt, _rename;
        private readonly Dictionary<string, (TextBox Box, TextBlock Result, string Default)> _keyRows =
            new Dictionary<string, (TextBox, TextBlock, string)>();

        internal bool Saved { get; private set; }
        internal bool RenameRequested { get; private set; }

        /// <param name="pendingRenames">Perseus parameters in the model whose name differs from the convention it was
        /// opened with (naming/ParameterNames.Pending), shown on the Rename button.</param>
        internal ParameterNamingWindow(string baseUrl, string sourceGuid, string modelTitle, bool hasProject, bool isStaffHint, int pendingRenames)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _sourceGuid = sourceGuid;

            Title = "Perseus — Parameter Naming";
            Width = 860; Height = 600; MinWidth = 640; MinHeight = 420;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 12.5;

            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            // top bar
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            var topRight = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(topRight, Dock.Right);
            top.Children.Add(topRight);
            topRight.Children.Add(new TextBlock { Text = "Editing:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _scope = new ComboBox { Width = 220 };
            _scope.Items.Add(new ComboBoxItem { Content = "This model", Tag = "source" });
            if (hasProject) _scope.Items.Add(new ComboBoxItem { Content = "Project", Tag = "project" });
            _scope.Items.Add(new ComboBoxItem { Content = "Server-wide (office)" + (isStaffHint ? "" : " — staff only"), Tag = "server" });
            _scope.SelectedIndex = 0;
            _scope.SelectionChanged += (s, e) => OnScopeChanged();
            topRight.Children.Add(_scope);
            top.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(modelTitle) ? "Parameter Naming" : $"Parameter Naming — {modelTitle}",
                FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // bottom bar
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            var help = Btn("Help", (s, e) => OpenUrl($"{_baseUrl}/help/parameter-naming/"));
            _rename = Btn(pendingRenames > 0 ? $"Rename parameters ({pendingRenames})…" : "Rename parameters…", (s, e) => RequestRename());
            _rename.ToolTip = "Rename the Perseus parameters already in this model to the convention's names (values are copied across).";
            _reload = Btn("Reload", (s, e) => { if (ConfirmDiscard()) Load(); });
            _save = Btn("Save", (s, e) => Save());
            _save.FontWeight = FontWeights.SemiBold;
            var close = Btn("Close", (s, e) => Close());
            buttons.Children.Add(help); buttons.Children.Add(_rename); buttons.Children.Add(_reload); buttons.Children.Add(_save); buttons.Children.Add(close);
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Muted };
            bottom.Children.Add(_status);

            // body
            var body = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _lead = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(_lead);

            var differsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _adopt = Btn("Adopt server-wide", (s, e) => Adopt());
            DockPanel.SetDock(_adopt, Dock.Right);
            differsPanel.Children.Add(_adopt);
            _differs = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = InfoBrush, VerticalAlignment = VerticalAlignment.Center };
            differsPanel.Children.Add(_differs);
            body.Children.Add(differsPanel);

            var settings = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            settings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            settings.RowDefinitions.Add(new RowDefinition());
            settings.RowDefinitions.Add(new RowDefinition());
            AddCell(settings, new TextBlock { Text = "Prefix", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 0, 0);
            _prefix = new TextBox { FontFamily = Mono, Padding = new Thickness(3), Margin = new Thickness(0, 2, 0, 2) };
            _prefix.TextChanged += (s, e) => Changed();
            AddCell(settings, _prefix, 0, 1);
            _prefixSet = new CheckBox { Content = "Set the prefix here (unticked = inherit)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _prefixSet.Checked += (s, e) => { _prefix.IsEnabled = true; Changed(); };
            _prefixSet.Unchecked += (s, e) => { _prefix.IsEnabled = false; Changed(); };
            AddCell(settings, _prefixSet, 0, 2);
            AddCell(settings, new TextBlock { Text = "Parameter group", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }, 1, 0);
            _group = new ComboBox { Margin = new Thickness(0, 2, 0, 2) };
            _group.SelectionChanged += (s, e) => Changed();
            AddCell(settings, _group, 1, 1);
            body.Children.Add(settings);

            body.Children.Add(new TextBlock { Text = "Parameters (leave a name blank to inherit it)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
            _rows = new Grid();
            _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            body.Children.Add(_rows);

            Loaded += (s, e) => Load();
            Closing += (s, e) => { if (!RenameRequested && !ConfirmDiscard()) e.Cancel = true; };
        }

        // ── load / save ─────────────────────────────────────────────────────

        private string Url(string extra = "") => $"{_baseUrl}/api/source/{_sourceGuid}/parameter-naming/?layer={_layer}{extra}";

        private void Load()
        {
            SetBusy(true, "Loading the naming convention…");
            string url = Url();
            Task.Run(() =>
            {
                try
                {
                    string json = WebHelper.Get(url, AuthService.GetAuthTokenSafely(), null, AuthService.GetAuthSchemeSafely());
                    var payload = JObject.Parse(json);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { Apply(payload); SetBusy(false, "Loaded."); }
                        catch (Exception ex) { Log.Exception(ex); SetBusy(false, $"Could not show the convention: {ex.Message}", true); }
                    }));
                }
                catch (Exception ex)
                {
                    string msg = PairingSession.Describe(ex);
                    Log.Warn($"[ParameterNamingWindow] load failed: {msg}");
                    Dispatcher.BeginInvoke(new Action(() => SetBusy(false, msg.Contains("404") || msg.Contains("Not Found")
                        ? "This model isn't registered on the server yet, or the server is too old for parameter naming."
                        : $"Could not load the convention: {msg}", true)));
                }
            });
        }

        private void Save()
        {
            var body = BuildBody();
            string problem = LocalProblem(body);
            if (problem != null) { SetStatus(problem, true); return; }
            Send("Saving…", url => PairingSession.Put(_baseUrl, _sourceGuid, url, body.ToString(Formatting.None), Status), Url(),
                 "Saved. New parameters get these names; existing ones keep theirs until you use Rename parameters.");
        }

        private void Adopt()
        {
            if (MessageBox.Show(this, "Replace this project's convention with today's server-wide one?\n\nExisting parameters keep their names " +
                                      "until someone uses Rename parameters.", "Perseus — Parameter Naming",
                                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Send("Adopting…", url => PairingSession.Post(_baseUrl, _sourceGuid, url, "{}", Status), Url("&action=adopt"),
                 "The project now uses the server-wide convention.");
        }

        private void Send(string busyText, Func<string, string> request, string url, string okText)
        {
            SetBusy(true, busyText);
            Task.Run(() =>
            {
                try
                {
                    string response = request(url);
                    if (response == null) { Dispatcher.BeginInvoke(new Action(() => SetBusy(false, null))); return; }
                    var payload = JObject.Parse(response);
                    naming.ParameterNames.LoadNow(_sourceGuid, _baseUrl); // the plugin uses the effective convention
                    Log.Info($"[ParameterNamingWindow] {_layer} layer saved for {_sourceGuid}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Saved = true;
                        _dirty = false;
                        Apply(payload);
                        SetBusy(false, null);
                        SetStatus(okText, false, ok: true);
                    }));
                }
                catch (Exception ex)
                {
                    string msg = PairingSession.Describe(ex);
                    Log.Warn($"[ParameterNamingWindow] save failed: {msg}");
                    Dispatcher.BeginInvoke(new Action(() => { SetBusy(false, null); SetStatus("Not saved:\n" + msg, true); }));
                }
            });
        }

        private void Status(string text, bool isError) => Dispatcher.BeginInvoke(new Action(() => SetStatus(text, isError)));

        private JObject BuildBody()
        {
            var names = new JObject();
            foreach (var kv in _keyRows)
                if (!string.IsNullOrWhiteSpace(kv.Value.Box.Text)) names[kv.Key] = kv.Value.Box.Text.Trim();
            return new JObject
            {
                ["prefix"] = _prefixSet.IsChecked == true ? (JToken)_prefix.Text.Trim() : JValue.CreateNull(),
                ["parameter_group"] = (_group.SelectedItem as ComboBoxItem)?.Tag is string g && g.Length > 0 ? (JToken)g : JValue.CreateNull(),
                ["names"] = names,
            };
        }

        // Same checks as the server (perseus/parameter_naming.py) for a quick answer; the server has the last word.
        private static readonly char[] Forbidden = "\\:{}[]|;<>?`~".ToCharArray();

        private string LocalProblem(JObject body)
        {
            string prefix = (string)body["prefix"];
            if (prefix != null && prefix.IndexOfAny(Forbidden) >= 0) return "The prefix contains characters Revit doesn't allow (\\ : { } [ ] | ; < > ? ` ~).";
            foreach (var p in ((JObject)body["names"]).Properties())
            {
                string n = (string)p.Value;
                if (n.IndexOfAny(Forbidden) >= 0) return $"'{n}' contains characters Revit doesn't allow (\\ : {{ }} [ ] | ; < > ? ` ~).";
                if (n.Length > 100) return $"'{n}' is longer than 100 characters.";
            }
            var resolved = _keyRows.Select(kv => (kv.Key, Name: Resolved(kv.Key))).ToList();
            var dup = resolved.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return dup == null ? null : $"'{dup.Key}' would be used for {string.Join(" and ", dup.Select(d => d.Key))}.";
        }

        // ── showing a payload ───────────────────────────────────────────────

        private void Apply(JObject payload)
        {
            _payload = payload;
            _loading = true;
            try
            {
                _layer = (string)payload["layer"] ?? _layer;
                if (_layer == "source" && (payload["project"] == null || payload["project"].Type == JTokenType.Null))
                {
                    var projectItem = _scope.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == "project");
                    if (projectItem != null) _scope.Items.Remove(projectItem); // the model isn't in a project
                }
                var naming = payload["naming"] as JObject ?? new JObject();
                var inherited = payload["inherited"] as JObject ?? new JObject();

                _lead.Text = _layer == "server"
                    ? "The office default. New projects copy it when they are created; existing projects keep their own copy until they adopt it."
                    : _layer == "project"
                        ? "Copied from the server-wide convention when the project was created. Models in the project use it unless they override a value."
                        : "Overrides for this model only. Anything left blank comes from the project's convention.";

                var diffs = (payload["server_differs"] as JArray)?.OfType<JArray>().ToList() ?? new List<JArray>();
                bool showDiffs = _layer == "project" && diffs.Count > 0;
                _differs.Text = showDiffs
                    ? "Differs from today's server-wide convention: " + string.Join("; ", diffs.Take(4).Select(d => $"{d[0]}: {Val(d[1])} → {Val(d[2])}")) +
                      (diffs.Count > 4 ? $"; +{diffs.Count - 4} more" : "")
                    : "";
                ((FrameworkElement)_differs.Parent).Visibility = showDiffs ? Visibility.Visible : Visibility.Collapsed;

                bool prefixSet = naming["prefix"] != null && naming["prefix"].Type != JTokenType.Null;
                _prefixSet.IsChecked = prefixSet;
                _prefix.Text = prefixSet ? (string)naming["prefix"] : "";
                _prefix.IsEnabled = prefixSet;
                _prefix.ToolTip = $"Inherited: {Val(inherited["prefix"])}";

                _group.Items.Clear();
                var groups = (payload["parameter_groups"] as JArray)?.OfType<JArray>().Select(a => ((string)a[0], (string)a[1])).ToList()
                             ?? new List<(string, string)> { ("other", "Other") };
                string inhGroup = (string)inherited["parameter_group"] ?? "other";
                _group.Items.Add(new ComboBoxItem { Content = $"Inherit ({groups.FirstOrDefault(g => g.Item1 == inhGroup).Item2 ?? inhGroup})", Tag = "" });
                foreach (var g in groups) _group.Items.Add(new ComboBoxItem { Content = g.Item2, Tag = g.Item1 });
                string own = (string)naming["parameter_group"];
                _group.SelectedItem = _group.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (own ?? "")) ?? _group.Items[0];

                BuildRows(payload["keys"] as JArray, naming["names"] as JObject ?? new JObject(), inherited);
            }
            finally { _loading = false; }
            Preview();
        }

        private void BuildRows(JArray keys, JObject own, JObject inherited)
        {
            _rows.Children.Clear();
            _rows.RowDefinitions.Clear();
            _keyRows.Clear();
            var inhNames = inherited["names"] as JObject ?? new JObject();
            int r = 0;
            AddHeader("Parameter", 0); AddHeader("Name (before prefix)", 1); AddHeader("Name in Revit", 2);
            foreach (var k in (keys ?? new JArray()).OfType<JObject>())
            {
                string key = (string)k["key"], label = (string)k["label"], def = (string)inhNames[key] ?? (string)k["default"];
                r++;
                _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var labelBlock = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                                                 Margin = new Thickness(0, 3, 10, 3), ToolTip = key };
                var box = new TextBox { Text = (string)own[key] ?? "", FontFamily = Mono, Padding = new Thickness(3), Margin = new Thickness(0, 3, 10, 3),
                                        ToolTip = $"Blank = inherit '{def}'" };
                box.TextChanged += (s, e) => Changed();
                var result = new TextBlock { FontFamily = Mono, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                             TextTrimming = TextTrimming.CharacterEllipsis };
                AddCell(_rows, labelBlock, r, 0); AddCell(_rows, box, r, 1); AddCell(_rows, result, r, 2);
                _keyRows[key] = (box, result, def);
            }

            void AddHeader(string text, int col)
            {
                if (_rows.RowDefinitions.Count == 0) _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AddCell(_rows, new TextBlock { Text = text, Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) }, 0, col);
            }
        }

        private string EffectivePrefix()
        {
            if (_prefixSet.IsChecked == true) return _prefix.Text.Trim();
            return (string)(_payload?["inherited"] as JObject)?["prefix"] ?? "";
        }

        private string Resolved(string key)
        {
            var row = _keyRows[key];
            string baseName = string.IsNullOrWhiteSpace(row.Box.Text) ? row.Default : row.Box.Text.Trim();
            string prefix = EffectivePrefix();
            return string.IsNullOrEmpty(prefix) || baseName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? baseName : prefix + baseName;
        }

        private void Preview()
        {
            var from = (_payload?["inherited"] as JObject)?["from"] as JObject;
            foreach (var kv in _keyRows)
            {
                bool own = !string.IsNullOrWhiteSpace(kv.Value.Box.Text);
                string origin = own ? ScopeLabel[_layer] : ScopeLabel.TryGetValue((string)from?[kv.Key] ?? "default", out var o) ? o : "built-in";
                kv.Value.Result.Text = Resolved(kv.Key);
                kv.Value.Result.ToolTip = $"Name from: {origin}";
            }
        }

        private void Changed()
        {
            if (_loading) return;
            _dirty = true;
            Preview();
        }

        private void OnScopeChanged()
        {
            string want = (_scope.SelectedItem as ComboBoxItem)?.Tag as string ?? "source";
            if (want == _layer) return;
            if (!ConfirmDiscard())
            {
                _scope.SelectedItem = _scope.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == _layer);
                return;
            }
            _layer = want;
            _dirty = false;
            Load();
        }

        private void RequestRename()
        {
            if (!ConfirmDiscard()) return;
            RenameRequested = true;
            Close();
        }

        private bool ConfirmDiscard() =>
            !_dirty || MessageBox.Show(this, "Discard your unsaved changes?", "Perseus — Parameter Naming",
                                       MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        // ── helpers ─────────────────────────────────────────────────────────

        private void SetBusy(bool busy, string text, bool error = false)
        {
            _save.IsEnabled = _reload.IsEnabled = _scope.IsEnabled = _adopt.IsEnabled = !busy;
            if (text != null) SetStatus(text, error);
        }

        private void SetStatus(string text, bool error, bool ok = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? ErrorBrush : ok ? OkBrush : Muted;
        }

        private static string Val(JToken t) => t == null || t.Type == JTokenType.Null || (string)t == "" ? "(none)" : (string)t;

        private void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus($"Could not open the browser: {ex.Message}", true); }
        }

        private static void AddCell(Grid grid, UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            grid.Children.Add(el);
        }

        private static Button Btn(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, MinWidth = 90, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
            if (click != null) b.Click += click;
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
