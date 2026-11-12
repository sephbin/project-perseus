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
        /// <summary>Why it's hidden by default (noise / ignored in triage), else null.</summary>
        internal string Hidden;
    }

    /// <summary>
    /// Orphan fix review for ui/FederatorWindow, built for speed: suggestions are grouped (federator/ReplacementGroups:
    /// same category, old type -> new type, same spatial relation) and the top 10 groups are shown first, each with a
    /// picture of its best example on a review view image (ui/ChangeThumbnail). Link a whole group in one click, mark it
    /// as not replacements, or open it to check pairs one by one. Patterns people linked before come first and are
    /// flagged (federator/ReplacementLearning). Suggestions on noise or ignored elements / types are hidden. Writes go
    /// through the panel; the list refreshes on its ReviewChanged / ImagesChanged.
    /// </summary>
    internal class ReplacementWindow : Window
    {
        private const int GroupPage = 10;
        private const int PairPage = 20;
        private const double Confident = 0.8;
        private static readonly Brush Ink = Frozen(0x2F, 0x3D, 0x57);
        private static readonly Brush Muted = Frozen(0x6B, 0x72, 0x80);
        private static readonly Brush LineBrush = Frozen(0xE2, 0xE6, 0xEE);
        private static readonly Brush Good = Frozen(0x2E, 0xAD, 0x4B);
        private static readonly Brush Bad = Frozen(0xC0, 0x26, 0x26);
        private static readonly Brush CardFill = Frozen(0xFA, 0xFB, 0xFD);

        private readonly FederatorWindow _owner;
        private readonly StackPanel _list;
        private readonly TextBlock _summary;
        private readonly CheckBox _showHidden;
        private readonly Button _linkConfident;
        private readonly ScrollViewer _scroll;
        private List<ReplacementGroup> _groups = new List<ReplacementGroup>();
        // Category > type tree: open categories (the first one opens by default until the user toggles any), closed types,
        // and how many size / placement groups are shown per type.
        private readonly HashSet<string> _openCats = new HashSet<string>(StringComparer.Ordinal);
        private bool _catsTouched;
        private readonly HashSet<string> _closedTypes = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _groupsShown = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pairsShown = new Dictionary<string, int>(StringComparer.Ordinal);
        // Ticks by deleted UniqueId inside opened groups (start ticked).
        private readonly HashSet<string> _unticked = new HashSet<string>(StringComparer.Ordinal);
        private string _loggedSig;

        internal ReplacementWindow(FederatorWindow owner)
        {
            _owner = owner;
            Title = "Perseus — Possible replacements";
            Width = 1000;
            Height = 720;
            MinWidth = 700;
            MinHeight = 360;
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
                       "They're listed by category, then by type (old → new), then by size and placement: link a whole category, type or group at once, or open a group to check its pairs. " +
                       "Each linked pair becomes one Replaced change (⇄). What people link or reject is remembered and used to rank future suggestions.",
            };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            _showHidden = new CheckBox { Content = "Show hidden", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                         ToolTip = "Also show suggestions on noise (Triage noise rules), on elements / types marked Ignored, and pairs of a different family (e.g. a detector next to a sprinkler). Once people link such a pair, similar pairs show again." };
            _showHidden.Checked += (s, e) => Refresh();
            _showHidden.Unchecked += (s, e) => Refresh();
            right.Children.Add(_showHidden);
            _linkConfident = Btn("", "Link every pair in the groups shown with an average match of 80 % or more (and no rejections learnt).", LinkConfident);
            right.Children.Add(_linkConfident);
            DockPanel.SetDock(right, Dock.Right);
            tools.Children.Add(right);
            _summary = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            tools.Children.Add(_summary);
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            footer.Children.Add(Btn("Close", "", Close));
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _list = new StackPanel();
            _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                         BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 1) };
            root.Children.Add(_scroll);
            Content = root;

            Action refresh = () => Dispatcher.BeginInvoke(new Action(Refresh));
            _owner.ReviewChanged += refresh;
            _owner.ImagesChanged += refresh;
            Closed += (s, e) => { _owner.ReviewChanged -= refresh; _owner.ImagesChanged -= refresh; };
            Refresh();
        }

        private void Refresh()
        {
            double offset = _scroll.VerticalOffset;
            var all = _owner.ReplacementSuggestions(includeHidden: true);
            int hidden = all.Count(s => s.Hidden != null);
            var shown = _showHidden.IsChecked == true ? all : all.Where(s => s.Hidden == null).ToList();
            _groups = ReplacementGroups.Build(shown.Select(s => new ReplacementPair
            {
                Deleted = s.Deleted.Change, Added = s.Replacement.Change, Score = s.Candidate.Score, Tag = s,
            }), _owner.Learning);
            string sig = $"{shown.Count}|{hidden}|{_groups.Count}";
            if (sig != _loggedSig)
            {
                _loggedSig = sig;
                logging.Log.Info($"[Federator] replacements: {shown.Count} shown, {hidden} hidden ({string.Join(", ", all.Where(x => x.Hidden != null).GroupBy(x => x.Hidden).Select(g => $"{g.Key} {g.Count()}"))}), " +
                                 $"{_groups.Count} group(s): " + string.Join(" | ", _groups.Take(15).Select(g =>
                                     $"[{g.Category}] '{g.OldFamily}'/'{((ReplacementSuggestion)g.Example.Tag).Deleted.Change.Type}' -> '{g.NewFamily}'/'{((ReplacementSuggestion)g.Example.Tag).Replacement.Change.Type}' {g.Size} {g.Relation} x{g.Pairs.Count}")));
            }

            _summary.Text = $"{shown.Count:N0} possible replacement(s) in {_groups.Count:N0} group(s)" +
                            (hidden > 0 ? $" · {hidden:N0} hidden (noise, ignored in triage, or a different family)" : "");
            _showHidden.Content = hidden > 0 ? $"Show hidden ({hidden:N0})" : "Show hidden";
            var confident = ConfidentGroups();
            int confidentPairs = confident.Sum(g => g.Pairs.Count);
            _linkConfident.Content = $"Link confident groups ({confidentPairs:N0})";
            _linkConfident.IsEnabled = confidentPairs > 0;

            _list.Children.Clear();
            if (_groups.Count == 0)
                _list.Children.Add(new TextBlock { Text = "No unconfirmed replacements in this comparison.", Foreground = Muted, Margin = new Thickness(0, 8, 0, 0) });
            if (!_owner.HasViewImages)
                _list.Children.Add(new TextBlock
                {
                    Text = "Tip: register review views (Review views… in the panel) to see a picture of each replacement here instead of navigating Revit.",
                    Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6),
                });
            BuildTree();
            _scroll.UpdateLayout();
            _scroll.ScrollToVerticalOffset(offset);
        }

        private List<ReplacementGroup> ConfidentGroups() =>
            _groups.Where(g => g.MeanScore >= Confident && g.LearntBias >= 0).ToList();

        private static int PairCount(IEnumerable<ReplacementGroup> groups) => groups.Sum(g => g.Pairs.Count);

        private static List<ReplacementSuggestion> SuggestionsOf(IEnumerable<ReplacementGroup> groups) =>
            groups.SelectMany(g => g.Pairs).Select(p => (ReplacementSuggestion)p.Tag).ToList();

        private static string TypeTitle(ReplacementGroup g) => g.OldType == g.NewType ? $"{g.OldType}  →  same type" : $"{g.OldType}  →  {g.NewType}";

        /// <summary>Category (biggest first; learnt ones first) > type old → new (biggest first) > size / placement group cards.</summary>
        private void BuildTree()
        {
            var cats = _groups.GroupBy(g => g.Category)
                .Select(c => c.ToList())
                .OrderByDescending(c => c.Any(g => g.LearntBias > 0.2))
                .ThenByDescending(c => PairCount(c))
                .ThenBy(c => c[0].Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool first = true;
            foreach (var cat in cats)
            {
                string catName = cat[0].Category;
                bool open = _catsTouched ? _openCats.Contains(catName) : first;
                first = false;
                var types = cat.GroupBy(g => g.OldType + "\u0001" + g.NewType).Select(t => t.ToList())
                               .OrderByDescending(t => t.Any(g => g.LearntBias > 0.2)).ThenByDescending(t => PairCount(t)).ToList();
                _list.Children.Add(Header(open, $"{catName}", $"{PairCount(cat):N0} pair(s) in {types.Count} type(s)", 14.5, new Thickness(0, 10, 0, 0),
                    () =>
                    {
                        if (!_catsTouched) { _catsTouched = true; if (cats.Count > 0) _openCats.Add(cats[0][0].Category); }
                        if (!_openCats.Remove(catName)) _openCats.Add(catName);
                        Refresh();
                    }, cat, $"every {catName} suggestion ({types.Count} type(s))"));
                if (!open) continue;
                foreach (var type in types)
                {
                    string typeKey = catName + "\u0001" + type[0].OldType + "\u0001" + type[0].NewType;
                    bool typeOpen = !_closedTypes.Contains(typeKey);
                    string learnt = type.Select(g => g.Learnt).FirstOrDefault(l => l != null);
                    _list.Children.Add(Header(typeOpen, TypeTitle(type[0]),
                        $"{PairCount(type):N0} pair(s) in {type.Count} size / placement group(s)" + (learnt != null ? " · people " + learnt : ""),
                        12.5, new Thickness(18, 6, 0, 0),
                        () => { if (!_closedTypes.Remove(typeKey)) _closedTypes.Add(typeKey); Refresh(); }, type, TypeTitle(type[0])));
                    if (!typeOpen) continue;
                    _groupsShown.TryGetValue(typeKey, out int n);
                    if (n <= 0) n = GroupPage;
                    foreach (var g in type.Take(n))
                    {
                        var card = (FrameworkElement)Card(g);
                        card.Margin = new Thickness(36, 6, 4, 0);
                        _list.Children.Add(card);
                    }
                    if (type.Count > n)
                    {
                        var more = Btn($"Show {Math.Min(GroupPage, type.Count - n)} more groups ({type.Count - n:N0} left)", "",
                                       () => { _groupsShown[typeKey] = n + GroupPage; Refresh(); });
                        more.HorizontalAlignment = HorizontalAlignment.Left;
                        more.Margin = new Thickness(36, 6, 0, 0);
                        _list.Children.Add(more);
                    }
                }
            }
        }

        /// <summary>A collapsible category / type row with Link all / Not replacements for everything under it.</summary>
        private UIElement Header(bool open, string title, string detail, double size, Thickness margin, Action toggle,
                                 List<ReplacementGroup> groups, string what)
        {
            var dock = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            int n = PairCount(groups);
            buttons.Children.Add(Btn(n == 1 ? "Link" : $"Link all {n:N0}", $"Confirm {what} as replacements", () =>
            {
                if (groups.Count > 1 && MessageBox.Show(this, $"Link {n:N0} pair(s) as replacements: {what}?", "Link replacements",
                                                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _owner.ConfirmReplacements(SuggestionsOf(groups));
            }));
            buttons.Children.Add(Btn("Not replacements", $"Reject {what}", () =>
            {
                if (groups.Count > 1 && MessageBox.Show(this, $"Mark {n:N0} deleted element(s) as not replaced: {what}?", "Not replacements",
                                                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _owner.RejectReplacements(SuggestionsOf(groups).Select(s => s.Deleted).ToList());
            }));
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Cursor = System.Windows.Input.Cursors.Hand };
            text.Inlines.Add(new System.Windows.Documents.Run((open ? "▾ " : "▸ ")) { Foreground = Muted });
            text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, FontSize = size });
            text.Inlines.Add(new System.Windows.Documents.Run("   " + detail) { Foreground = Muted });
            text.MouseLeftButtonUp += (s, e) => { try { toggle(); } catch (Exception ex) { logging.Log.Exception(ex); } };
            dock.Children.Add(text);
            return new Border { Child = dock, Margin = margin, Padding = new Thickness(4, 4, 0, 4), BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1) };
        }

        private void LinkConfident()
        {
            var groups = ConfidentGroups();
            int n = groups.Sum(g => g.Pairs.Count);
            if (n == 0) return;
            if (MessageBox.Show(this, $"Link {n:N0} pair(s) in {groups.Count} group(s) as replacements?\n\n" +
                                      string.Join("\n", groups.Take(12).Select(g => $"• {g.Title}: {g.Pairs.Count:N0} ({g.Relation})")) +
                                      (groups.Count > 12 ? "\n…" : ""),
                                "Link replacements", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _owner.ConfirmReplacements(groups.SelectMany(g => g.Pairs).Select(p => (ReplacementSuggestion)p.Tag).ToList());
        }

        private UIElement Card(ReplacementGroup g)
        {
            var ex = (ReplacementSuggestion)g.Example.Tag;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var thumb = _owner.Thumbnail(new List<ChangeRow> { ex.Deleted, ex.Replacement }, 240, 150,
                                         "Best example of this group: dashed red = deleted element, green = the new one. Click to zoom in Revit.")
                        ?? Placeholder(240, 150);
            thumb.Margin = new Thickness(0, 0, 12, 0);
            Put(grid, thumb, 0, 0);

            var info = new StackPanel();
            var title = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            title.Inlines.Add(new System.Windows.Documents.Run("⇄ ") { Foreground = ChangeRow.ReplacedBrush });
            title.Inlines.Add(new System.Windows.Documents.Run($"{g.Size} · {g.Relation}"));
            info.Children.Add(title);
            info.Children.Add(new TextBlock
            {
                Text = $"{g.Pairs.Count:N0} pair(s) · " +
                       (g.Levels.Count == 1 ? g.Levels[0] : $"{g.Levels.Count} levels ({string.Join(", ", g.Levels.Take(3))}{(g.Levels.Count > 3 ? "…" : "")})") +
                       $" · {g.MeanScore * 100:0} % match on average" + (g.Pairs.Count > 1 ? $" (lowest {g.MinScore * 100:0} %)" : ""),
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            if (g.Learnt != null)
                info.Children.Add(new TextBlock
                {
                    Text = (g.LearntBias >= 0 ? "✓ " : "✗ ") + "People " + g.Learnt + ".",
                    Foreground = g.LearntBias >= 0 ? Good : Bad, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                });
            int hidden = g.Pairs.Count(p => ((ReplacementSuggestion)p.Tag).Hidden != null);
            if (hidden > 0)
                info.Children.Add(new TextBlock { Text = $"{hidden:N0} hidden by default ({((ReplacementSuggestion)g.Pairs.First(p => ((ReplacementSuggestion)p.Tag).Hidden != null).Tag).Hidden}).",
                                                  Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) });
            info.Children.Add(new TextBlock
            {
                Text = $"Example: − Id {ex.Deleted.Change.Id} ({ReplacementGroups.Dimensions(ex.Deleted.Change.OldMin, ex.Deleted.Change.OldMax)})" +
                       $"  →  + Id {ex.Replacement.Change.Id} ({ReplacementGroups.Dimensions(ex.Replacement.Change.NewMin, ex.Replacement.Change.NewMax)})" +
                       (string.IsNullOrEmpty(ex.Deleted.Level) ? "" : $" · {ex.Deleted.Level}") + $" · {ex.Candidate.Reason}",
                Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            Put(grid, info, 1, 0);

            var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            var all = Btn(g.Pairs.Count == 1 ? "Link" : $"Link all {g.Pairs.Count:N0}", "Confirm every pair in this group as a replacement",
                          () => _owner.ConfirmReplacements(g.Pairs.Select(p => (ReplacementSuggestion)p.Tag).ToList()));
            all.FontWeight = FontWeights.SemiBold;
            buttons.Children.Add(all);
            buttons.Children.Add(Btn(g.Pairs.Count == 1 ? "Not a replacement" : "Not replacements", "Stop suggesting these (remembered for similar pairs too)",
                                     () => _owner.RejectReplacements(g.Pairs.Select(p => ((ReplacementSuggestion)p.Tag).Deleted).ToList())));
            bool open = _expanded.Contains(g.Key);
            if (g.Pairs.Count > 1)
                buttons.Children.Add(Btn(open ? "Hide pairs ▴" : $"Review pairs ▾", "Check the pairs one by one", () =>
                {
                    if (!_expanded.Remove(g.Key)) _expanded.Add(g.Key);
                    Refresh();
                }));
            buttons.Children.Add(Btn("Zoom to example", "Zoom to the example's old position and new element in Revit",
                                     () => _owner.TriageShow(new List<ChangeRow> { ex.Deleted, ex.Replacement }, true)));
            foreach (Button b in buttons.Children) { b.Margin = new Thickness(0, 0, 0, 4); b.HorizontalAlignment = HorizontalAlignment.Stretch; }
            Put(grid, buttons, 2, 0);

            if (open && g.Pairs.Count > 1)
            {
                var pairs = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                _pairsShown.TryGetValue(g.Key, out int n);
                if (n <= 0) n = PairPage;
                foreach (var p in g.Pairs.Take(n)) pairs.Children.Add(PairRow((ReplacementSuggestion)p.Tag));
                var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                if (g.Pairs.Count > n)
                    bar.Children.Add(Btn($"Show {Math.Min(PairPage, g.Pairs.Count - n)} more ({g.Pairs.Count - n:N0} left)", "",
                                         () => { _pairsShown[g.Key] = n + PairPage; Refresh(); }));
                var ticked = g.Pairs.Take(n).Select(p => (ReplacementSuggestion)p.Tag).Where(s => !_unticked.Contains(s.Deleted.Change.UniqueId)).ToList();
                var linkT = Btn($"Link ticked ({ticked.Count})", "Confirm the ticked pairs shown", () => _owner.ConfirmReplacements(Ticked(g, n)));
                linkT.IsEnabled = ticked.Count > 0;
                var rejT = Btn($"Not replacements ({ticked.Count})", "Reject the ticked pairs shown", () => _owner.RejectReplacements(Ticked(g, n).Select(s => s.Deleted).ToList()));
                rejT.IsEnabled = ticked.Count > 0;
                bar.Children.Add(linkT);
                bar.Children.Add(rejT);
                pairs.Children.Add(bar);
                Grid.SetColumnSpan(pairs, 3);
                Put(grid, pairs, 0, 1);
            }

            return new Border
            {
                Child = grid, Background = CardFill, BorderBrush = LineBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(10), Margin = new Thickness(0, 8, 4, 0),
            };
        }

        private List<ReplacementSuggestion> Ticked(ReplacementGroup g, int n) =>
            g.Pairs.Take(n).Select(p => (ReplacementSuggestion)p.Tag).Where(s => !_unticked.Contains(s.Deleted.Change.UniqueId)).ToList();

        private UIElement PairRow(ReplacementSuggestion s)
        {
            string key = s.Deleted.Change.UniqueId;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cb = new CheckBox { IsChecked = !_unticked.Contains(key), VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (o, e) => { _unticked.Remove(key); Refresh(); };
            cb.Unchecked += (o, e) => { _unticked.Add(key); Refresh(); };
            Put(g, cb, 0, 0);
            var thumb = _owner.Thumbnail(new List<ChangeRow> { s.Deleted, s.Replacement }, 170, 106, "Click to zoom in Revit") ?? Placeholder(170, 106);
            thumb.Margin = new Thickness(0, 0, 10, 0);
            Put(g, thumb, 1, 0);
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(new System.Windows.Documents.Run("− ") { Foreground = ChangeRow.DeletedBrush, FontWeight = FontWeights.Bold });
            t.Inlines.Add(new System.Windows.Documents.Run($"Id {s.Deleted.Change.Id}"));
            t.Inlines.Add(new System.Windows.Documents.Run("   →   ") { Foreground = ChangeRow.ReplacedBrush });
            t.Inlines.Add(new System.Windows.Documents.Run("+ ") { Foreground = ChangeRow.AddedBrush, FontWeight = FontWeights.Bold });
            t.Inlines.Add(new System.Windows.Documents.Run($"Id {s.Replacement.Change.Id}"));
            var dc = s.Deleted.Change; var ac = s.Replacement.Change;
            t.Inlines.Add(new System.Windows.Documents.Run($"\n{s.Deleted.Element}  ({ReplacementGroups.Dimensions(dc.OldMin, dc.OldMax)})" +
                                                           $"\n→ {s.Replacement.Element}  ({ReplacementGroups.Dimensions(ac.NewMin, ac.NewMax)})") { FontSize = 11 });
            t.Inlines.Add(new System.Windows.Documents.Run($"\n{s.Candidate.Score * 100:0} % · {s.Candidate.Reason}" +
                                                           (string.IsNullOrEmpty(s.Deleted.Level) ? "" : " · " + s.Deleted.Level) +
                                                           (s.Alternatives > 0 ? $" · {s.Alternatives} other candidate(s)" : "") +
                                                           (s.Hidden != null ? $" · hidden: {s.Hidden}" : "")) { Foreground = Muted, FontSize = 11 });
            Put(g, t, 2, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Btn("Zoom", "Zoom to the old position and the new element in Revit",
                                     () => _owner.TriageShow(new List<ChangeRow> { s.Deleted, s.Replacement }, true)));
            buttons.Children.Add(Btn("Link", "Confirm this pair", () => _owner.ConfirmReplacements(new List<ReplacementSuggestion> { s })));
            buttons.Children.Add(Btn("No", "Not a replacement", () => _owner.RejectReplacements(new List<ChangeRow> { s.Deleted })));
            Put(g, buttons, 3, 0);
            return new Border { Child = g, BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 3, 0, 0) };
        }

        private static FrameworkElement Placeholder(double w, double h) => new Border
        {
            Width = w, Height = h, BorderBrush = LineBrush, BorderThickness = new Thickness(1), Background = Brushes.White,
            Child = new TextBlock { Text = "No review view image", Foreground = Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center },
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
        };

        private static void Put(Grid g, UIElement e, int col, int row) { Grid.SetColumn(e, col); Grid.SetRow(e, row); g.Children.Add(e); }

        private static Button Btn(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                                 Background = Brushes.White };
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
