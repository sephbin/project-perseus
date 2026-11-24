using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UIApplication = Autodesk.Revit.UI.UIApplication;
using RevitSelectionChangedEventArgs = Autodesk.Revit.UI.Events.SelectionChangedEventArgs;
using ProjectPerseus.collections;
using ProjectPerseus.config;
using ProjectPerseus.ids;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Modeless "Collections" panel (REFERENCE_COLLECTIONS_PLAN.md stage 4; ribbon panel Collections). The collection
    /// parameters are locked, so this is where people edit them:
    ///   left     the collection picker and its owners (sign pack types) with placed counts and a stale / broken badge;
    ///   Members  the selected owner's ordered slots: add from the allowed targets (search) or Pick in model, insert an
    ///            empty slot, duplicate, reorder, remove, replace, fix stale references (re-point / accept rename);
    ///   Instance overrides  follows the Revit selection: placed owners x member slots x overridable fields, overridden
    ///            cells highlighted, reset cell / slot / all; several selected instances are edited together;
    ///   Preview  what the value rules reading the collection would write on the instance, with the unsaved edits.
    /// Edits stay local until Apply, which writes them in one transaction (one undo step) and moves every instance's
    /// overrides with its reordered members. Owners checked out by someone else are read-only with their name.
    /// WPF runs on Revit's main thread; button handlers run outside an API context, so model work goes through
    /// queue/OwnershipUiEvent. SelectionChanged (subscribed while open) arrives in an API context.
    /// </summary>
    internal class CollectionsWindow : Window
    {
        internal static CollectionsWindow Instance { get; private set; }

        internal const string TransactionName = "Perseus: Edit collection";

        private static readonly Brush Ink = Frozen(Color.FromRgb(0x2F, 0x3D, 0x57));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush ErrorBrush = Frozen(Color.FromRgb(0xC0, 0x26, 0x26));
        private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xB4, 0x5F, 0x06));
        private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x1F, 0x8A, 0x3E));
        private static readonly Brush LineBrush = Frozen(Color.FromRgb(0xE2, 0xE6, 0xEE));
        private static readonly Brush OverriddenFill = Frozen(Color.FromRgb(0xFF, 0xF4, 0xC7));
        private static readonly Brush VariesFill = Frozen(Color.FromRgb(0xFD, 0xE6, 0xD3));
        private static readonly Brush ChangedFill = Frozen(Color.FromRgb(0xE8, 0xF1, 0xFC));

        // ── model state (read in an API context) ──
        private ARDB.Document _doc;
        private string _docGuid;
        private List<CollectionDto> _collections = new List<CollectionDto>();
        private CollectionDto _c;
        private string _membersName, _overrideName;
        private CollectionStore.TargetIndex _index;
        private List<TargetRow> _targets = new List<TargetRow>();
        private List<OwnerRow> _owners = new List<OwnerRow>();
        private readonly Dictionary<long, List<long>> _instancesByOwner = new Dictionary<long, List<long>>();

        // ── editing state ──
        private OwnerRow _owner;
        private List<SlotRow> _saved = new List<SlotRow>();       // the owner's members as stored
        private List<SlotRow> _slots = new List<SlotRow>();       // draft
        private List<long> _instances = new List<long>();         // instances shown in the overrides grid (of _owner)
        private readonly Dictionary<long, Overrides> _savedOverrides = new Dictionary<long, Overrides>();
        private readonly Dictionary<long, Overrides> _draftOverrides = new Dictionary<long, Overrides>();
        private readonly Dictionary<long, string> _instanceNames = new Dictionary<long, string>();
        private readonly Dictionary<int, Dictionary<string, string>> _baseValues = new Dictionary<int, Dictionary<string, string>>();
        private readonly List<string> _extraFields = new List<string>();
        private List<string> _fieldChoices = new List<string>();
        private bool _loading;

        // ── controls ──
        private readonly ComboBox _collectionBox;
        private readonly TextBlock _collectionInfo;
        private readonly TextBox _ownerSearch, _targetSearch;
        private readonly ListBox _ownerList, _slotList, _targetList;
        private readonly TextBlock _ownerHeader, _ownerLock, _instanceHeader, _previewHeader, _status;
        private readonly TabControl _tabs;
        private readonly TabItem _membersTab, _instanceTab;
        private readonly StackPanel _memberButtons;
        private readonly Grid _overrideGrid;
        private readonly ComboBox _addField;
        private readonly StackPanel _overrideTools;
        private readonly StackPanel _previewRows;
        private readonly Button _apply, _revert;

        private readonly DispatcherTimer _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private UIApplication _app;

        private class OwnerRow
        {
            public long Id;
            public string Name, Family;
            public int Placed, Members, Stale, Broken;
            public bool Editable = true;
            public string LockedBy;
        }

        private class SlotRow
        {
            public MemberRef Ref;
            public int Origin;           // slot it had when read (0 = added in this edit), so overrides can follow it
            public Resolution Res;
            public SlotRow Copy() => new SlotRow { Ref = Ref, Origin = Origin, Res = Res };
        }

        private class TargetRow
        {
            public long Id;
            public string Name, Family;
        }

        // ── open / close ─────────────────────────────────────────────────────

        /// <summary>Opens the panel or brings it to the front. API context (a command).</summary>
        internal static void Open(UIApplication app)
        {
            if (Instance != null)
            {
                if (Instance.WindowState == WindowState.Minimized) Instance.WindowState = WindowState.Normal;
                Instance.Activate();
                return;
            }
            var w = new CollectionsWindow(app);
            new WindowInteropHelper(w).Owner = app.MainWindowHandle;
            app.SelectionChanged += w.OnSelectionChanged;
            w.Show();
            w.Load(app, null);
        }

        private CollectionsWindow(UIApplication app)
        {
            Instance = this;
            _app = app;
            Title = "Perseus — Collections";
            Width = 1180;
            Height = 700;
            MinWidth = 820;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Foreground = Ink;
            Background = Brushes.White;

            var root = new DockPanel { Margin = new Thickness(12) };

            // ── top: collection picker ──
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var refresh = MakeButton("Refresh", "Reload the collections from the server and re-read the model", (s, e) => Refresh());
            DockPanel.SetDock(refresh, Dock.Right);
            top.Children.Add(refresh);
            var lbl = new TextBlock { Text = "Collection", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(lbl, Dock.Left);
            top.Children.Add(lbl);
            _collectionBox = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            _collectionBox.SelectionChanged += (s, e) => { if (!_loading) ChangeCollection(); };
            DockPanel.SetDock(_collectionBox, Dock.Left);
            top.Children.Add(_collectionBox);
            _collectionInfo = new TextBlock { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            top.Children.Add(_collectionInfo);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            // ── bottom: status + apply ──
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            _apply = MakeButton("Apply", "Write the changes to the model (one undo step)", (s, e) => Apply());
            _apply.FontWeight = FontWeights.SemiBold;
            _revert = MakeButton("Revert", "Throw away the changes not applied yet", (s, e) => Revert());
            DockPanel.SetDock(_apply, Dock.Right);
            DockPanel.SetDock(_revert, Dock.Right);
            bottom.Children.Add(_apply);
            bottom.Children.Add(_revert);
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Muted };
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ── left: owners ──
            var left = new DockPanel();
            var ownersLabel = Hint("Owners (search)");
            DockPanel.SetDock(ownersLabel, Dock.Top);
            left.Children.Add(ownersLabel);
            _ownerSearch = new TextBox { Margin = new Thickness(0, 2, 0, 4) };
            _ownerSearch.TextChanged += (s, e) => FillOwners();
            DockPanel.SetDock(_ownerSearch, Dock.Top);
            left.Children.Add(_ownerSearch);
            var selectPlaced = MakeButton("Select placed in Revit", "Select the placed elements of this owner in the model", (s, e) => SelectPlaced());
            selectPlaced.Margin = new Thickness(0, 4, 0, 0);
            DockPanel.SetDock(selectPlaced, Dock.Bottom);
            left.Children.Add(selectPlaced);
            _ownerList = new ListBox { BorderBrush = LineBrush };
            ScrollViewer.SetHorizontalScrollBarVisibility(_ownerList, ScrollBarVisibility.Disabled);
            _ownerList.SelectionChanged += (s, e) => { if (!_loading) OnOwnerPicked(); };
            left.Children.Add(_ownerList);
            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            // ── right: tabs over preview ──
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            _tabs = new TabControl();
            _membersTab = new TabItem { Header = "Members" };
            _instanceTab = new TabItem { Header = "Instance overrides" };
            _tabs.Items.Add(_membersTab);
            _tabs.Items.Add(_instanceTab);
            _tabs.SelectionChanged += (s, e) => { if (e.Source == _tabs) SchedulePreview(); };
            right.Children.Add(_tabs);

            // Members tab
            var members = new DockPanel { Margin = new Thickness(8) };
            var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            _ownerHeader = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            _ownerLock = new TextBlock { Foreground = WarnBrush, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            headerStack.Children.Add(_ownerHeader);
            headerStack.Children.Add(_ownerLock);
            DockPanel.SetDock(headerStack, Dock.Top);
            members.Children.Add(headerStack);

            var memberGrid = new Grid();
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var slotsPanel = new DockPanel();
            var slotsLabel = Hint("Slots, in order");
            DockPanel.SetDock(slotsLabel, Dock.Top);
            slotsPanel.Children.Add(slotsLabel);
            _slotList = new ListBox { BorderBrush = LineBrush, SelectionMode = SelectionMode.Extended, Margin = new Thickness(0, 2, 0, 0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(_slotList, ScrollBarVisibility.Disabled);
            _slotList.KeyDown += (s, e) => { if (e.Key == Key.Delete) { RemoveSlots(); e.Handled = true; } };
            slotsPanel.Children.Add(_slotList);
            Grid.SetColumn(slotsPanel, 0);
            memberGrid.Children.Add(slotsPanel);

            _memberButtons = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            _memberButtons.Children.Add(MakeButton("◀ Add", "Add the selected target(s) after the selected slot (or at the end)", (s, e) => AddTargets(false)));
            _memberButtons.Children.Add(MakeButton("◀ Replace", "Put the selected target in the selected slot", (s, e) => AddTargets(true)));
            _memberButtons.Children.Add(MakeButton("Pick in model…", "Pick a placed element in Revit (its type, when members are types) and add it", (s, e) => PickInModel()));
            _memberButtons.Children.Add(Spacer());
            _memberButtons.Children.Add(MakeButton("Empty slot", "Insert an empty slot after the selected slot", (s, e) => InsertEmpty()));
            _memberButtons.Children.Add(MakeButton("Duplicate", "Repeat the selected slot(s) after them", (s, e) => DuplicateSlots()));
            _memberButtons.Children.Add(MakeButton("Move up", "Move the selected slot(s) up (Alt+Up)", (s, e) => MoveSlots(-1)));
            _memberButtons.Children.Add(MakeButton("Move down", "Move the selected slot(s) down (Alt+Down)", (s, e) => MoveSlots(1)));
            _memberButtons.Children.Add(MakeButton("Remove", "Remove the selected slot(s); later slots move up (Delete)", (s, e) => RemoveSlots()));
            _memberButtons.Children.Add(MakeButton("Clear slot", "Empty the selected slot(s), keeping the slot numbers of the others", (s, e) => ClearSlots()));
            _memberButtons.Children.Add(Spacer());
            _memberButtons.Children.Add(MakeButton("Fix selected", "Stale references: point at the element now holding the name, or keep the element and store its new name", (s, e) => FixStale(true)));
            _memberButtons.Children.Add(MakeButton("Fix all stale", "Fix every stale reference of this owner", (s, e) => FixStale(false)));
            foreach (var b in _memberButtons.Children.OfType<Button>()) { b.Margin = new Thickness(0, 0, 0, 4); b.MinWidth = 110; }
            Grid.SetColumn(_memberButtons, 2);
            memberGrid.Children.Add(_memberButtons);
            _slotList.PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Alt) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Up) { MoveSlots(-1); e.Handled = true; }
                else if (key == Key.Down) { MoveSlots(1); e.Handled = true; }
            };

            var targetsPanel = new DockPanel();
            var targetsLabel = Hint("Allowed members (search; double-click adds)");
            DockPanel.SetDock(targetsLabel, Dock.Top);
            targetsPanel.Children.Add(targetsLabel);
            _targetSearch = new TextBox { Margin = new Thickness(0, 2, 0, 4) };
            _targetSearch.TextChanged += (s, e) => FillTargets();
            DockPanel.SetDock(_targetSearch, Dock.Top);
            targetsPanel.Children.Add(_targetSearch);
            _targetList = new ListBox { BorderBrush = LineBrush, SelectionMode = SelectionMode.Extended };
            ScrollViewer.SetHorizontalScrollBarVisibility(_targetList, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_targetList, true);
            _targetList.MouseDoubleClick += (s, e) => AddTargets(false);
            targetsPanel.Children.Add(_targetList);
            Grid.SetColumn(targetsPanel, 4);
            memberGrid.Children.Add(targetsPanel);
            members.Children.Add(memberGrid);
            _membersTab.Content = members;

            // Instance overrides tab
            var inst = new DockPanel { Margin = new Thickness(8) };
            _instanceHeader = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(_instanceHeader, Dock.Top);
            inst.Children.Add(_instanceHeader);
            _overrideTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            _addField = new ComboBox { Width = 220, IsEditable = true, IsTextSearchEnabled = true, ToolTip = "A member parameter to show as a column (any parameter can be overridden in this collection)" };
            _overrideTools.Children.Add(new TextBlock { Text = "Add field", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _overrideTools.Children.Add(_addField);
            var addFieldButton = MakeButton("Add", "Show this field as a column", (s, e) => AddField());
            addFieldButton.Margin = new Thickness(6, 0, 0, 0);
            _overrideTools.Children.Add(addFieldButton);
            var resetAll = MakeButton("Reset all", "Remove every override of the selected instance(s)", (s, e) => ResetOverrides(null, null));
            resetAll.Margin = new Thickness(18, 0, 0, 0);
            _overrideTools.Children.Add(resetAll);
            DockPanel.SetDock(_overrideTools, Dock.Top);
            inst.Children.Add(_overrideTools);
            _overrideGrid = new Grid();
            inst.Children.Add(new ScrollViewer
            {
                Content = _overrideGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            _instanceTab.Content = inst;

            // Preview
            var preview = new DockPanel();
            _previewHeader = Hint("Preview");
            DockPanel.SetDock(_previewHeader, Dock.Top);
            preview.Children.Add(_previewHeader);
            _previewRows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            preview.Children.Add(new Border
            {
                BorderBrush = LineBrush, BorderThickness = new Thickness(1), Padding = new Thickness(6),
                Child = new ScrollViewer { Content = _previewRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
            Grid.SetRow(preview, 2);
            right.Children.Add(preview);

            root.Children.Add(body);
            Content = root;

            _previewTimer.Tick += (s, e) => { _previewTimer.Stop(); RunPreview(); };
            Closing += OnClosing;
            Closed += (s, e) =>
            {
                Instance = null;
                _previewTimer.Stop();
                OwnershipUiEvent.Run(a => { try { a.SelectionChanged -= OnSelectionChanged; } catch (Exception ex) { Log.Info($"[Collections] unsubscribe: {ex.Message}"); } });
                _app = null;
            };
            UpdateButtons();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!IsDirty) return;
            var r = MessageBox.Show(this, "Close without applying your changes?", "Perseus — Collections", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) e.Cancel = true;
        }

        // ── loading ──────────────────────────────────────────────────────────

        private void Refresh()
        {
            if (IsDirty && !Confirm("Refreshing throws away the changes not applied yet. Continue?")) return;
            string guid = _docGuid;
            string baseUrl = Config.Instance.BaseUrl;
            Status("Loading collections from the server…");
            Task.Run(() =>
            {
                try { if (guid != null) CollectionsCache.LoadNow(guid, baseUrl); }
                catch (Exception ex) { Log.Info($"[Collections] refresh: {ex.Message}"); }
                OwnershipUiEvent.Run(app => { if (Instance == this) Load(app, _c?.Name); });
            });
        }

        /// <summary>Reads the model for the chosen collection (API context). Keeps the selected owner when it still exists.</summary>
        private void Load(UIApplication app, string collectionName)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                _doc = doc;
                _docGuid = null;
                _collections = new List<CollectionDto>();
                if (doc != null && !doc.IsFamilyDocument)
                {
                    try { _docGuid = ModelGuidStorage.TryGet(doc); } catch { /* model not registered */ }
                    _collections = (_docGuid == null ? null : CollectionsCache.Get(_docGuid))?.Collections ?? new List<CollectionDto>();
                }
                _loading = true;
                _collectionBox.Items.Clear();
                foreach (var c in _collections) _collectionBox.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c, ToolTip = c.Description });
                var pick = _collections.FirstOrDefault(c => string.Equals(c.Name, collectionName ?? _c?.Name, StringComparison.OrdinalIgnoreCase))
                           ?? _collections.FirstOrDefault();
                _collectionBox.SelectedIndex = pick == null ? -1 : _collections.IndexOf(pick);
                _loading = false;
                LoadCollection(doc, pick, _owner?.Id);
                if (doc == null) Status("Open a model to edit its collections.", true);
                else if (_docGuid == null) Status("This model isn't registered with Perseus yet (sync it once).", true);
                else if (_collections.Count == 0) Status("No collections are set up for this model. Set them up on the web (Collections), then Refresh.", true);
                // Pick up what is selected in Revit right now.
                if (doc != null && _c != null) FollowSelection(doc, app.ActiveUIDocument.Selection.GetElementIds());
            }
            catch (Exception ex)
            {
                _loading = false;
                Log.Warn($"[Collections] load: {ex.Message}");
                Status("Couldn't read the model: " + ex.Message, true);
            }
        }

        private void ChangeCollection()
        {
            var c = (_collectionBox.SelectedItem as ComboBoxItem)?.Tag as CollectionDto;
            if (c == _c) return;
            if (IsDirty && !Confirm("Switching collection throws away the changes not applied yet. Continue?"))
            {
                _loading = true;
                _collectionBox.SelectedIndex = _collections.IndexOf(_c);
                _loading = false;
                return;
            }
            OwnershipUiEvent.Run(app => { if (Instance == this) LoadCollection(app.ActiveUIDocument?.Document, c, null); });
        }

        private void LoadCollection(ARDB.Document doc, CollectionDto c, long? keepOwner)
        {
            _c = c;
            _owners = new List<OwnerRow>();
            _targets = new List<TargetRow>();
            _instancesByOwner.Clear();
            _index = null;
            _extraFields.Clear();
            if (doc == null || c == null)
            {
                _collectionInfo.Text = "";
                FillOwners();
                FillTargets();
                SelectOwner(null);
                return;
            }
            _membersName = CollectionStore.MembersName(doc, c);
            _overrideName = CollectionStore.OverrideName(doc, c);
            _index = new CollectionStore.TargetIndex(doc, c);
            _targets = CollectionStore.Targets(doc, c)
                .Select(e => new TargetRow { Id = e.Id.GetIdValue(), Name = CollectionStore.NameOf(e), Family = CollectionStore.FamilyName(e) })
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            var owners = CollectionStore.Owners(doc, c);
            if (c.OnType)
            {
                // Placed instances per owner type (overrides live on them).
                var ownerIds = new HashSet<long>(owners.Select(o => o.Id.GetIdValue()));
                var col = new ARDB.FilteredElementCollector(doc).WhereElementIsNotElementType();
                var catIds = CategoryIds(doc, c.OwnerCategories);
                if (catIds.Count > 0) col = col.WherePasses(new ARDB.ElementMulticategoryFilter(catIds));
                foreach (var e in col)
                {
                    var t = e.GetTypeId();
                    if (t == null || t == ARDB.ElementId.InvalidElementId) continue;
                    long tid = t.GetIdValue();
                    if (!ownerIds.Contains(tid)) continue;
                    if (!_instancesByOwner.TryGetValue(tid, out var l)) _instancesByOwner[tid] = l = new List<long>();
                    l.Add(e.Id.GetIdValue());
                }
            }
            foreach (var o in owners)
            {
                long id = o.Id.GetIdValue();
                var refs = CollectionStore.ReadMembers(o, c, _membersName, out _);
                var row = new OwnerRow
                {
                    Id = id, Name = CollectionStore.NameOf(o), Family = CollectionStore.FamilyName(o),
                    Placed = c.OnType ? (_instancesByOwner.TryGetValue(id, out var l) ? l.Count : 0) : 1,
                };
                foreach (var r in refs)
                {
                    if (r.IsEmpty) continue;
                    row.Members++;
                    var res = _index.Resolve(r);
                    if (res.Status == RefStatus.Stale) row.Stale++;
                    else if (res.Status == RefStatus.Broken) row.Broken++;
                }
                _owners.Add(row);
            }
            _owners = _owners.OrderBy(o => o.Family, StringComparer.CurrentCultureIgnoreCase)
                             .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            string where = c.OnType ? "types" : "placed elements";
            _collectionInfo.Text = $"{_owners.Count} owner {where} · {_targets.Count} allowed members · " +
                                   (c.UsesSlots ? $"slots '{_membersName}'" : $"list '{_membersName}'") +
                                   (c.HasOverrides ? $" · overrides '{_overrideName}'" : " · no overrides") +
                                   (string.IsNullOrWhiteSpace(c.Description) ? "" : " — " + c.Description);
            FillOwners();
            FillTargets();
            var keep = keepOwner.HasValue ? _owners.FirstOrDefault(o => o.Id == keepOwner.Value) : null;
            ReadOwner(doc, keep);
        }

        private static List<ARDB.ElementId> CategoryIds(ARDB.Document doc, IList<string> names)
        {
            var wanted = new HashSet<string>(names ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var ids = new List<ARDB.ElementId>();
            if (wanted.Count == 0) return ids;
            foreach (ARDB.Category cat in doc.Settings.Categories)
                if (wanted.Contains(cat.Name)) ids.Add(cat.Id);
            return ids;
        }

        /// <summary>Reads one owner's members and lock state (API context) and shows it.</summary>
        private void ReadOwner(ARDB.Document doc, OwnerRow owner)
        {
            _owner = owner;
            _saved = new List<SlotRow>();
            if (owner != null && doc != null && _c != null)
            {
                var el = doc.GetElement(RevitExtensions.CreateId(owner.Id));
                var refs = CollectionStore.ReadMembers(el, _c, _membersName, out string err);
                for (int i = 0; i < refs.Count; i++)
                    _saved.Add(new SlotRow { Ref = refs[i], Origin = i + 1, Res = _index.Resolve(refs[i]) });
                owner.Editable = IdAssigner.CanEdit(doc, el.Id);
                owner.LockedBy = owner.Editable ? null : OwnerName(doc, el.Id);
                if (err != null) Status(err, true);
            }
            _slots = _saved.Select(s => s.Copy()).ToList();
            _loading = true;
            _ownerList.SelectedItem = _ownerList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == owner);
            _loading = false;
            ShowOwner();
            // The instance grid belongs to the owner: keep only instances of it.
            var keepInstances = owner == null ? new List<long>() : _instances.Where(id => InstancesOf(owner).Contains(id)).ToList();
            ReadInstances(doc, keepInstances);
        }

        private List<long> InstancesOf(OwnerRow o)
        {
            if (o == null) return new List<long>();
            if (_c != null && !_c.OnType) return new List<long> { o.Id };
            return _instancesByOwner.TryGetValue(o.Id, out var l) ? l : new List<long>();
        }

        private static string OwnerName(ARDB.Document doc, ARDB.ElementId id)
        {
            try
            {
                var info = ARDB.WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
                if (!string.IsNullOrEmpty(info?.Owner)) return info.Owner;
                var updates = ARDB.WorksharingUtils.GetModelUpdatesStatus(doc, id);
                if (updates == ARDB.ModelUpdatesStatus.UpdatedInCentral) return "a newer version in the central model (reload latest)";
            }
            catch (Exception ex) { Log.Info($"[Collections] owner of {id}: {ex.Message}"); }
            return "another user";
        }

        // ── owners list ──────────────────────────────────────────────────────

        private void FillOwners()
        {
            string q = (_ownerSearch.Text ?? "").Trim();
            _loading = true;
            _ownerList.Items.Clear();
            foreach (var o in _owners)
            {
                if (q.Length > 0 && o.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    o.Family.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _ownerList.Items.Add(new ListBoxItem { Content = OwnerItem(o), Tag = o, IsSelected = o == _owner });
            }
            _loading = false;
            if (_owners.Count == 0 && _c != null)
                _ownerList.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Content = new TextBlock
                    {
                        Text = $"Nothing in this model matches the owner filter of {_c.Name} ({(_c.OnType ? "types" : "instances")} of " +
                               $"{(_c.OwnerCategories.Count == 0 ? "any category" : string.Join(", ", _c.OwnerCategories))}" +
                               $"{(string.IsNullOrWhiteSpace(_c.OwnerFamily) ? "" : ", family matching '" + _c.OwnerFamily + "'")}).",
                        TextWrapping = TextWrapping.Wrap, Foreground = Muted,
                    },
                });
        }

        private UIElement OwnerItem(OwnerRow o)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var line = new DockPanel();
            if (o.Stale + o.Broken > 0)
            {
                var badge = new TextBlock
                {
                    Text = "⚠ " + (o.Stale + o.Broken), Foreground = o.Broken > 0 ? ErrorBrush : WarnBrush, Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = $"{o.Stale} stale, {o.Broken} broken reference(s)",
                };
                DockPanel.SetDock(badge, Dock.Right);
                line.Children.Add(badge);
            }
            line.Children.Add(new TextBlock { Text = o.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(line);
            string placed = _c != null && _c.OnType ? $" · {o.Placed} placed" : "";
            sp.Children.Add(new TextBlock
            {
                Text = $"{(string.IsNullOrEmpty(o.Family) ? "" : o.Family + " · ")}{o.Members} member{(o.Members == 1 ? "" : "s")}{placed}",
                Foreground = Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return sp;
        }

        private void OnOwnerPicked()
        {
            var o = (_ownerList.SelectedItem as ListBoxItem)?.Tag as OwnerRow;
            if (o == _owner) return;
            if (IsDirty && !Confirm("Switching owner throws away the changes not applied yet. Continue?"))
            {
                _loading = true;
                _ownerList.SelectedItem = _ownerList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag == _owner);
                _loading = false;
                return;
            }
            OwnershipUiEvent.Run(app => { if (Instance == this && DocOk(app)) ReadOwner(_doc, o); });
        }

        private void SelectOwner(OwnerRow o)
        {
            _owner = o;
            _saved = new List<SlotRow>();
            _slots = new List<SlotRow>();
            ShowOwner();
            ShowInstances();
        }

        private void SelectPlaced()
        {
            var o = _owner;
            if (o == null) return;
            var ids = InstancesOf(o);
            if (ids.Count == 0) { Status($"No {o.Name} placed in this model."); return; }
            OwnershipUiEvent.Run(app =>
            {
                if (!DocOk(app)) return;
                app.ActiveUIDocument.Selection.SetElementIds(ids.Select(RevitExtensions.CreateId).ToList());
                Status($"{ids.Count} {o.Name} selected.");
            });
        }

        // ── members tab ──────────────────────────────────────────────────────

        private bool MembersDirty =>
            _slots.Count != _saved.Count || _slots.Where((s, i) => !s.Ref.Equals(_saved[i].Ref) || s.Origin != _saved[i].Origin).Any();

        private bool OverridesDirty =>
            _draftOverrides.Any(kv => OverrideCodec.Serialize(kv.Value) != OverrideCodec.Serialize(_savedOverrides.TryGetValue(kv.Key, out var s) ? s : null));

        private bool IsDirty => MembersDirty || OverridesDirty;

        private bool CanEditOwner => _owner != null && _owner.Editable && _c != null;

        private void ShowOwner()
        {
            if (_owner == null)
            {
                _ownerHeader.Text = _c == null ? "" : "Select an owner on the left.";
                _ownerLock.Visibility = Visibility.Collapsed;
            }
            else
            {
                _ownerHeader.Text = _owner.Name + (string.IsNullOrEmpty(_owner.Family) ? "" : "  —  " + _owner.Family);
                _ownerLock.Text = _owner.Editable ? "" : $"Read only: checked out by {_owner.LockedBy}.";
                _ownerLock.Visibility = _owner.Editable ? Visibility.Collapsed : Visibility.Visible;
            }
            FillSlots(null);
        }

        private void FillSlots(IEnumerable<int> select)
        {
            var keep = new HashSet<int>(select ?? _slotList.SelectedItems.OfType<ListBoxItem>().Select(i => (int)i.Tag));
            _slotList.Items.Clear();
            for (int i = 0; i < _slots.Count; i++)
                _slotList.Items.Add(new ListBoxItem { Content = SlotItem(i), Tag = i, IsSelected = keep.Contains(i) });
            if (_owner != null && _slots.Count == 0)
                _slotList.Items.Add(new ListBoxItem
                {
                    IsEnabled = false,
                    Content = new TextBlock { Text = "No members yet. Add some from the list on the right.", Foreground = Muted },
                });
            UpdateButtons();
            SchedulePreview();
        }

        private UIElement SlotItem(int i)
        {
            var s = _slots[i];
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(Docked(new TextBlock { Text = (i + 1).ToString(), Width = 26, Foreground = Muted }, Dock.Left));
            string state; Brush brush; string tip;
            switch (s.Res?.Status ?? RefStatus.Empty)
            {
                case RefStatus.Ok: state = ""; brush = OkBrush; tip = $"Element {s.Ref.Id}"; break;
                case RefStatus.Stale:
                    state = s.Res.Fix == RefFix.AcceptRename ? "renamed" : "stale"; brush = WarnBrush;
                    tip = s.Res.Reason + (s.Res.Fix == RefFix.Repoint ? $"\nFix: point at element {s.Res.FixId}." : s.Res.Fix == RefFix.AcceptRename ? $"\nFix: store the new name '{s.Res.FixName}'." : "");
                    break;
                case RefStatus.Broken: state = "broken"; brush = ErrorBrush; tip = s.Res.Reason + "\nReplace it with an allowed member."; break;
                default: state = ""; brush = Muted; tip = "Empty slot (kept so later slots keep their numbers)"; break;
            }
            bool changed = i >= _saved.Count || !_saved[i].Ref.Equals(s.Ref) || _saved[i].Origin != s.Origin;
            if (changed) row.Children.Add(Docked(new TextBlock { Text = "●", Foreground = Brushes.SteelBlue, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Changed, not applied yet" }, Dock.Right));
            if (state.Length > 0)
                row.Children.Add(Docked(new TextBlock { Text = state, Foreground = brush, Margin = new Thickness(6, 0, 0, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, Dock.Right));
            row.Children.Add(new TextBlock
            {
                Text = s.Ref.IsEmpty ? "(empty)" : s.Ref.Name + (s.Ref.Id > 0 ? "" : "  (by name)"),
                Foreground = s.Ref.IsEmpty ? Muted : Ink, FontStyle = s.Ref.IsEmpty ? FontStyles.Italic : FontStyles.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.ToolTip = tip;
            return row;
        }

        private void FillTargets()
        {
            string q = (_targetSearch.Text ?? "").Trim();
            _targetList.Items.Clear();
            foreach (var t in _targets)
            {
                if (q.Length > 0 && t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (t.Family ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _targetList.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock
                    {
                        Inlines =
                        {
                            new System.Windows.Documents.Run(t.Name),
                            new System.Windows.Documents.Run(string.IsNullOrEmpty(t.Family) ? "" : "   " + t.Family) { Foreground = Muted, FontSize = 11 },
                        },
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    Tag = t,
                    ToolTip = $"Element {t.Id}",
                });
            }
        }

        private List<int> SelectedSlots() =>
            _slotList.SelectedItems.OfType<ListBoxItem>().Where(i => i.Tag is int).Select(i => (int)i.Tag).OrderBy(i => i).ToList();

        private bool EditGuard()
        {
            if (_owner == null) { Status("Select an owner on the left first.", true); return false; }
            if (!_owner.Editable) { Status($"{_owner.Name} is checked out by {_owner.LockedBy}: read only.", true); return false; }
            return true;
        }

        private SlotRow NewSlot(MemberRef r) => new SlotRow { Ref = r, Origin = 0, Res = _index?.Resolve(r) ?? new Resolution { Status = r.IsEmpty ? RefStatus.Empty : RefStatus.Ok } };

        private void AddTargets(bool replace)
        {
            if (!EditGuard()) return;
            var picked = _targetList.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag as TargetRow).Where(t => t != null).ToList();
            if (picked.Count == 0) { Status("Select an allowed member on the right first.", true); return; }
            var sel = SelectedSlots();
            if (replace)
            {
                if (sel.Count != 1) { Status("Select the one slot to replace.", true); return; }
                var old = _slots[sel[0]];
                _slots[sel[0]] = new SlotRow { Ref = new MemberRef(picked[0].Id, picked[0].Name), Origin = old.Origin };
                _slots[sel[0]].Res = _index.Resolve(_slots[sel[0]].Ref);
                FillSlots(sel);
                return;
            }
            int at = sel.Count > 0 ? sel.Last() + 1 : _slots.Count;
            var added = new List<int>();
            foreach (var t in picked)
            {
                _slots.Insert(at, NewSlot(new MemberRef(t.Id, t.Name)));
                added.Add(at++);
            }
            FillSlots(added);
        }

        private void PickInModel()
        {
            if (!EditGuard()) return;
            var c = _c;
            int at = SelectedSlots().DefaultIfEmpty(_slots.Count - 1).Max() + 1;
            OwnershipUiEvent.Run(app =>
            {
                if (Instance != this || !DocOk(app) || _c != c) return;
                ARDB.Element target = null;
                try
                {
                    var r = app.ActiveUIDocument.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element,
                        $"Pick a {(c.TargetsTypes ? "placed element of the type" : "element")} to add to {c.Name} (Esc to cancel)");
                    var el = _doc.GetElement(r?.ElementId);
                    target = c.TargetsTypes && !(el is ARDB.ElementType) ? _doc.GetElement(el?.GetTypeId() ?? ARDB.ElementId.InvalidElementId) : el;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { Status("Pick cancelled."); return; }
                finally { Activate(); }
                if (!CollectionStore.IsTarget(target, c))
                {
                    Status($"{CollectionStore.NameOf(target)} isn't an allowed member of {c.Name}" +
                           $" ({(c.TargetsTypes ? "types" : "placed elements")} of {(c.TargetCategories.Count == 0 ? "any category" : string.Join(", ", c.TargetCategories))}).", true);
                    return;
                }
                at = Math.Min(Math.Max(0, at), _slots.Count);
                _slots.Insert(at, NewSlot(CollectionStore.RefTo(target)));
                FillSlots(new[] { at });
                Status($"Added {CollectionStore.NameOf(target)} in slot {at + 1}.");
            });
        }

        private void InsertEmpty()
        {
            if (!EditGuard()) return;
            var sel = SelectedSlots();
            int at = sel.Count > 0 ? sel.Last() + 1 : _slots.Count;
            _slots.Insert(at, NewSlot(MemberRef.Empty));
            FillSlots(new[] { at });
            if (at == _slots.Count - 1) Status("An empty last slot isn't stored (trailing empty slots are dropped on Apply).");
        }

        private void DuplicateSlots()
        {
            if (!EditGuard()) return;
            var sel = SelectedSlots();
            if (sel.Count == 0) { Status("Select the slot(s) to duplicate.", true); return; }
            int at = sel.Last() + 1;
            var added = new List<int>();
            foreach (int i in sel)
            {
                _slots.Insert(at, NewSlot(_slots[i].Ref));
                added.Add(at++);
            }
            FillSlots(added);
        }

        private void MoveSlots(int dir)
        {
            if (!EditGuard()) return;
            var sel = SelectedSlots();
            if (sel.Count == 0) return;
            if ((dir < 0 && sel.First() == 0) || (dir > 0 && sel.Last() == _slots.Count - 1)) return;
            var order = dir < 0 ? sel : Enumerable.Reverse(sel).ToList();
            foreach (int i in order)
            {
                var s = _slots[i];
                _slots.RemoveAt(i);
                _slots.Insert(i + dir, s);
            }
            FillSlots(sel.Select(i => i + dir));
        }

        private void RemoveSlots()
        {
            if (!EditGuard()) return;
            var sel = SelectedSlots();
            if (sel.Count == 0) return;
            foreach (int i in Enumerable.Reverse(sel)) _slots.RemoveAt(i);
            FillSlots(new[] { Math.Min(sel.First(), _slots.Count - 1) });
        }

        private void ClearSlots()
        {
            if (!EditGuard()) return;
            var sel = SelectedSlots();
            foreach (int i in sel) _slots[i] = new SlotRow { Ref = MemberRef.Empty, Origin = 0, Res = new Resolution { Status = RefStatus.Empty } };
            FillSlots(sel);
        }

        private void FixStale(bool selectedOnly)
        {
            if (!EditGuard()) return;
            var which = selectedOnly ? SelectedSlots() : Enumerable.Range(0, _slots.Count).ToList();
            int fixedCount = 0, unfixable = 0;
            foreach (int i in which)
            {
                var s = _slots[i];
                if (s.Res == null || s.Res.Status == RefStatus.Ok || s.Res.Status == RefStatus.Empty) continue;
                if (s.Res.Fix == RefFix.None) { unfixable++; continue; }
                var r = s.Res.Fix == RefFix.Repoint ? new MemberRef(s.Res.FixId, s.Res.FixName) : new MemberRef(s.Ref.Id, s.Res.FixName);
                _slots[i] = new SlotRow { Ref = r, Origin = s.Origin, Res = _index.Resolve(r) };
                fixedCount++;
            }
            FillSlots(which.Where(i => i < _slots.Count));
            Status(fixedCount == 0 && unfixable == 0 ? "Nothing to fix." :
                   $"{fixedCount} reference(s) fixed (Apply to write)." + (unfixable > 0 ? $" {unfixable} broken: replace them with an allowed member." : ""),
                   unfixable > 0);
        }

        // ── instance overrides tab ───────────────────────────────────────────

        private void OnSelectionChanged(object sender, RevitSelectionChangedEventArgs e)
        {
            try
            {
                if (Instance != this || _c == null) return;
                var doc = e.GetDocument();
                if (doc == null || _doc == null || !doc.Equals(_doc)) return;
                FollowSelection(doc, e.GetSelectedElements());
            }
            catch (Exception ex) { Log.Info($"[Collections] selection: {ex.Message}"); }
        }

        /// <summary>Revit selection -> owner + instances shown in the overrides grid (API context).</summary>
        private void FollowSelection(ARDB.Document doc, ICollection<ARDB.ElementId> ids)
        {
            if (_c == null || ids == null) return;
            var byOwner = new Dictionary<long, List<long>>();
            OwnerRow ownerTypePicked = null;
            foreach (var id in ids)
            {
                var el = doc.GetElement(id);
                if (el == null) continue;
                long ownerId;
                if (_c.OnType)
                {
                    if (el is ARDB.ElementType)
                    {
                        ownerTypePicked = ownerTypePicked ?? _owners.FirstOrDefault(o => o.Id == id.GetIdValue());
                        continue;
                    }
                    var t = el.GetTypeId();
                    if (t == null || t == ARDB.ElementId.InvalidElementId) continue;
                    ownerId = t.GetIdValue();
                }
                else ownerId = id.GetIdValue();
                if (_owners.All(o => o.Id != ownerId)) continue;
                if (!byOwner.TryGetValue(ownerId, out var l)) byOwner[ownerId] = l = new List<long>();
                l.Add(id.GetIdValue());
            }
            if (byOwner.Count == 0)
            {
                if (ownerTypePicked != null && ownerTypePicked != _owner && !IsDirty) ReadOwner(doc, ownerTypePicked);
                return;
            }
            // Keep the current owner when it is among the selection, else the one with most selected.
            long pick = _owner != null && byOwner.ContainsKey(_owner.Id) ? _owner.Id : byOwner.OrderByDescending(kv => kv.Value.Count).First().Key;
            var owner = _owners.First(o => o.Id == pick);
            int others = byOwner.Where(kv => kv.Key != pick).Sum(kv => kv.Value.Count);
            if (owner != _owner)
            {
                if (IsDirty)
                {
                    Status($"Selected: {owner.Name}. Apply or revert the changes to {_owner?.Name} first.", true);
                    return;
                }
                ReadOwner(doc, owner);
            }
            if (OverridesDirty && !_instances.OrderBy(x => x).SequenceEqual(byOwner[pick].OrderBy(x => x)))
            {
                Status("Apply or revert the override changes before picking other instances.", true);
                return;
            }
            ReadInstances(doc, byOwner[pick]);
            if (others > 0) Status($"{others} selected element(s) of other types left out: the grid shows one type at a time.");
        }

        /// <summary>Reads overrides of these instances and the members' current values (API context).</summary>
        private void ReadInstances(ARDB.Document doc, List<long> ids)
        {
            _instances = ids ?? new List<long>();
            _savedOverrides.Clear();
            _draftOverrides.Clear();
            _instanceNames.Clear();
            _baseValues.Clear();
            _fieldChoices = new List<string>();
            if (doc != null && _c != null)
            {
                foreach (long id in _instances)
                {
                    var el = doc.GetElement(RevitExtensions.CreateId(id));
                    if (el == null) continue;
                    _instanceNames[id] = $"{CollectionStore.NameOf(el)} [{id}]";
                    _savedOverrides[id] = _c.HasOverrides ? CollectionStore.ReadOverrides(el, _overrideName, out _) : new Overrides();
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _saved.Count; i++)
                {
                    var res = _saved[i].Res;
                    if (res == null || res.ElementId <= 0) continue;
                    var m = doc.GetElement(RevitExtensions.CreateId(res.ElementId));
                    if (m == null) continue;
                    var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ARDB.Parameter p in m.Parameters)
                    {
                        string n = p.Definition?.Name;
                        if (string.IsNullOrEmpty(n) || vals.ContainsKey(n)) continue;
                        vals[n] = ValueText(p);
                        names.Add(n);
                    }
                    vals["Name"] = CollectionStore.NameOf(m);
                    _baseValues[i + 1] = vals;
                }
                _fieldChoices = names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            _addField.ItemsSource = _fieldChoices;
            ShowInstances();
        }

        private static string ValueText(ARDB.Parameter p)
        {
            try
            {
                if (!p.HasValue) return "";
                if (p.StorageType == ARDB.StorageType.String) return p.AsString() ?? "";
                return p.AsValueString() ?? "";
            }
            catch { return ""; }
        }

        private List<string> Fields()
        {
            var fields = new List<string>();
            if (_c == null) return fields;
            if (_c.OverridableFields != null && _c.OverridableFields.Count > 0) fields.AddRange(_c.OverridableFields);
            else
            {
                foreach (var o in _instances.Select(Effective))
                    foreach (int slot in o.Slots)
                        foreach (var f in o.For(slot).Keys)
                            if (!fields.Contains(f, StringComparer.OrdinalIgnoreCase)) fields.Add(f);
            }
            foreach (var f in _extraFields)
                if (!fields.Contains(f, StringComparer.OrdinalIgnoreCase)) fields.Add(f);
            return fields;
        }

        private Overrides Effective(long id) =>
            _draftOverrides.TryGetValue(id, out var d) ? d : _savedOverrides.TryGetValue(id, out var s) ? s : new Overrides();

        private void ShowInstances()
        {
            _overrideGrid.Children.Clear();
            _overrideGrid.RowDefinitions.Clear();
            _overrideGrid.ColumnDefinitions.Clear();
            _instanceTab.Header = _instances.Count > 0 ? $"Instance overrides ({_instances.Count})" : "Instance overrides";
            bool listed = _c?.OverridableFields != null && _c.OverridableFields.Count > 0;
            _overrideTools.Visibility = _c != null && _c.HasOverrides ? Visibility.Visible : Visibility.Collapsed;
            _addField.IsEnabled = !listed;
            if (_c == null) { _instanceHeader.Text = ""; UpdateButtons(); return; }
            if (!_c.HasOverrides)
            {
                _instanceHeader.Text = $"{_c.Name} has no override parameter, so instances can't change member values. Add one to the collection on the web to allow it.";
                UpdateButtons();
                return;
            }
            if (_instances.Count == 0)
            {
                _instanceHeader.Text = _owner == null
                    ? "Select placed owners in Revit to edit their overrides."
                    : $"Select placed {_owner.Name} in Revit to edit their overrides ({_owner.Placed} placed; \"Select placed in Revit\" selects them all).";
                UpdateButtons();
                return;
            }
            string who = _instances.Count == 1
                ? (_instanceNames.TryGetValue(_instances[0], out var n) ? n : _instances[0].ToString())
                : $"{_instances.Count} placed {_owner?.Name} (edits apply to all of them)";
            _instanceHeader.Text = $"Overrides of {who}. Yellow = overridden, orange = differs between the selected instances. " +
                                   "Type a value to override the member's; right-click to reset." +
                                   (MembersDirty ? "\nApply or revert the member changes first: overrides follow slot numbers." : "") +
                                   (listed ? "" : "\nAny member parameter can be overridden here: add its column with Add field.");

            var fields = Fields();
            _overrideGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var _ in fields) _overrideGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            _overrideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(new TextBlock { Text = "Slot / member", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 4) }, 0, 0);
            for (int f = 0; f < fields.Count; f++)
                AddCell(new TextBlock { Text = fields[f], FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 4), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = fields[f] }, 0, f + 1);
            if (fields.Count == 0)
                AddCell(new TextBlock { Text = "No fields yet: add one above.", Foreground = Muted, Margin = new Thickness(4, 0, 0, 4) }, 0, 1);

            bool editable = !MembersDirty && InstancesEditable(out _);
            int row = 1;
            for (int i = 0; i < _saved.Count; i++)
            {
                int slot = i + 1;
                var s = _saved[i];
                if (s.Ref.IsEmpty || s.Res == null || s.Res.ElementId <= 0) continue;
                _overrideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = $"{slot}  {s.Ref.Name}", Margin = new Thickness(0, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = s.Res.Status == RefStatus.Ok ? Ink : WarnBrush,
                };
                label.ContextMenu = Menu(("Reset this slot", () => ResetOverrides(slot, null)));
                AddCell(label, row, 0);
                for (int f = 0; f < fields.Count; f++)
                    AddCell(OverrideCell(slot, fields[f], editable), row, f + 1);
                row++;
            }
            UpdateButtons();
        }

        private bool InstancesEditable(out string lockedBy)
        {
            lockedBy = null;
            if (_doc == null) return false;
            foreach (long id in _instances)
            {
                var eid = RevitExtensions.CreateId(id);
                try
                {
                    if (!IdAssigner.CanEdit(_doc, eid)) { lockedBy = OwnerName(_doc, eid); return false; }
                }
                catch (Exception ex) { Log.Info($"[Collections] can edit {id}: {ex.Message}"); }
            }
            return true;
        }

        private void AddCell(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            _overrideGrid.Children.Add(e);
        }

        private UIElement OverrideCell(int slot, string field, bool editable)
        {
            string baseValue = _baseValues.TryGetValue(slot, out var vals) && vals.TryGetValue(field, out var bv) ? bv : null;
            var values = _instances.Select(id => Effective(id).TryGet(slot, field, out var v) ? v : null).ToList();
            bool varies = values.Distinct().Count() > 1;
            bool overridden = !varies && values.Count > 0 && values[0] != null;
            string shown = varies ? "" : overridden ? values[0] : baseValue ?? "";
            bool changed = _instances.Any(id => _draftOverrides.ContainsKey(id) &&
                                                (_draftOverrides[id].TryGet(slot, field, out var a) ? a : null) !=
                                                (_savedOverrides.TryGetValue(id, out var so) && so.TryGet(slot, field, out var b) ? b : null));
            var box = new TextBox
            {
                Text = shown, Margin = new Thickness(2), Padding = new Thickness(3, 2, 3, 2), IsReadOnly = !editable,
                Background = varies ? VariesFill : overridden ? OverriddenFill : Brushes.White,
                Foreground = overridden || varies ? Ink : Muted,
                BorderBrush = changed ? Brushes.SteelBlue : LineBrush,
                ToolTip = (baseValue == null ? $"The member has no '{field}' parameter." : $"Member value: {(baseValue.Length == 0 ? "(empty)" : baseValue)}") +
                          (varies ? "\nDiffers between the selected instances: typing sets it on all of them." : overridden ? "\nOverridden here." : ""),
            };
            if (baseValue == null && !overridden && !varies) box.FontStyle = FontStyles.Italic;
            // Restyle in place (rebuilding the grid would drop the focus Tab just moved to the next cell).
            void Commit()
            {
                if (box.IsReadOnly || box.Text == shown) return;
                foreach (long id in _instances)
                {
                    var d = Effective(id).Clone();
                    d.Set(slot, field, box.Text);
                    _draftOverrides[id] = d;
                }
                shown = box.Text;
                box.Background = OverriddenFill;
                box.Foreground = Ink;
                box.FontStyle = FontStyles.Normal;
                box.BorderBrush = Brushes.SteelBlue;
                UpdateButtons();
                SchedulePreview();
            }
            box.LostKeyboardFocus += (s, e) => Commit();
            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { box.Text = shown; e.Handled = true; }
            };
            box.ContextMenu = Menu(
                ("Reset to the member's value", () => ResetOverrides(slot, field)),
                ("Reset this slot", () => ResetOverrides(slot, null)),
                ("Reset all overrides", () => ResetOverrides(null, null)));
            return box;
        }

        private void ResetOverrides(int? slot, string field)
        {
            if (_instances.Count == 0) return;
            if (MembersDirty || !InstancesEditable(out _)) { Status("These overrides can't be edited right now (member changes pending or checked out by someone else).", true); return; }
            foreach (long id in _instances)
            {
                var d = Effective(id).Clone();
                if (slot == null) foreach (int s in d.Slots.ToList()) d.ClearSlot(s);
                else if (field == null) d.ClearSlot(slot.Value);
                else d.Clear(slot.Value, field);
                _draftOverrides[id] = d;
            }
            ShowInstances();
            SchedulePreview();
        }

        private void AddField()
        {
            string f = (_addField.Text ?? "").Trim();
            if (f.Length == 0) return;
            if (!_extraFields.Contains(f, StringComparer.OrdinalIgnoreCase)) _extraFields.Add(f);
            _addField.Text = "";
            ShowInstances();
        }

        // ── preview ──────────────────────────────────────────────────────────

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>Old slot -> new slot of the draft (null when the members didn't move).</summary>
        private Dictionary<int, int> SlotMap()
        {
            var map = new Dictionary<int, int>();
            bool moved = false;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Origin <= 0 || map.ContainsKey(_slots[i].Origin)) continue;
                map[_slots[i].Origin] = i + 1;
                if (_slots[i].Origin != i + 1) moved = true;
            }
            for (int s = 1; s <= _saved.Count; s++)
                if (!map.ContainsKey(s)) moved = true; // removed: its overrides go
            return moved ? map : null;
        }

        private void RunPreview()
        {
            var c = _c;
            var owner = _owner;
            if (c == null || owner == null)
            {
                _previewHeader.Text = "Preview";
                _previewRows.Children.Clear();
                return;
            }
            long previewId = _instances.Count > 0 ? _instances[0] : InstancesOf(owner).FirstOrDefault();
            var draft = MembersDirty ? _slots.Select(s => s.Ref).ToList() : null;
            var map = MembersDirty ? SlotMap() : null;
            var draftOverrides = _draftOverrides.TryGetValue(previewId, out var dov) ? dov.Clone() : null;
            OwnershipUiEvent.Run(app =>
            {
                if (Instance != this || _c != c || _owner != owner || !DocOk(app)) return;
                _previewRows.Children.Clear();
                var el = previewId > 0 ? _doc.GetElement(RevitExtensions.CreateId(previewId)) : null;
                if (el == null)
                {
                    _previewHeader.Text = "Preview";
                    _previewRows.Children.Add(new TextBlock { Text = $"Place a {owner.Name} to preview the values the Auto Parameters value rules write from {c.Name}.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
                    return;
                }
                var rules = _docGuid == null ? null : IdRulesCache.Get(_docGuid);
                _previewHeader.Text = $"Preview on {CollectionStore.NameOf(el)} [{previewId}]" + (draft != null || draftOverrides != null ? " — with your unsaved changes" : "");
                if (rules == null)
                {
                    _previewRows.Children.Add(new TextBlock { Text = "The Auto Parameters rules of this model aren't loaded (sync or run Audit once).", Foreground = Muted });
                    return;
                }
                List<IdAssigner.ValuePreview> rows;
                try
                {
                    rows = IdAssigner.PreviewCollectionValues(_doc, rules, el, r =>
                    {
                        if (draft != null) r.DraftMembers(c.Name, owner.Id, draft);
                        if (c.HasOverrides && (draft != null || draftOverrides != null))
                        {
                            var o = draftOverrides ?? CollectionStore.ReadOverrides(el, _overrideName, out _);
                            if (map != null) { o = o.Clone(); o.Remap(map); }
                            r.DraftOverrides(c.Name, previewId, o);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _previewRows.Children.Add(new TextBlock { Text = "Preview failed: " + ex.Message, Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap });
                    return;
                }
                if (rows.Count == 0)
                {
                    _previewRows.Children.Add(new TextBlock
                    {
                        Text = $"No value rule on this element reads {c.Name}. A rule's template uses it like {{% for s in {c.Name} %}}{{{{ s.Message }}}}{{% endfor %}}.",
                        Foreground = Muted, TextWrapping = TextWrapping.Wrap,
                    });
                    return;
                }
                foreach (var r in rows)
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                    line.Inlines.Add(new System.Windows.Documents.Run(r.Parameter + ":  ") { FontWeight = FontWeights.SemiBold });
                    line.Inlines.Add(new System.Windows.Documents.Run(r.Value.Length == 0 ? "(empty)" : r.Value));
                    if (r.Value != r.Current)
                        line.Inlines.Add(new System.Windows.Documents.Run("   now: " + (r.Current.Length == 0 ? "(empty)" : r.Current)) { Foreground = Muted, FontSize = 11 });
                    if (r.Error != null)
                        line.Inlines.Add(new System.Windows.Documents.Run("   " + r.Error) { Foreground = ErrorBrush, FontSize = 11 });
                    _previewRows.Children.Add(line);
                }
            });
        }

        // ── apply / revert ───────────────────────────────────────────────────

        private void Revert()
        {
            _slots = _saved.Select(s => s.Copy()).ToList();
            _draftOverrides.Clear();
            FillSlots(null);
            ShowInstances();
            Status("Changes thrown away.");
        }

        private void Apply()
        {
            if (!IsDirty || _c == null) return;
            var c = _c;
            var owner = _owner;
            bool membersDirty = MembersDirty;
            var members = ReferenceCodec.TrimTrailingEmpty(_slots.Select(s => s.Ref));
            var map = membersDirty ? SlotMap() : null;
            var drafts = _draftOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
            _apply.IsEnabled = false;
            OwnershipUiEvent.Run(app =>
            {
                try
                {
                    if (Instance != this || !DocOk(app)) return;
                    string msg = Write(c, owner, membersDirty, members, map, drafts, out bool written);
                    // Re-read what the model holds now; a failed apply keeps the edits for another try.
                    if (written) LoadCollection(_doc, c, owner?.Id);
                    Status(msg, !written);
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Collections] apply: {ex.Message}");
                    Status("Couldn't apply: " + ex.Message, true);
                }
                finally { UpdateButtons(); }
            });
        }

        /// <summary>Writes members + overrides in one transaction (API context). Returns what happened.</summary>
        private string Write(CollectionDto c, OwnerRow owner, bool membersDirty, List<MemberRef> members, Dictionary<int, int> map,
                             Dictionary<long, Overrides> drafts, out bool written)
        {
            written = false;
            var doc = _doc;
            if (doc.IsReadOnly) return "Couldn't apply: the model is read only.";
            var ownerEl = owner == null ? null : doc.GetElement(RevitExtensions.CreateId(owner.Id));
            if (membersDirty && ownerEl == null) return "Couldn't apply: the owner no longer exists. Refresh.";
            if (membersDirty && !IdAssigner.CanEdit(doc, ownerEl.Id))
                return $"Couldn't apply: {owner.Name} is checked out by {OwnerName(doc, ownerEl.Id)}.";

            // Overrides to write: edited ones, and (members moved / removed) every instance of the owner whose overrides follow.
            var overrides = new Dictionary<long, Overrides>(drafts);
            if (membersDirty && map != null && c.HasOverrides)
            {
                foreach (long id in InstancesOf(owner))
                {
                    var el = doc.GetElement(RevitExtensions.CreateId(id));
                    var o = overrides.TryGetValue(id, out var d) ? d : CollectionStore.ReadOverrides(el, _overrideName, out _);
                    if (o.IsEmpty) continue;
                    o = o.Clone();
                    o.Remap(map);
                    overrides[id] = o;
                }
            }
            else if (membersDirty && c.HasOverrides)
            {
                // Only appended / renamed members: drop overrides of slots past the end (emptied trailing slots).
                foreach (long id in InstancesOf(owner))
                {
                    var el = doc.GetElement(RevitExtensions.CreateId(id));
                    var o = overrides.TryGetValue(id, out var d) ? d.Clone() : CollectionStore.ReadOverrides(el, _overrideName, out _);
                    if (o.PruneAbove(members.Count) > 0 || overrides.ContainsKey(id)) overrides[id] = o;
                }
            }
            // Leave out instances whose overrides end up as they are now.
            foreach (var id in overrides.Keys.ToList())
            {
                var el = doc.GetElement(RevitExtensions.CreateId(id));
                if (el == null || OverrideCodec.Serialize(CollectionStore.ReadOverrides(el, _overrideName, out _)) == OverrideCodec.Serialize(overrides[id]))
                    overrides.Remove(id);
            }
            var locked = overrides.Keys.Where(id => !IdAssigner.CanEdit(doc, RevitExtensions.CreateId(id))).ToList();
            if (locked.Count > 0)
            {
                var edited = locked.Where(drafts.ContainsKey).ToList();
                string who = OwnerName(doc, RevitExtensions.CreateId(edited.Count > 0 ? edited[0] : locked[0]));
                if (edited.Count > 0)
                    return $"Couldn't apply: {edited.Count} of the instances you edited are checked out by {who}.";
                var r = MessageBox.Show(this,
                    $"{locked.Count} placed {owner?.Name} with overrides are checked out by {who}. Their overrides can't follow the reordered " +
                    "members now and will point at the wrong slots until they are fixed.\n\nApply anyway?",
                    "Perseus — Collections", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return "Nothing applied.";
                foreach (var id in locked) overrides.Remove(id);
            }
            if (!membersDirty && overrides.Count == 0) return "Nothing to apply.";

            // Parameters first (outside the transaction): more slots, or a missing override parameter.
            bool needParams = false;
            if (membersDirty)
            {
                needParams = c.UsesSlots ? CollectionStore.SlotCapacity(ownerEl, c, _membersName) < members.Count
                                         : ownerEl.LookupParameter(_membersName) == null;
            }
            if (!needParams && overrides.Count > 0)
                needParams = overrides.Keys.Any(id => doc.GetElement(RevitExtensions.CreateId(id))?.LookupParameter(_overrideName) == null);
            if (needParams)
            {
                CollectionStore.EnsureParameters(doc, c, members.Count);
                _membersName = CollectionStore.MembersName(doc, c);
                _overrideName = CollectionStore.OverrideName(doc, c);
            }

            var errors = new List<string>();
            using (var tx = new ARDB.Transaction(doc, TransactionName))
            {
                tx.Start();
                if (membersDirty)
                {
                    string err = CollectionStore.WriteMembers(doc.GetElement(ownerEl.Id), c, _membersName, members);
                    if (err != null) errors.Add(err);
                }
                if (errors.Count == 0)
                    foreach (var kv in overrides)
                    {
                        string err = CollectionStore.WriteOverrides(doc.GetElement(RevitExtensions.CreateId(kv.Key)), _overrideName, kv.Value);
                        if (err != null) errors.Add(err);
                    }
                if (errors.Count > 0)
                {
                    tx.RollBack();
                    Log.Warn($"[Collections] apply rolled back: {string.Join("; ", errors)}");
                    return "Couldn't apply: " + errors[0];
                }
                var status = tx.Commit();
                if (status != ARDB.TransactionStatus.Committed) return $"Couldn't apply: Revit didn't commit ({status}).";
            }
            written = true;
            _draftOverrides.Clear();
            var parts = new List<string>();
            if (membersDirty) parts.Add($"{owner.Name}: {members.Count(m => !m.IsEmpty)} member(s) written");
            if (overrides.Count > 0) parts.Add($"overrides written on {overrides.Count} instance(s)");
            Log.Info($"[Collections] {c.Name}: {string.Join(", ", parts)}.");
            return string.Join(", ", parts) + ". Value rules reading the collection refresh automatically.";
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private bool DocOk(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc != null && _doc != null && doc.Equals(_doc) && _doc.IsValidObject) return true;
            Status("The active model changed: reloading.");
            Load(app, _c?.Name);
            return false;
        }

        private void UpdateButtons()
        {
            bool dirty = IsDirty;
            _apply.IsEnabled = dirty;
            _revert.IsEnabled = dirty;
            foreach (var b in _memberButtons.Children.OfType<Button>()) b.IsEnabled = CanEditOwner;
            if (dirty && !_status.Text.StartsWith("Couldn't", StringComparison.Ordinal))
            {
                int moved = MembersDirty ? 1 : 0;
                int inst = _draftOverrides.Count;
                _status.Foreground = Muted;
                _status.Text = (moved > 0 ? "Member changes" : "") + (moved > 0 && inst > 0 ? " and " : "") +
                               (inst > 0 ? $"override changes on {inst} instance(s)" : "") + " not applied yet.";
            }
        }

        private void Status(string text, bool error = false)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? ErrorBrush : Muted;
        }

        private bool Confirm(string text) =>
            MessageBox.Show(this, text, "Perseus — Collections", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        private static ContextMenu Menu(params (string Text, Action Do)[] items)
        {
            var m = new ContextMenu();
            foreach (var it in items)
            {
                var mi = new MenuItem { Header = it.Text };
                var act = it.Do;
                mi.Click += (s, e) => act();
                m.Items.Add(mi);
            }
            return m;
        }

        private static Button MakeButton(string text, string tip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
            b.Click += click;
            return b;
        }

        private static UIElement Spacer() => new Border { Height = 8 };

        private static TextBlock Hint(string text) => new TextBlock { Text = text, Foreground = Muted, FontSize = 11 };

        private static T Docked<T>(T e, Dock d) where T : UIElement
        {
            DockPanel.SetDock(e, d);
            return e;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
