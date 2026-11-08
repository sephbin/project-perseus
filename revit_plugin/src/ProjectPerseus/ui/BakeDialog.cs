using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modal "Bake changes to views" dialog for ui/FederatorWindow. Left: candidate views; target = regex matches (on
    /// view name or sheet number) plus ticked, minus unticked (federator/ViewTargets), so views added later that match
    /// the regex are picked up next time. Right: which changes, outline vs revision cloud, labels, replace previous.
    /// Edits the BakeSettings passed in; Action tells the caller whether to bake or remove.
    /// </summary>
    internal class BakeDialog : Window
    {
        internal enum Choice { None, Bake, Remove }

        internal Choice Action { get; private set; } = Choice.None;
        internal List<string> TargetUids => _rows.Where(r => r.IsTarget).Select(r => r.View.Uid).ToList();

        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush ErrorBrush = Frozen(0xC0, 0x26, 0x26);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);

        private readonly BakeSettings _s;
        private readonly List<ViewRow> _rows;
        private Regex _rx;
        private readonly TextBox _regex;
        private readonly TextBlock _regexError, _targetCount;
        private readonly ComboBox _match, _show;
        private readonly ListCollectionView _view;

        private class ViewRow : INotifyPropertyChanged
        {
            private readonly BakeDialog _owner;
            internal ViewRow(BakeDialog owner, ViewCandidate v) { _owner = owner; View = v; }
            internal ViewCandidate View { get; }
            public string Name => View.Name;
            public string Type => View.ViewType;
            public string Sheet => View.SheetNumber;
            public string Scale => View.Scale > 0 ? "1:" + View.Scale : "";
            public bool IsTarget
            {
                get => ViewTargets.IsTarget(View, _owner._s, _owner._rx);
                set { ViewTargets.Toggle(View, _owner._s, _owner._rx, value); Changed(); _owner.UpdateCount(); }
            }
            public string Why
            {
                get
                {
                    switch (ViewTargets.ReasonFor(View, _owner._s, _owner._rx))
                    {
                        case ViewTargets.Reason.Regex: return "matches";
                        case ViewTargets.Reason.Manual: return "ticked";
                        case ViewTargets.Reason.Excluded: return "unticked";
                        default: return "";
                    }
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
            internal void Changed()
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTarget)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Why)));
            }
        }

        internal BakeDialog(string linkTitle, List<ViewCandidate> views, List<KeyValuePair<string, string>> revisions, BakeSettings settings,
                            int shown, int selected, int all)
        {
            _s = settings;
            _rx = ViewTargets.Build(_s.ViewRegex, out _);
            _rows = views.Select(v => new ViewRow(this, v)).ToList();

            Title = "Bake changes to views — " + linkTitle;
            Width = 980; Height = 640; MinWidth = 760; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Background = Brushes.White;

            var root = new Grid { Margin = new Thickness(12) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Left: views ──
            var left = new DockPanel();
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(new TextBlock { Text = "Views that receive the annotations", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var rxRow = new DockPanel();
            _match = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 6, 0) };
            _match.Items.Add("View name");
            _match.Items.Add("Sheet number");
            _match.SelectedIndex = _s.RegexTarget == BakeSettings.MatchSheetNumber ? 1 : 0;
            DockPanel.SetDock(_match, Dock.Left);
            rxRow.Children.Add(_match);
            _show = new ComboBox { Width = 130, Margin = new Thickness(6, 0, 0, 0) };
            _show.Items.Add("All views");
            _show.Items.Add("Receiving only");
            _show.SelectedIndex = 0;
            DockPanel.SetDock(_show, Dock.Right);
            rxRow.Children.Add(_show);
            _regex = new TextBox { Text = _s.ViewRegex ?? "", VerticalContentAlignment = VerticalAlignment.Center,
                                   ToolTip = "Regular expression, case-insensitive. e.g.  ^L\\d+ - Coordination  or  ^A-1\\d\\d" };
            rxRow.Children.Add(_regex);
            top.Children.Add(rxRow);
            _regexError = new TextBlock { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap };
            top.Children.Add(_regexError);
            top.Children.Add(new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = "Views matching the pattern are ticked automatically (including views added later). Tick or untick any view to override." });
            DockPanel.SetDock(top, Dock.Top);
            left.Children.Add(top);

            _targetCount = new TextBlock { Foreground = Muted, Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(_targetCount, Dock.Bottom);
            left.Children.Add(_targetCount);

            _view = new ListCollectionView(_rows);
            var grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = LineBrush, BorderBrush = LineBrush,
                RowHeight = 22, ItemsSource = _view, SelectionMode = DataGridSelectionMode.Extended,
            };
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                             new Binding(nameof(ViewRow.IsTarget)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(30), CellTemplate = new DataTemplate { VisualTree = check } });
            grid.Columns.Add(new DataGridTextColumn { Header = "View", Binding = new Binding(nameof(ViewRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(ViewRow.Type)), Width = new DataGridLength(90), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Sheet", Binding = new Binding(nameof(ViewRow.Sheet)), Width = new DataGridLength(70), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "Scale", Binding = new Binding(nameof(ViewRow.Scale)), Width = new DataGridLength(55), IsReadOnly = true });
            grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(ViewRow.Why)), Width = new DataGridLength(70), IsReadOnly = true });
            left.Children.Add(grid);
            Grid.SetColumn(left, 0);
            root.Children.Add(left);

            // ── Right: options ──
            var right = new StackPanel();
            right.Children.Add(Section("Changes"));
            var scopeShown = Radio("scope", $"Shown in the list and previewed ({shown:N0})", _s.Scope == BakeSettings.ScopeShown);
            var scopeSel = Radio("scope", $"Selected ({selected:N0})", _s.Scope == BakeSettings.ScopeSelected);
            var scopeAll = Radio("scope", $"All in this comparison ({all:N0})", _s.Scope == BakeSettings.ScopeAll);
            scopeSel.IsEnabled = selected > 0;
            right.Children.Add(scopeShown); right.Children.Add(scopeSel); right.Children.Add(scopeAll);
            right.Children.Add(Note("Only the changes visible in each view are drawn in it."));

            right.Children.Add(Section("Draw as"));
            var outline = Radio("style", "Coloured outlines (green added, amber modified, red deleted)", _s.Style != BakeSettings.StyleRevisionCloud);
            var cloud = Radio("style", "Revision clouds on revision:", _s.Style == BakeSettings.StyleRevisionCloud);
            var revBox = new ComboBox { Margin = new Thickness(20, 2, 0, 0), DisplayMemberPath = "Value" };
            foreach (var r in revisions) revBox.Items.Add(r);
            revBox.SelectedItem = revisions.FirstOrDefault(r => r.Key == _s.RevisionUid);
            if (revBox.SelectedItem == null && revisions.Count > 0) revBox.SelectedIndex = revisions.Count - 1;
            if (revisions.Count == 0) { cloud.IsEnabled = false; cloud.IsChecked = false; outline.IsChecked = true; cloud.Content = "Revision clouds (no open revisions in this model)"; }
            revBox.IsEnabled = cloud.IsChecked == true;
            cloud.Checked += (s, e) => revBox.IsEnabled = true;
            outline.Checked += (s, e) => revBox.IsEnabled = false;
            right.Children.Add(outline); right.Children.Add(cloud); right.Children.Add(revBox);

            right.Children.Add(Section("Labels"));
            var labels = new CheckBox { Content = "Add a text label to each change", IsChecked = _s.Labels, Margin = new Thickness(0, 2, 0, 2) };
            var labelStatus = new CheckBox { Content = "Include review status and assignees", IsChecked = _s.LabelStatus, Margin = new Thickness(20, 2, 0, 2) };
            labelStatus.IsEnabled = labels.IsChecked == true;
            labels.Checked += (s, e) => labelStatus.IsEnabled = true;
            labels.Unchecked += (s, e) => labelStatus.IsEnabled = false;
            right.Children.Add(labels); right.Children.Add(labelStatus);

            right.Children.Add(Section("Earlier annotations"));
            var replace = new CheckBox { Content = "Replace this link's earlier baked annotations in these views", IsChecked = _s.ReplacePrevious };
            right.Children.Add(replace);
            right.Children.Add(Note("Annotations are ordinary detail lines / clouds / text: they print, and can be moved or restyled " +
                                    "(object styles \"Perseus Federator - …\")."));
            Grid.SetColumn(right, 2);
            root.Children.Add(right);

            // ── Buttons ──
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var bake = new Button { Content = "Bake", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var remove = new Button { Content = "Remove baked from these views", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
            buttons.Children.Add(bake); buttons.Children.Add(remove); buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 1);
            Grid.SetColumnSpan(buttons, 3);
            root.Children.Add(buttons);
            Content = root;

            // ── Wiring ──
            void Collect()
            {
                _s.ViewRegex = _regex.Text ?? "";
                _s.RegexTarget = _match.SelectedIndex == 1 ? BakeSettings.MatchSheetNumber : BakeSettings.MatchViewName;
                _s.Scope = scopeSel.IsChecked == true ? BakeSettings.ScopeSelected : scopeAll.IsChecked == true ? BakeSettings.ScopeAll : BakeSettings.ScopeShown;
                _s.Style = cloud.IsChecked == true ? BakeSettings.StyleRevisionCloud : BakeSettings.StyleOutline;
                _s.RevisionUid = (revBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? _s.RevisionUid;
                _s.Labels = labels.IsChecked == true;
                _s.LabelStatus = labelStatus.IsChecked == true;
                _s.ReplacePrevious = replace.IsChecked == true;
            }
            _regex.TextChanged += (s, e) => RegexChanged();
            _match.SelectionChanged += (s, e) => { _s.RegexTarget = _match.SelectedIndex == 1 ? BakeSettings.MatchSheetNumber : BakeSettings.MatchViewName; RefreshRows(); };
            _show.SelectionChanged += (s, e) => ApplyShow();
            bake.Click += (s, e) =>
            {
                Collect();
                if (TargetUids.Count == 0) { MessageBox.Show(this, "Tick at least one view, or enter a pattern that matches some.", "Federator"); return; }
                Action = Choice.Bake; DialogResult = true;
            };
            remove.Click += (s, e) =>
            {
                Collect();
                if (TargetUids.Count == 0) { MessageBox.Show(this, "Tick the views to remove annotations from.", "Federator"); return; }
                Action = Choice.Remove; DialogResult = true;
            };
            UpdateCount();
        }

        private void RegexChanged()
        {
            _s.ViewRegex = _regex.Text ?? "";
            _rx = ViewTargets.Build(_s.ViewRegex, out string error);
            _regexError.Text = error == null ? "" : "Invalid pattern: " + error;
            RefreshRows();
        }

        private void RefreshRows()
        {
            foreach (var r in _rows) r.Changed();
            ApplyShow();
            UpdateCount();
        }

        private void ApplyShow()
        {
            if (_show.SelectedIndex == 1) _view.Filter = o => ((ViewRow)o).IsTarget;
            else _view.Filter = null;
        }

        private void UpdateCount()
        {
            int n = _rows.Count(r => r.IsTarget);
            _targetCount.Text = $"{n:N0} of {_rows.Count:N0} views will receive annotations.";
        }

        private static TextBlock Section(string text) =>
            new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Muted, Margin = new Thickness(0, 12, 0, 4) };

        private static TextBlock Note(string text) =>
            new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        private static RadioButton Radio(string group, string text, bool on) =>
            new RadioButton { GroupName = group, Content = text, IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
