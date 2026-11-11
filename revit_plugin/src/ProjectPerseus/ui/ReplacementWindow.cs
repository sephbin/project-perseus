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
    /// <summary>A deleted row's suggested replacement (federator/ReplacementMatcher) that hasn't been confirmed or rejected.</summary>
    internal class ReplacementSuggestion
    {
        internal ChangeRow Deleted;
        internal ChangeRow Replacement;
        internal ReplacementCandidate Candidate;
        internal int Alternatives;
    }

    /// <summary>
    /// Orphan fix review for ui/FederatorWindow: every deleted element that looks replaced by a new one (same category,
    /// in its place), best match first. Tick and Link (one Replaced change each, federator/ReplacementLinks) or mark as
    /// not replacements. Writes go through the panel; the list refreshes on its ReviewChanged.
    /// </summary>
    internal class ReplacementWindow : Window
    {
        private const double PreTick = 0.8;
        private const int MaxShown = 600;
        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);

        private readonly FederatorWindow _owner;
        private readonly StackPanel _list;
        private readonly TextBlock _summary;
        private readonly Button _linkButton, _rejectButton;
        private List<ReplacementSuggestion> _items = new List<ReplacementSuggestion>();
        // Ticks by deleted UniqueId, kept across refreshes; never-seen suggestions start ticked when the match is strong.
        private readonly HashSet<string> _ticked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        internal ReplacementWindow(FederatorWindow owner)
        {
            _owner = owner;
            Title = "Perseus — Possible replacements";
            Width = 900;
            Height = 600;
            MinWidth = 640;
            MinHeight = 320;
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
                Text = "Deleted elements with a new element of the same category in their place: the consultant probably deleted and re-modelled them. " +
                       "Link the ones that are the same element: each pair becomes one Replaced change (⇄), and references to the old element can later be " +
                       "moved to the new one. Strong matches (80 %+) start ticked. Links are saved in the model for everyone and can be broken from the panel.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            tools.Children.Add(Btn("Tick 80 %+", "Tick the strong matches only", () => TickWhere(s => s.Candidate.Score >= PreTick)));
            tools.Children.Add(Btn("Tick all", "", () => TickWhere(s => true)));
            tools.Children.Add(Btn("Tick none", "", () => TickWhere(s => false)));
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            tools.Children.Add(_summary);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            _linkButton = Btn("Link ticked", "Confirm the ticked pairs as replacements", () => _owner.ConfirmReplacements(Ticked()));
            _rejectButton = Btn("Not replacements", "Stop suggesting a replacement for the ticked deleted elements",
                                () => _owner.RejectReplacements(Ticked().Select(s => s.Deleted).ToList()));
            footer.Children.Add(_linkButton);
            footer.Children.Add(_rejectButton);
            footer.Children.Add(Btn("Close", "", Close));
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _list = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                                 BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 1) });
            Content = root;

            Action refresh = () => Dispatcher.BeginInvoke(new Action(Refresh));
            _owner.ReviewChanged += refresh;
            Closed += (s, e) => _owner.ReviewChanged -= refresh;
            Refresh();
        }

        private void Refresh()
        {
            _items = _owner.ReplacementSuggestions();
            var keys = new HashSet<string>(_items.Select(s => s.Deleted.Change.UniqueId), StringComparer.Ordinal);
            _ticked.IntersectWith(keys);
            foreach (var s in _items)
                if (_seen.Add(s.Deleted.Change.UniqueId) && s.Candidate.Score >= PreTick) _ticked.Add(s.Deleted.Change.UniqueId);

            _list.Children.Clear();
            if (_items.Count == 0)
                _list.Children.Add(new TextBlock { Text = "No unconfirmed replacements in this comparison.", Foreground = Muted, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var s in _items.Take(MaxShown)) _list.Children.Add(RowFor(s));
            if (_items.Count > MaxShown)
                _list.Children.Add(new TextBlock { Text = $"… and {_items.Count - MaxShown:N0} more (link or reject these first).", Foreground = Muted, Margin = new Thickness(0, 6, 0, 0) });
            UpdateSummary();
        }

        private UIElement RowFor(ReplacementSuggestion s)
        {
            string key = s.Deleted.Change.UniqueId;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var w in new[] { new GridLength(26), new GridLength(1, GridUnitType.Star), new GridLength(26), new GridLength(1, GridUnitType.Star),
                                      new GridLength(1.1, GridUnitType.Star), GridLength.Auto })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var cb = new CheckBox { IsChecked = _ticked.Contains(key), VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (o, e) => { _ticked.Add(key); UpdateSummary(); };
            cb.Unchecked += (o, e) => { _ticked.Remove(key); UpdateSummary(); };
            Put(g, cb, 0);
            Put(g, Side("−", ChangeRow.DeletedBrush, s.Deleted, "deleted"), 1);
            Put(g, new TextBlock { Text = "⇄", Foreground = ChangeRow.ReplacedBrush, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                                   HorizontalAlignment = HorizontalAlignment.Center }, 2);
            Put(g, Side("+", ChangeRow.AddedBrush, s.Replacement, "new"), 3);
            Put(g, new TextBlock
            {
                Text = $"{s.Candidate.Score * 100:0} % · {s.Candidate.Reason}" + (s.Alternatives > 0 ? $" · {s.Alternatives} other candidate(s)" : ""),
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
            }, 4);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Btn("Zoom", "Zoom to the old position and the new element in Revit",
                                     () => _owner.TriageShow(new List<ChangeRow> { s.Deleted, s.Replacement }, true)));
            buttons.Children.Add(Btn("Link", "Confirm this pair", () => _owner.ConfirmReplacements(new List<ReplacementSuggestion> { s })));
            buttons.Children.Add(Btn("No", "Not a replacement", () => _owner.RejectReplacements(new List<ChangeRow> { s.Deleted })));
            Put(g, buttons, 5);
            return new Border { Child = g, BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3) };
        }

        private static UIElement Side(string glyph, Brush brush, ChangeRow row, string what)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(new System.Windows.Documents.Run(glyph + " ") { Foreground = brush, FontWeight = FontWeights.Bold });
            t.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrEmpty(row.Element) ? row.Category : row.Element));
            t.Inlines.Add(new System.Windows.Documents.Run($"\n{row.Category} · Id {row.Id} ({what})" + (string.IsNullOrEmpty(row.Level) ? "" : " · " + row.Level))
                          { Foreground = Muted, FontSize = 11 });
            return t;
        }

        private List<ReplacementSuggestion> Ticked() => _items.Where(s => _ticked.Contains(s.Deleted.Change.UniqueId)).ToList();

        private void TickWhere(Func<ReplacementSuggestion, bool> pick)
        {
            _ticked.Clear();
            foreach (var s in _items.Where(pick)) _ticked.Add(s.Deleted.Change.UniqueId);
            Refresh();
        }

        private void UpdateSummary()
        {
            int n = Ticked().Count;
            _summary.Text = $"{_items.Count:N0} possible replacement(s) · {n:N0} ticked";
            _linkButton.Content = n > 0 ? $"Link ticked ({n:N0})" : "Link ticked";
            _linkButton.IsEnabled = _rejectButton.IsEnabled = n > 0;
        }

        private static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

        private static Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
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
