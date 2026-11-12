using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modal "Review views" editor for ui/FederatorWindow: pick the host views the federator uses to show changes
    /// (models/ReviewViewSettings, federator/ReviewViews), in priority order, and whether their images are captured
    /// automatically. The caller saves the result.
    /// </summary>
    internal class ReviewViewsWindow : Window
    {
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private readonly List<ViewCandidate> _all;
        private readonly Dictionary<string, ViewCandidate> _byUid;
        private readonly ListBox _available, _chosen;
        private readonly TextBox _filter;
        private readonly CheckBox _auto;
        private readonly List<string> _order;

        internal ReviewViewSettings Result { get; private set; }
        internal bool RefreshNow { get; private set; }

        private class Item
        {
            internal ViewCandidate View;
            public override string ToString() =>
                $"{View.Name}   ({Pretty(View.ViewType)}{(string.IsNullOrEmpty(View.SheetNumber) ? "" : " · sheet " + View.SheetNumber)} · 1:{View.Scale})";
        }

        internal static string Pretty(string viewType)
        {
            switch (viewType)
            {
                case "FloorPlan": return "Floor plan";
                case "CeilingPlan": return "Ceiling plan";
                case "EngineeringPlan": return "Structural plan";
                case "AreaPlan": return "Area plan";
                case "ThreeD": return "3D";
                default: return viewType;
            }
        }

        internal ReviewViewsWindow(List<ViewCandidate> candidates, ReviewViewSettings current, string activeViewUid)
        {
            _all = candidates;
            _byUid = candidates.GroupBy(c => c.Uid).ToDictionary(g => g.Key, g => g.First());
            _order = (current?.ViewUids ?? new List<string>()).Where(u => _byUid.ContainsKey(u)).Distinct().ToList();

            Title = "Perseus — Review views";
            Width = 900;
            Height = 560;
            MinWidth = 640;
            MinHeight = 360;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Background = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(12) };
            var intro = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8),
                Text = "Review views are the go-to views for showing changes: when a change isn't visible in the active view, the federator opens the " +
                       "first review view that shows it (top of the list first) instead of its 3D view. Perseus also captures an image of each review view " +
                       "so the panel can show every change and replacement as a cropped picture without navigating Revit. " +
                       "Typically: one floor plan per level (cropped to the building), plus the key sections. Shared with everyone using this model.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            _auto = new CheckBox { Content = "Offer to capture missing / out-of-date view images when a comparison opens",
                                   IsChecked = current?.AutoCapture ?? true, VerticalAlignment = VerticalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = Btn("Save", () => Finish(false));
            ok.IsDefault = true;
            var captureNow = Btn("Save and capture images now", () => Finish(true));
            captureNow.ToolTip = "Slow: Revit exports every review view that has changes and is busy until it's done (seconds to minutes per view).";
            buttons.Children.Add(captureNow);
            buttons.Children.Add(ok);
            var cancel = Btn("Cancel", () => { DialogResult = false; });
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(_auto);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new DockPanel();
            var leftHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            leftHead.Children.Add(new TextBlock { Text = "Views in this model", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _filter = new TextBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Filter by name, type or sheet number" };
            DockPanel.SetDock(_filter, Dock.Right);
            leftHead.Children.Insert(0, _filter);
            DockPanel.SetDock(leftHead, Dock.Top);
            left.Children.Add(leftHead);
            _available = new ListBox { SelectionMode = SelectionMode.Extended };
            _available.MouseDoubleClick += (s, e) => Add();
            left.Children.Add(_available);
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            mid.Children.Add(Btn("Add →", Add));
            mid.Children.Add(Btn("← Remove", Remove));
            var addActive = Btn("Add active view", () =>
            {
                if (activeViewUid != null && _byUid.ContainsKey(activeViewUid) && !_order.Contains(activeViewUid)) _order.Add(activeViewUid);
                Refresh();
            });
            addActive.IsEnabled = activeViewUid != null && _byUid.ContainsKey(activeViewUid);
            addActive.Margin = new Thickness(0, 16, 0, 4);
            mid.Children.Add(addActive);
            Grid.SetColumn(mid, 1);
            body.Children.Add(mid);

            var right = new DockPanel();
            var rightHead = new TextBlock { Text = "Review views (first match wins)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 6) };
            DockPanel.SetDock(rightHead, Dock.Top);
            right.Children.Add(rightHead);
            var order = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            order.Children.Add(Btn("Move up", () => Move(-1)));
            order.Children.Add(Btn("Move down", () => Move(+1)));
            order.Children.Add(Btn("Sort by type", () =>
            {
                var sorted = _order.OrderBy(u => ReviewViews.TypeOrder(_byUid[u].ViewType)).ThenBy(u => _byUid[u].Name, StringComparer.OrdinalIgnoreCase).ToList();
                _order.Clear();
                _order.AddRange(sorted);
                Refresh();
            }));
            DockPanel.SetDock(order, Dock.Bottom);
            right.Children.Add(order);
            _chosen = new ListBox { SelectionMode = SelectionMode.Extended };
            _chosen.MouseDoubleClick += (s, e) => Remove();
            right.Children.Add(_chosen);
            Grid.SetColumn(right, 2);
            body.Children.Add(right);
            root.Children.Add(body);
            Content = root;

            _filter.TextChanged += (s, e) => Refresh();
            Refresh();
        }

        private void Refresh()
        {
            string q = _filter.Text?.Trim() ?? "";
            var keepA = _available.SelectedItems.Cast<Item>().Select(i => i.View.Uid).ToList();
            var keepC = _chosen.SelectedItems.Cast<Item>().Select(i => i.View.Uid).ToList();
            _available.Items.Clear();
            foreach (var v in _all.Where(v => !_order.Contains(v.Uid)))
            {
                var item = new Item { View = v };
                if (q.Length > 0 && item.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _available.Items.Add(item);
                if (keepA.Contains(v.Uid)) _available.SelectedItems.Add(item);
            }
            _chosen.Items.Clear();
            foreach (var u in _order)
            {
                var item = new Item { View = _byUid[u] };
                _chosen.Items.Add(item);
                if (keepC.Contains(u)) _chosen.SelectedItems.Add(item);
            }
        }

        private void Add()
        {
            foreach (var i in _available.SelectedItems.Cast<Item>().ToList()) if (!_order.Contains(i.View.Uid)) _order.Add(i.View.Uid);
            Refresh();
        }

        private void Remove()
        {
            foreach (var i in _chosen.SelectedItems.Cast<Item>().ToList()) _order.Remove(i.View.Uid);
            Refresh();
        }

        private void Move(int dir)
        {
            var sel = _chosen.SelectedItems.Cast<Item>().Select(i => i.View.Uid).ToList();
            if (sel.Count == 0) return;
            var idx = sel.Select(u => _order.IndexOf(u)).OrderBy(i => dir < 0 ? i : -i).ToList();
            foreach (int i in idx)
            {
                int j = i + dir;
                if (j < 0 || j >= _order.Count || sel.Contains(_order[j])) continue;
                var t = _order[i]; _order[i] = _order[j]; _order[j] = t;
            }
            Refresh();
        }

        private void Finish(bool capture)
        {
            Result = new ReviewViewSettings { ViewUids = _order.ToList(), AutoCapture = _auto.IsChecked == true };
            RefreshNow = capture;
            DialogResult = true;
        }

        private static Button Btn(string text, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), Background = Brushes.White };
            b.Click += (s, e) =>
            {
                try { click(); }
                catch (Exception ex) { logging.Log.Exception(ex); }
            };
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
