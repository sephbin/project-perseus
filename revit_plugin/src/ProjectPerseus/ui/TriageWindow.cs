using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ProjectPerseus.federator;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Bulk review ("triage") for ui/FederatorWindow: changes grouped by pattern (federator/ChangeTriage - same category,
    /// type, kind, aspects, changed parameters, move bucket), largest first, with the most common before → after values.
    /// Tick groups (or select them) and Accept / Ignore / Query + assign / hide from preview them in one go, or turn
    /// their parameters / small moves into the link's noise rules. Everything goes through the panel's own review
    /// writes (batch events) and noise rules; the window refreshes on the panel's ReviewChanged.
    /// </summary>
    internal class TriageWindow : Window
    {
        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);
        private static readonly Brush Accent = Frozen(0x00, 0x7C, 0xB8);

        private readonly FederatorWindow _owner;
        private readonly CheckBox _byType, _split, _includeReviewed;
        private readonly TextBlock _summary, _scope;
        private readonly DataGrid _grid;
        private readonly StackPanel _detail, _noisePanel;
        private readonly ComboBox _assignee, _actionType;
        private readonly TextBox _moveMm;
        private List<GroupRow> _groups = new List<GroupRow>();
        private readonly HashSet<string> _checkedKeys = new HashSet<string>();

        private class GroupRow : INotifyPropertyChanged
        {
            private readonly TriageWindow _w;
            internal GroupRow(TriageWindow w, TriageGroup g) { _w = w; G = g; }
            internal TriageGroup G { get; }
            public bool Checked
            {
                get => _w._checkedKeys.Contains(G.Key);
                set
                {
                    if (value) _w._checkedKeys.Add(G.Key); else _w._checkedKeys.Remove(G.Key);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                    _w.UpdateScope();
                }
            }
            public int Count => G.Changes.Count;
            public string Kind => G.Kind == ChangeKind.Added ? "+" : G.Kind == ChangeKind.Deleted ? "−" : "~";
            public Brush KindBrush => ChangeRow.KindBrushOf(G.Kind);
            public string Category => G.Category;
            public string Type => G.Type;
            public string Change => G.Description;
            public event PropertyChangedEventHandler PropertyChanged;
        }

        internal TriageWindow(FederatorWindow owner)
        {
            _owner = owner;
            Title = "Perseus — Triage changes";
            Width = 1180; Height = 720; MinWidth = 860; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Ink;
            Background = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(12) };

            // ── Options ──
            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _byType = Check("Group by type", true, "Off: group by category only");
            _split = Check("Split by values", false, "Also split groups whose before → after values differ");
            _includeReviewed = Check("Include reviewed", false, "Off: only changes still Unreviewed");
            top.Children.Add(_byType); top.Children.Add(_split); top.Children.Add(_includeReviewed);
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            top.Children.Add(_summary);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // ── Bulk action bar ──
            var bar = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            _scope = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(_scope, Dock.Left);
            bar.Children.Add(_scope);
            var actions = new WrapPanel();
            var accept = Btn("Accept", "Accept every change in the ticked groups");
            var ignore = Btn("Ignore", "Ignore every change in the ticked groups");
            var resolve = Btn("Resolved", "Mark every change in the ticked groups resolved");
            _actionType = new ComboBox { IsEditable = true, Width = 105, Margin = new Thickness(10, 0, 4, 0), ToolTip = "Action type" };
            foreach (var t in owner.KnownActionTypes) _actionType.Items.Add(t);
            _actionType.Text = ReviewAction.DefaultTypes[0];
            _assignee = new ComboBox { IsEditable = true, Width = 140, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Assign to (person or discipline)" };
            foreach (var p in owner.KnownPeople) _assignee.Items.Add(p);
            var assign = Btn("Assign + Query", "Set the ticked groups to Query and assign the action");
            var hide = Btn("Hide from preview", "Don't draw / bake the ticked groups");
            var show = Btn("Preview", "Draw the ticked groups in the view again");
            var noise = Btn("Mark as noise…", "Treat the changed parameters of the ticked groups as noise for this link (saved in the model for everyone)");
            foreach (var b in new UIElement[] { accept, ignore, resolve, _actionType, _assignee, assign, hide, show, noise }) actions.Children.Add(b);
            bar.Children.Add(actions);
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);

            // ── Body: groups | detail ──
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            _grid = BuildGrid();
            Grid.SetColumn(_grid, 0);
            body.Children.Add(_grid);
            var right = new StackPanel();
            _detail = new StackPanel();
            right.Children.Add(_detail);
            right.Children.Add(Section("Noise rules for this link"));
            _noisePanel = new StackPanel();
            right.Children.Add(_noisePanel);
            var moveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            moveRow.Children.Add(new TextBlock { Text = "Moves under", VerticalAlignment = VerticalAlignment.Center });
            _moveMm = new TextBox { Width = 50, Margin = new Thickness(6, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            moveRow.Children.Add(_moveMm);
            moveRow.Children.Add(new TextBlock { Text = "mm are noise", VerticalAlignment = VerticalAlignment.Center });
            var applyMove = Btn("Apply", "Save the move threshold (0 = off)");
            applyMove.Margin = new Thickness(8, 0, 0, 0);
            moveRow.Children.Add(applyMove);
            right.Children.Add(moveRow);
            var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(scroll, 2);
            body.Children.Add(scroll);
            root.Children.Add(body);
            Content = root;

            // ── Wiring ──
            foreach (var c in new[] { _byType, _split, _includeReviewed })
            {
                c.Checked += (s, e) => Safe(Refresh);
                c.Unchecked += (s, e) => Safe(Refresh);
            }
            _grid.SelectionChanged += (s, e) => Safe(() => { BuildDetail(); UpdateScope(); });
            accept.Click += (s, e) => Safe(() => Bulk(rows => _owner.TriageStatus(rows, ReviewStatus.Accepted)));
            ignore.Click += (s, e) => Safe(() => Bulk(rows => _owner.TriageStatus(rows, ReviewStatus.Ignored)));
            resolve.Click += (s, e) => Safe(() => Bulk(rows => _owner.TriageStatus(rows, ReviewStatus.Resolved)));
            assign.Click += (s, e) => Safe(() =>
            {
                string who = _assignee.Text?.Trim(), type = _actionType.Text?.Trim();
                if (string.IsNullOrEmpty(who)) { MessageBox.Show(this, "Type or pick who to assign it to.", "Triage"); return; }
                Bulk(rows => { _owner.TriageStatus(rows, ReviewStatus.Query); _owner.TriageAssign(rows, type, who); });
            });
            hide.Click += (s, e) => Safe(() => Bulk(rows => _owner.TriagePreview(rows, false)));
            show.Click += (s, e) => Safe(() => Bulk(rows => _owner.TriagePreview(rows, true)));
            noise.Click += (s, e) => Safe(MarkNoise);
            applyMove.Click += (s, e) => Safe(() =>
            {
                if (!double.TryParse(_moveMm.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double mm) || mm < 0)
                { MessageBox.Show(this, "Enter a distance in mm (0 turns it off).", "Triage"); return; }
                _owner.TriageAddNoise(null, mm);
            });
            _owner.ReviewChanged += OnOwnerChanged;
            Closed += (s, e) => _owner.ReviewChanged -= OnOwnerChanged;

            Refresh();
        }

        private void OnOwnerChanged() => Safe(Refresh);

        // ── Data ──

        private void Refresh()
        {
            var keepSelected = new HashSet<string>(_grid.SelectedItems.Cast<GroupRow>().Select(g => g.G.Key));
            var source = _owner.TriageSource(_includeReviewed.IsChecked == true);
            var groups = ChangeTriage.Group(source.Select(r => r.Change), new TriageOptions
            {
                ByType = _byType.IsChecked == true,
                SplitByValues = _split.IsChecked == true,
            });
            _groups = groups.Select(g => new GroupRow(this, g)).ToList();
            _checkedKeys.IntersectWith(_groups.Select(g => g.G.Key));
            _grid.ItemsSource = _groups;
            foreach (var g in _groups.Where(g => keepSelected.Contains(g.G.Key))) _grid.SelectedItems.Add(g);
            _summary.Text = $"{_groups.Count:N0} groups · {source.Count:N0} changes" +
                            (_includeReviewed.IsChecked == true ? "" : " still unreviewed") + " (noise excluded)";
            _moveMm.Text = _owner.Noise.MinMoveMm > 0 ? _owner.Noise.MinMoveMm.ToString("0.#", CultureInfo.CurrentCulture) : "0";
            BuildNoise();
            BuildDetail();
            UpdateScope();
        }

        /// <summary>Ticked groups, or the selected ones when nothing is ticked.</summary>
        private List<GroupRow> Targets()
        {
            var ticked = _groups.Where(g => g.Checked).ToList();
            return ticked.Count > 0 ? ticked : _grid.SelectedItems.Cast<GroupRow>().ToList();
        }

        private void UpdateScope()
        {
            var t = Targets();
            int n = t.Sum(g => g.Count);
            bool ticked = _groups.Any(g => g.Checked);
            _scope.Text = t.Count == 0 ? "Tick or select groups" : $"{(ticked ? "Ticked" : "Selected")}: {t.Count:N0} group(s) · {n:N0} change(s)";
        }

        private void Bulk(Action<List<ChangeRow>> act)
        {
            var targets = Targets();
            if (targets.Count == 0) { MessageBox.Show(this, "Tick or select one or more groups first.", "Triage"); return; }
            var rows = _owner.RowsFor(targets.SelectMany(g => g.G.Changes));
            if (rows.Count > 500 &&
                MessageBox.Show(this, $"Apply to {rows.Count:N0} changes?", "Triage", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _checkedKeys.Clear();
            act(rows);
        }

        private void MarkNoise()
        {
            var targets = Targets();
            var parms = targets.SelectMany(g => g.G.Parameters).Distinct().OrderBy(p => p).ToList();
            if (parms.Count == 0) { MessageBox.Show(this, "The chosen groups have no parameter changes. For small moves use the 'Moves under … mm' rule.", "Triage"); return; }
            string list = string.Join("\n  • ", parms.Take(25)) + (parms.Count > 25 ? $"\n  … and {parms.Count - 25} more" : "");
            if (MessageBox.Show(this,
                    $"Treat these parameters as noise for this link?\n\n  • {list}\n\nChanges made up only of noise are hidden (not deleted) for everyone. " +
                    "You can undo this from 'Noise rules' at any time.", "Triage", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _checkedKeys.Clear();
            _owner.TriageAddNoise(parms, null);
        }

        // ── Detail ──

        private void BuildDetail()
        {
            _detail.Children.Clear();
            var sel = _grid.SelectedItems.Cast<GroupRow>().ToList();
            if (sel.Count != 1)
            {
                _detail.Children.Add(Heading(sel.Count == 0 ? "Pick a group" : $"{sel.Count} groups selected"));
                _detail.Children.Add(MutedText("Groups are changes that look alike: same category and type, same kind of change and the same " +
                                               "parameters. Tick several and use the buttons below to review them in one go."));
                return;
            }
            var g = sel[0].G;
            _detail.Children.Add(Heading($"{g.Changes.Count:N0} × {(string.IsNullOrEmpty(g.Type) ? g.Category : g.Type)}"));
            _detail.Children.Add(MutedText($"{g.Category} · {g.Kind} · {g.Description}"));

            if (g.Transitions.Count > 0)
            {
                _detail.Children.Add(Section("Most common values"));
                var table = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                int row = 0;
                foreach (var byParam in g.Transitions.GroupBy(t => t.Parameter))
                {
                    bool first = true;
                    foreach (var t in byParam)
                    {
                        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        if (first)
                        {
                            string name = t.Parameter.StartsWith(LinkDiff.TypeParameterPrefix, StringComparison.Ordinal)
                                ? "Type: " + t.Parameter.Substring(LinkDiff.TypeParameterPrefix.Length) : t.Parameter;
                            var nameBlock = new StackPanel();
                            nameBlock.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                            if (t.Distinct > byParam.Count()) nameBlock.Children.Add(MutedText($"{t.Distinct} different changes"));
                            var noiseLink = Link("mark as noise", $"Treat '{name}' as noise for this link");
                            string p = t.Parameter;
                            noiseLink.Click += (s, e) => Safe(() =>
                            {
                                if (MessageBox.Show(this, $"Treat '{name}' as noise for this link (for everyone)?", "Triage",
                                                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                                    _owner.TriageAddNoise(new[] { p }, null);
                            });
                            nameBlock.Children.Add(noiseLink);
                            Place(table, nameBlock, row, 0);
                            first = false;
                        }
                        Place(table, new TextBlock { Text = $"{Show(t.Old)}  →  {Show(t.New)}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 2) }, row, 1);
                        Place(table, new TextBlock { Text = t.Count.ToString("N0"), Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Right }, row, 2);
                        row++;
                    }
                }
                _detail.Children.Add(table);
            }

            var rows = _owner.RowsFor(g.Changes);
            _detail.Children.Add(Section("Review status"));
            _detail.Children.Add(MutedText(string.Join("   ", rows.GroupBy(r => r.State.Status).OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count():N0}"))));

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var list = Btn("Show in the list", "Narrow the Federator list to this group");
            var select = Btn("Select in Revit", "Select this group's linked elements and zoom to them");
            string label = $"{g.Changes.Count:N0} × {(string.IsNullOrEmpty(g.Type) ? g.Category : g.Type)} — {g.Description}";
            list.Click += (s, e) => Safe(() => _owner.TriageFocus(rows, label));
            select.Click += (s, e) => Safe(() => _owner.TriageShow(rows, true));
            buttons.Children.Add(list);
            buttons.Children.Add(select);
            _detail.Children.Add(buttons);
        }

        private void BuildNoise()
        {
            _noisePanel.Children.Clear();
            var n = _owner.Noise;
            if (n.Parameters.Count == 0) _noisePanel.Children.Add(MutedText("No parameters are treated as noise yet."));
            foreach (var p in n.Parameters)
            {
                var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var remove = Link("remove", $"'{p}' changes count again");
                string param = p;
                remove.Click += (s, e) => Safe(() => _owner.TriageRemoveNoise(param));
                DockPanel.SetDock(remove, Dock.Right);
                line.Children.Add(remove);
                line.Children.Add(new TextBlock { Text = p, TextTrimming = TextTrimming.CharacterEllipsis });
                _noisePanel.Children.Add(line);
            }
        }

        // ── Grid ──

        private DataGrid BuildGrid()
        {
            var g = new DataGrid
            {
                AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false, CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = LineBrush, BorderBrush = LineBrush, Background = Brushes.White, RowHeight = 24,
            };
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                          new Binding(nameof(GroupRow.Checked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            cb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            g.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(30), CellTemplate = new DataTemplate { VisualTree = cb } });

            var kind = new FrameworkElementFactory(typeof(TextBlock));
            kind.SetBinding(TextBlock.TextProperty, new Binding(nameof(GroupRow.Kind)));
            kind.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(GroupRow.KindBrush)));
            kind.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            kind.SetValue(TextBlock.FontSizeProperty, 14.0);
            kind.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            kind.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            g.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(24), CellTemplate = new DataTemplate { VisualTree = kind }, IsReadOnly = true });

            g.Columns.Add(Text("Count", nameof(GroupRow.Count), new DataGridLength(60)));
            g.Columns.Add(Text("Category", nameof(GroupRow.Category), new DataGridLength(120)));
            g.Columns.Add(Text("Type", nameof(GroupRow.Type), new DataGridLength(1, DataGridLengthUnitType.Star)));
            g.Columns.Add(Text("Change", nameof(GroupRow.Change), new DataGridLength(1.6, DataGridLengthUnitType.Star)));
            return g;
        }

        private static DataGridTextColumn Text(string header, string path, DataGridLength width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 0)));
            return new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width, IsReadOnly = true, ElementStyle = style };
        }

        // ── Helpers ──

        private void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Warn($"[Federator] triage: {ex}"); }
        }

        private static string Show(string v) => v == null ? "(none)" : v.Length == 0 ? "(empty)" : v;

        private static void Place(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e);
        }

        private static CheckBox Check(string text, bool on, string tip) =>
            new CheckBox { Content = text, IsChecked = on, ToolTip = tip, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };

        private static Button Btn(string text, string tip) =>
            new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4),
                         Background = Brushes.White, BorderBrush = LineBrush };

        private static Button Link(string text, string tip)
        {
            var b = new Button { Content = new TextBlock { Text = text, Foreground = Accent, TextDecorations = TextDecorations.Underline, FontSize = 11 },
                                 ToolTip = tip, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            return b;
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 14, 0, 4) };

        private static TextBlock MutedText(string text) =>
            new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
