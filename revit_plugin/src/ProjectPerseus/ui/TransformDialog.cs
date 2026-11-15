using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectPerseus.mapping;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modal editor of a parameter mapping's transform (models/ParamTransform, mapping/ParamTransforms): constant, default for empty,
    /// lookup table (fill from the consultant values), expression, prefix / suffix, with a live preview on the consultant values.
    /// </summary>
    internal class TransformDialog : Window
    {
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x2F, 0x3D, 0x57));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x00));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC0, 0x26, 0x26));

        private readonly ParamRef _src, _dst;
        private readonly IList<SourceValue> _values, _aligned;
        private readonly IList<Dictionary<string, string>> _rows2;
        private readonly CheckBox _useConstant, _strict;
        private readonly TextBox _constant, _default, _expression, _prefix, _suffix;
        private readonly ObservableCollection<LookupEntry> _rows;
        private readonly DataGrid _table;
        private readonly TextBlock _preview;
        private readonly StackPanel _sourceSteps;
        private readonly DispatcherTimer _timer;

        /// <summary>The edited transform (null = copied as it is).</summary>
        internal ParamTransform Result { get; private set; }

        /// <param name="values">Preview values per element / type; aligned + rows = one per consultant element (expressions reading row["..."]).</param>
        internal TransformDialog(ParamRef src, ParamRef dst, ParamTransform current, IList<SourceValue> values,
                                 IList<SourceValue> aligned, IList<Dictionary<string, string>> rows, IList<string> rowNames)
        {
            _src = src; _dst = dst; _values = values ?? new List<SourceValue>();
            _aligned = aligned ?? new List<SourceValue>(); _rows2 = rows ?? new List<Dictionary<string, string>>();
            var t = current ?? new ParamTransform();
            Title = "Perseus — Transform value";
            Width = 560; Height = 720; MinHeight = 480; ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI"); FontSize = 12;

            var root = new DockPanel { Margin = new Thickness(14) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var clear = new Button { Content = "Copy as it is", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0),
                                     ToolTip = "Remove every step: the consultant value is copied unchanged", IsEnabled = src != null };
            var ok = new Button { Content = "OK", Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), IsCancel = true };
            buttons.Children.Add(clear); buttons.Children.Add(ok); buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            _preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 11.5 };
            var previewBox = new ScrollViewer { Content = _preview, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DockPanel.SetDock(previewBox, Dock.Bottom);
            root.Children.Add(previewBox);

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8),
                Text = $"{(src != null ? $"Consultant '{src.Name}'" : "A constant")} → local '{dst?.Name}'. Steps run top to bottom; a step that changes the value " +
                       "makes it text, which is then written like text (numbers in it can still fill a number or a length, in project units).",
            });

            _useConstant = new CheckBox { Content = "Constant: write this value instead of the consultant's", FontWeight = FontWeights.SemiBold, IsChecked = t.Constant != null || (src == null && string.IsNullOrWhiteSpace(t.Expression)) };
            body.Children.Add(_useConstant);
            _constant = Box(t.Constant ?? "");
            body.Children.Add(_constant);

            _sourceSteps = new StackPanel();
            _sourceSteps.Children.Add(Label("1. Default for an empty consultant value"));
            _default = Box(t.Default ?? "");
            _sourceSteps.Children.Add(_default);

            _sourceSteps.Children.Add(Label("2. Lookup table (consultant value → local value; trimmed, not case-sensitive)"));
            _rows = new ObservableCollection<LookupEntry>((t.Lookup ?? new List<LookupEntry>()).Select(e => new LookupEntry { From = e.From, To = e.To }));
            _table = new DataGrid
            {
                ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, HeadersVisibility = DataGridHeadersVisibility.Column,
                Height = 150, RowHeaderWidth = 0, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, Margin = new Thickness(0, 2, 0, 4),
            };
            _table.Columns.Add(new DataGridTextColumn { Header = "Consultant value", Binding = new System.Windows.Data.Binding(nameof(LookupEntry.From)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _table.Columns.Add(new DataGridTextColumn { Header = "Local value", Binding = new System.Windows.Data.Binding(nameof(LookupEntry.To)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _table.CellEditEnding += (s, e) => Queue();
            _table.RowEditEnding += (s, e) => Queue();
            _rows.CollectionChanged += (s, e) => Queue();
            _sourceSteps.Children.Add(_table);
            var tableRow = new WrapPanel();
            var fill = new Button { Content = "Add the consultant values", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 8, 0),
                                    ToolTip = "Add a row for every distinct consultant value not in the table yet (fill in the local values)" };
            fill.Click += (s, e) => FillFromValues();
            tableRow.Children.Add(fill);
            _strict = new CheckBox { Content = "Only values in the table (others aren't written)", IsChecked = t.LookupStrict, VerticalAlignment = VerticalAlignment.Center };
            _strict.Checked += (s, e) => Queue(); _strict.Unchecked += (s, e) => Queue();
            tableRow.Children.Add(_strict);
            _sourceSteps.Children.Add(tableRow);

            _sourceSteps.Children.Add(Label("3. Expression (optional)"));
            _expression = Box(t.Expression ?? "");
            _expression.FontFamily = new FontFamily("Consolas");
            _sourceSteps.Children.Add(_expression);
            _sourceSteps.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Text = "Python-like, on value (text), number (its number: project units for lengths etc.), empty, and row[\"Name\"] = any other " +
                       "parameter of the same consultant element (as text). Without a consultant parameter the expression alone makes the value. Examples:\n" +
                       "  value.upper()      number / 1000      round(number, 1)\n" +
                       "  \"FRL \" + value if not empty else \"N/A\"\n" +
                       "  re.sub(r\"FR(\\d+)\", r\"\\1/\\1/\\1\", value)     value.replace(\"-\", \"/\")\n" +
                       "  row[\"Mark\"] + \"-\" + row[\"Level\"]      value + \" (\" + row[\"Comments\"] + \")\"",
            });
            var insert = new ComboBox { Margin = new Thickness(0, 4, 0, 0), IsEditable = true, IsTextSearchEnabled = true, Text = "",
                                        ToolTip = "Insert row[\"…\"] for another parameter of the consultant element at the cursor" };
            insert.Items.Add("Insert a consultant parameter…");
            foreach (var n in rowNames ?? new List<string>()) insert.Items.Add(n);
            insert.SelectedIndex = 0;
            insert.SelectionChanged += (s, e) =>
            {
                if (insert.SelectedIndex <= 0) return;
                string add = "row[\"" + ((string)insert.SelectedItem).Replace("\"", "\\\"") + "\"]";
                int at = Math.Min(_expression.CaretIndex, _expression.Text.Length);
                _expression.Text = _expression.Text.Insert(at, add);
                _expression.CaretIndex = at + add.Length;
                _expression.Focus();
                insert.SelectedIndex = 0;
            };
            _sourceSteps.Children.Add(insert);

            _sourceSteps.Children.Add(Label("4. Prefix / suffix"));
            var ps = new Grid();
            ps.ColumnDefinitions.Add(new ColumnDefinition());
            ps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            ps.ColumnDefinitions.Add(new ColumnDefinition());
            _prefix = Box(t.Prefix ?? ""); _prefix.ToolTip = "Prefix";
            _suffix = Box(t.Suffix ?? ""); _suffix.ToolTip = "Suffix";
            Grid.SetColumn(_suffix, 2);
            ps.Children.Add(_prefix); ps.Children.Add(_suffix);
            _sourceSteps.Children.Add(ps);
            body.Children.Add(_sourceSteps);

            root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => { _timer.Stop(); UpdatePreview(); };
            foreach (var b in new[] { _constant, _default, _expression, _prefix, _suffix }) b.TextChanged += (s, e) => Queue();
            _useConstant.Checked += (s, e) => { Enable(); Queue(); };
            _useConstant.Unchecked += (s, e) => { Enable(); Queue(); };
            clear.Click += (s, e) =>
            {
                _useConstant.IsChecked = false; _constant.Text = ""; _default.Text = ""; _expression.Text = ""; _prefix.Text = ""; _suffix.Text = "";
                _rows.Clear(); _strict.IsChecked = false;
            };
            ok.Click += (s, e) =>
            {
                _table.CommitEdit(DataGridEditingUnit.Row, true);
                Result = Read();
                DialogResult = true;
            };
            Enable();
            Loaded += (s, e) => UpdatePreview();
        }

        private static TextBlock Label(string s) => new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
        private static TextBox Box(string s) => new TextBox { Text = s, Padding = new Thickness(3), Margin = new Thickness(0, 2, 0, 0) };

        private void Enable()
        {
            bool k = _useConstant.IsChecked == true;
            _constant.IsEnabled = k;
            _sourceSteps.IsEnabled = !k;
            _sourceSteps.Opacity = k ? 0.45 : 1;
        }

        private void Queue() { _timer.Stop(); _timer.Start(); }

        private ParamTransform Read()
        {
            if (_useConstant.IsChecked == true) return new ParamTransform { Constant = _constant.Text ?? "" };
            return new ParamTransform
            {
                Default = Null(_default.Text), Expression = Null(_expression.Text?.Trim()), Prefix = Null(_prefix.Text), Suffix = Null(_suffix.Text),
                Lookup = _rows.Where(r => r != null && !string.IsNullOrEmpty(r.From)).Select(r => new LookupEntry { From = r.From, To = r.To ?? "" }).ToList(),
                LookupStrict = _strict.IsChecked == true,
            };
        }

        private static string Null(string s) => string.IsNullOrEmpty(s) ? null : s;

        private void FillFromValues()
        {
            _table.CommitEdit(DataGridEditingUnit.Row, true);
            var have = new HashSet<string>(_rows.Where(r => r?.From != null).Select(r => r.From.Trim()), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var v in _values.Where(v => v?.HasValue == true && !string.IsNullOrEmpty(v.Text)).Select(v => v.Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                if (have.Contains(v) || added >= 200) continue;
                _rows.Add(new LookupEntry { From = v, To = v });
                added++;
            }
            if (added == 0) _preview.Text = "Every consultant value is in the table already (or there are none).";
        }

        private void UpdatePreview()
        {
            try
            {
                var t = Read();
                var c = ParamTransforms.Check(_src, t, _dst);
                bool perElement = ParamTransforms.UsesRow(t) || ParamTransforms.ExpressionOnly(_src, t);
                var p = ParamTransforms.Preview(_src, t, _dst, perElement ? _aligned : _values, 12, perElement ? _rows2 : null);
                var lines = new List<string> { $"{c.Symbol} {c.Message}" };
                if (c.Level != CompatLevel.Error)
                {
                    lines.Add($"{p.Total:N0} value(s): {p.Converted:N0} written, {p.Empty:N0} empty, {p.Failing:N0} not written.");
                    foreach (var sm in p.Samples) lines.Add($"  {sm.Item1}  →  {sm.Item2}");
                    if (p.Problems.Count > 0) lines.Add("Not written: " + string.Join("; ", p.Problems));
                }
                _preview.Text = string.Join("\n", lines);
                _preview.Foreground = c.Level == CompatLevel.Error ? Bad : c.Level == CompatLevel.Warn || p.Failing > 0 ? Warn : Ink;
            }
            catch (Exception ex) { _preview.Text = ex.Message; _preview.Foreground = Bad; }
        }
    }
}
