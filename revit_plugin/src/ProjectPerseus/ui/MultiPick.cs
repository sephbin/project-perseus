using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Drop-down tick list for the federator panel's filters: a button showing what's ticked ("All", "None", one name or
    /// "3 of 8") opening a popup with All / None (+ optional presets) and a check box per item. Items may sit under a
    /// parent (e.g. Modified → Moved / Geometry...): the parent's box ticks / unticks its children and shows mixed.
    /// What the user unticked is kept by key across SetItems, so new values (another comparison) start ticked.
    /// </summary>
    internal class MultiPick : ToggleButton
    {
        internal class Item
        {
            internal string Key, Label, Parent;
            internal Item(string key, string label = null, string parent = null) { Key = key; Label = label ?? key; Parent = parent; }
        }

        private readonly Popup _popup;
        private readonly StackPanel _list = new StackPanel();
        private readonly StackPanel _presets = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly List<Item> _items = new List<Item>();
        private readonly HashSet<string> _unticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        private bool _updating;
        private DateTime _closedAt;

        /// <summary>Raised when the ticks change (not by SetItems).</summary>
        internal event Action Changed;

        internal MultiPick(double width)
        {
            Width = width;
            Margin = new Thickness(0, 0, 10, 4);
            Padding = new Thickness(6, 2, 4, 2);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Background = Brushes.White;
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xAB, 0xAD, 0xB3));
            var content = new DockPanel { LastChildFill = true };
            var arrow = new TextBlock { Text = "▾", Margin = new Thickness(4, 0, 0, 0), Foreground = Brushes.DimGray };
            DockPanel.SetDock(arrow, Dock.Right);
            content.Children.Add(arrow);
            content.Children.Add(_text);
            Content = content;

            var all = SmallButton("All", "Tick every item");
            all.Click += (s, e) => SetAll(true);
            var none = SmallButton("None", "Untick every item");
            none.Click += (s, e) => SetAll(false);
            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(all);
            top.Children.Add(none);
            top.Children.Add(_presets);

            var panel = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            panel.Children.Add(top);
            panel.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            _popup = new Popup
            {
                PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                Child = new Border
                {
                    Background = Brushes.White, BorderBrush = BorderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(8),
                    MinWidth = Math.Max(width, 180), Child = panel,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 },
                },
            };
            // Button <-> popup by hand (a Binding needs System.Xaml). Clicking the button while open: the popup closes on mouse
            // down (StaysOpen=false), so swallow that click instead of reopening it.
            Checked += (s, e) => _popup.IsOpen = true;
            Unchecked += (s, e) => _popup.IsOpen = false;
            _popup.Closed += (s, e) => { _closedAt = DateTime.UtcNow; IsChecked = false; };
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) e.Handled = true;
            };
            UpdateText();
        }

        /// <summary>A quick pick next to All / None that ticks exactly these keys.</summary>
        internal void AddPreset(string label, string tip, params string[] keys)
        {
            var b = SmallButton(label, tip);
            b.Click += (s, e) =>
            {
                var want = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                _unticked.Clear();
                foreach (var i in _items.Where(i => !IsParent(i.Key) && !want.Contains(i.Key))) _unticked.Add(i.Key);
                Sync(); Changed?.Invoke();
            };
            _presets.Children.Add(b);
        }

        /// <summary>Replaces the items; keys the user unticked stay unticked, new keys start ticked.</summary>
        internal void SetItems(IEnumerable<Item> items)
        {
            _items.Clear();
            _items.AddRange(items);
            _list.Children.Clear();
            _boxes.Clear();
            foreach (var it in _items)
            {
                var cb = new CheckBox { Content = it.Label, Margin = new Thickness(it.Parent == null ? 0 : 18, 2, 0, 2) };
                string key = it.Key;
                bool parent = IsParent(key);
                cb.IsThreeState = false;
                cb.Click += (s, e) =>
                {
                    if (_updating) return;
                    // A parent's box cycles to mixed on click when three-state; treat any click as "all on" unless it was all on.
                    bool on = parent ? !Children(key).All(c => !_unticked.Contains(c)) : cb.IsChecked == true;
                    foreach (var k in parent ? Children(key).ToList() : new List<string> { key })
                        if (on) _unticked.Remove(k); else _unticked.Add(k);
                    Sync(); Changed?.Invoke();
                };
                _boxes[key] = cb;
                _list.Children.Add(cb);
            }
            Sync();
        }

        /// <summary>The ticked leaf keys, or null when all are ticked (no filter).</summary>
        internal HashSet<string> Selected
        {
            get
            {
                var leaves = Leaves().ToList();
                if (leaves.All(k => !_unticked.Contains(k))) return null;
                return new HashSet<string>(leaves.Where(k => !_unticked.Contains(k)), StringComparer.OrdinalIgnoreCase);
            }
        }

        internal bool IsTicked(string key) => !_unticked.Contains(key);

        /// <summary>Ticks / unticks one item without raising Changed (initial defaults).</summary>
        internal void SetTicked(string key, bool on)
        {
            if (on) _unticked.Remove(key); else _unticked.Add(key);
            Sync();
        }

        private void SetAll(bool on)
        {
            _unticked.Clear();
            if (!on) foreach (var k in Leaves()) _unticked.Add(k);
            Sync(); Changed?.Invoke();
        }

        private bool IsParent(string key) => _items.Any(i => string.Equals(i.Parent, key, StringComparison.OrdinalIgnoreCase));
        private IEnumerable<string> Children(string key) => _items.Where(i => string.Equals(i.Parent, key, StringComparison.OrdinalIgnoreCase)).Select(i => i.Key);
        private IEnumerable<string> Leaves() => _items.Where(i => !IsParent(i.Key)).Select(i => i.Key);

        private void Sync()
        {
            _updating = true;
            try
            {
                foreach (var it in _items)
                {
                    if (!_boxes.TryGetValue(it.Key, out var cb)) continue;
                    if (IsParent(it.Key))
                    {
                        var kids = Children(it.Key).ToList();
                        int on = kids.Count(k => !_unticked.Contains(k));
                        cb.IsThreeState = on > 0 && on < kids.Count;
                        cb.IsChecked = on == kids.Count ? true : on == 0 ? false : (bool?)null;
                    }
                    else cb.IsChecked = !_unticked.Contains(it.Key);
                }
            }
            finally { _updating = false; }
            UpdateText();
        }

        private void UpdateText()
        {
            var leaves = _items.Where(i => !IsParent(i.Key)).ToList();
            var on = leaves.Where(i => !_unticked.Contains(i.Key)).ToList();
            string text;
            if (on.Count == leaves.Count) text = "All";
            else if (on.Count == 0) text = "None";
            else if (on.Count == 1) text = on[0].Label;
            else
            {
                // A parent with all its children ticked reads as the parent (e.g. "Added, Modified").
                var parts = new List<string>();
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in _items.Where(i => IsParent(i.Key)))
                {
                    var kids = Children(p.Key).ToList();
                    if (kids.All(k => !_unticked.Contains(k))) { parts.Add(p.Label); foreach (var k in kids) done.Add(k); }
                }
                parts.AddRange(on.Where(i => !done.Contains(i.Key)).Select(i => i.Label));
                text = parts.Count <= 2 ? string.Join(", ", parts) : $"{on.Count} of {leaves.Count}";
            }
            _text.Text = text;
            _text.FontWeight = on.Count == leaves.Count ? FontWeights.Normal : FontWeights.SemiBold;
            ToolTip = on.Count == leaves.Count || on.Count == 0 ? null : string.Join("\n", on.Select(i => i.Label).Take(30));
        }

        private static Button SmallButton(string text, string tip) =>
            new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 6, 0),
                         Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE6, 0xEE)) };
    }
}
