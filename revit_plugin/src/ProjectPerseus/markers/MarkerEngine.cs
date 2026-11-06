using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Element = Autodesk.Revit.DB.Element;
using StorageType = Autodesk.Revit.DB.StorageType;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.queue;
using ProjectPerseus.revit;
using ProjectPerseus.ui;
using ProjectPerseus.util;

namespace ProjectPerseus.markers
{
    /// <summary>
    /// Evaluates the enabled marker sets (MarkerSetStore) against the elements visible in the active view and keeps
    /// the result for the overlay: per parameter read by the set's entries, the first entry the element's value
    /// matches gives a mark (ring + icon, or icon only) - so one element can carry several marks. Runs on Idling, debounced, only when something changed (view switch,
    /// model edit, sets edited) — never per pan/zoom: the view-scoped collector already gives everything in the view,
    /// and queue/ViolationScreenPositionEvent re-projects the cached hits each tick.
    ///
    /// Linked models: for each link instance visible in the view, the link's elements are read from the link document
    /// and their boxes transformed into host coordinates (RevitLinkInstance.GetTotalTransform). Revit 2024+ collects
    /// exactly the linked elements visible in the host view; 2023 has no such collector, so it takes the link's whole
    /// category and keeps those inside the view's depth (plan view range / section depth). Links nested inside links
    /// are not included. Hits are keyed per model ("h:id" host, "l<instance>:id" link) since ids repeat across models.
    /// </summary>
    internal static class MarkerEngine
    {
        private const int MaxHits = 4000;
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

        internal struct Hit
        {
            internal string Key;     // HostKey(id) for host elements, "l<link instance id>:<id>" for linked ones
            internal BoundingBoxXYZ BBox;
            internal byte R, G, B;
            internal string Icon;
            internal bool IconOnly; // no ring: icon (or dot) on the element's centre
            internal int Ring;      // ring marks: 0 = innermost ring on this element
            internal int Slot;      // icon-only marks: 0 = first icon on this element
        }

        /// <summary>What one set found in the last evaluation (for the panel's status line).</summary>
        internal class SetResult
        {
            internal string SetId;
            internal int Considered;   // elements of the set's categories in the view
            internal int Matched;      // ... that passed the filter
            internal int Marked;       // ... whose value matched an entry
            internal int MarkedInLinks;// ... of which in linked models
            internal int ParamMissing; // matched elements with no such parameter / pseudo field
            internal int[] EntryCounts = new int[0];
            internal string Error;
        }

        private static readonly object _lock = new object();
        private static List<Hit> _hits = new List<Hit>();
        private static Dictionary<string, SetResult> _results = new Dictionary<string, SetResult>();
        private static bool _dirty = true;
        private static DateTime _dirtyAt = DateTime.MinValue;
        private static DateTime _lastRun = DateTime.MinValue;
        private static TimeSpan _minGap = TimeSpan.Zero;
        private static string _lastViewNote;

        /// <summary>Raised on the main thread after each evaluation.</summary>
        internal static event Action Evaluated;

        internal static bool HasHits { get { lock (_lock) return _hits.Count > 0; } }
        internal static string ViewNote => _lastViewNote;

        internal static string HostKey(ElementId id) => "h:" + id.GetIdValue();

        internal static List<Hit> GetHitsSnapshot() { lock (_lock) return new List<Hit>(_hits); }

        internal static SetResult GetResult(string setId)
        {
            lock (_lock) return setId != null && _results.TryGetValue(setId, out var r) ? r : null;
        }

        internal static void Subscribe(UIControlledApplication app)
        {
            app.Idling += OnIdling;
            app.ViewActivated += OnViewActivated;
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            app.ControlledApplication.DocumentClosed += OnDocumentClosed;
            MarkerSetStore.Changed += MarkDirty;
        }

        internal static void Unsubscribe(UIControlledApplication app)
        {
            app.Idling -= OnIdling;
            app.ViewActivated -= OnViewActivated;
            app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            app.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            MarkerSetStore.Changed -= MarkDirty;
        }

        internal static void MarkDirty()
        {
            _dirty = true;
            _dirtyAt = DateTime.UtcNow;
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e) => MarkDirty();
        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e) => MarkDirty();
        private static void OnDocumentClosed(object sender, DocumentClosedEventArgs e) => MarkDirty();

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            if (!_dirty || now - _dirtyAt < Debounce || now - _lastRun < _minGap) return;
            _dirty = false;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Evaluate((sender as UIApplication)?.ActiveUIDocument);
                long ms = clock.ElapsedMilliseconds;
                _lastRun = DateTime.UtcNow;
                // A slow evaluation (big view, heavy filter) waits proportionally longer before the next one, so
                // editing stays responsive: 200 ms -> at least 1 s apart.
                _minGap = TimeSpan.FromMilliseconds(Math.Min(10000, ms * 5));
                if (ms > 300) Log.Info($"[MarkerEngine] evaluation took {ms} ms; next no sooner than {_minGap.TotalSeconds:0.0} s");
            }
            catch (Exception ex)
            {
                Log.Warn($"[MarkerEngine] evaluation failed: {ex.Message}");
                Publish(new List<Hit>(), new Dictionary<string, SetResult>(), null);
            }
        }

        // ── Evaluation ──────────────────────────────────────────────────────────

        private static void Evaluate(UIDocument uidoc)
        {
            var hits = new List<Hit>();
            var results = new Dictionary<string, SetResult>();
            var sets = MarkerSetStore.Sets.Where(s => s.Enabled).ToList();

            Document doc = uidoc?.Document;
            View view = uidoc?.ActiveView;
            string note = null;
            if (!MarkerSetStore.ShowMarkers) note = "Markers are switched off.";
            else if (doc == null || view == null) note = "No model open.";
            else if (doc.IsFamilyDocument) note = "Markers aren't shown in the family editor.";
            else if (!ViolationScreenPositionEvent.IsProjectableView(view.ViewType))
                note = "Markers show in plans, sections, elevations and detail views (not 3D, sheets or schedules).";

            if (note == null)
            {
                var rings = new Dictionary<string, int>();
                var slots = new Dictionary<string, int>();
                var links = sets.Any(s => s.IncludeLinks) ? VisibleLinks(doc, view) : new List<LinkSource>();
                foreach (var set in sets)
                {
                    var r = EvaluateSet(doc, view, set, links, rings, slots, hits);
                    results[set.Id] = r;
                    if (r.Error != null) Log.Info($"[MarkerEngine] set '{set.Name}': {r.Error}");
                }
                if (hits.Count >= MaxHits) note = $"Showing the first {MaxHits:N0} markers in this view.";
            }
            Publish(hits, results, note);
        }

        private static SetResult EvaluateSet(Document doc, View view, MarkerSet set, List<LinkSource> links,
                                             Dictionary<string, int> rings, Dictionary<string, int> slots, List<Hit> hits)
        {
            var r = new SetResult { SetId = set.Id, EntryCounts = new int[set.Entries.Count] };
            var cats = ParseCategories(set.Categories);
            if (cats.Count == 0) { r.Error = "Pick at least one category."; return r; }
            if (!set.Entries.Any(x => x.Enabled)) { r.Error = "Add a legend entry."; return r; }

            var matchers = BuildMatchers(set, out string regexError);
            if (regexError != null) { r.Error = regexError; return r; }

            // Entries grouped by the parameter they read, in legend order: within a group the first match wins,
            // groups mark independently (mag lock from one parameter and drop bolt from another on the same door).
            var groups = new List<KeyValuePair<string, List<int>>>();
            for (int i = 0; i < set.Entries.Count; i++)
            {
                if (matchers[i] == null) continue;
                string p = set.ParameterFor(set.Entries[i]);
                var g = groups.FirstOrDefault(x => string.Equals(x.Key, p, StringComparison.OrdinalIgnoreCase));
                if (g.Value == null) { g = new KeyValuePair<string, List<int>>(p, new List<int>()); groups.Add(g); }
                g.Value.Add(i);
            }
            if (groups.Any(g => g.Key.Length == 0)) { r.Error = "Choose the parameter to read."; return r; }
            string defaultParam = (set.Parameter ?? "").Trim();

            var colors = set.Entries.Select(x => ParseColor(x.Color)).ToArray();
            bool hasFilter = !string.IsNullOrWhiteSpace(set.Filter);
            var matched = new List<int>();

            var catFilter = new ElementMulticategoryFilter(cats);

            // False = stop the whole set (filter error, reported in r.Error).
            bool Process(Document src, Element el, Transform toHost, string keyPrefix)
            {
                r.Considered++;
                Dictionary<string, string> row = null;
                if (hasFilter)
                {
                    row = BuildRow(src, el);
                    if (!FilterEvaluator.TryEvaluate(set.Filter, row, out bool pass, out string err))
                    {
                        r.Error = "Filter: " + err;
                        return false;
                    }
                    if (!pass) return true;
                }
                r.Matched++;

                matched.Clear();
                bool missing = false;
                foreach (var g in groups)
                {
                    string value = ReadValue(src, el, g.Key, row, out bool found);
                    if (!found && (defaultParam.Length == 0 || string.Equals(g.Key, defaultParam, StringComparison.OrdinalIgnoreCase)))
                        missing = true;
                    foreach (int i in g.Value)
                        if (matchers[i](value)) { matched.Add(i); break; }
                }
                if (missing) r.ParamMissing++;
                if (matched.Count == 0) return true;

                BoundingBoxXYZ bb = el.get_BoundingBox(null);
                if (bb == null) return true;
                if (toHost != null) bb = TransformBox(bb, toHost);

                r.Marked++;
                if (toHost != null) r.MarkedInLinks++;
                string key = keyPrefix + el.Id.GetIdValue();
                matched.Sort(); // legend order: earlier entries get the inner ring / first icon
                foreach (int entry in matched)
                {
                    r.EntryCounts[entry]++;
                    var e = set.Entries[entry];
                    var c = colors[entry];
                    var hit = new Hit { Key = key, BBox = bb, R = c.R, G = c.G, B = c.B, Icon = e.Icon, IconOnly = !e.ShowRing };
                    var counter = hit.IconOnly ? slots : rings;
                    counter.TryGetValue(key, out int n);
                    counter[key] = n + 1;
                    if (hit.IconOnly) hit.Slot = n; else hit.Ring = n;
                    hits.Add(hit);
                }
                return true;
            }

            var host = new FilteredElementCollector(doc, view.Id).WherePasses(catFilter).WhereElementIsNotElementType();
            foreach (Element el in host)
            {
                if (hits.Count >= MaxHits) return r;
                if (!Process(doc, el, null, "h:")) return r;
            }

            if (set.IncludeLinks)
            {
                foreach (var link in links)
                {
                    IEnumerable<Element> linked;
                    try { linked = LinkedElementsInView(doc, view, link, catFilter); }
                    catch (Exception ex)
                    {
                        Log.Info($"[MarkerEngine] link '{link.Doc.Title}' skipped: {ex.Message}");
                        continue;
                    }
                    foreach (Element el in linked)
                    {
                        if (hits.Count >= MaxHits) return r;
                        if (!Process(link.Doc, el, link.ToHost, link.KeyPrefix)) return r;
                    }
                }
            }
            return r;
        }

        // ── Linked models ───────────────────────────────────────────────────────

        internal class LinkSource
        {
            internal RevitLinkInstance Instance;
            internal Document Doc;
            internal Transform ToHost;
            internal string KeyPrefix;
        }

        // Loaded link instances visible in the view (hidden links / link category off are excluded by the
        // view-scoped collector).
        private static List<LinkSource> VisibleLinks(Document doc, View view)
        {
            var list = new List<LinkSource>();
            foreach (RevitLinkInstance inst in new FilteredElementCollector(doc, view.Id).OfClass(typeof(RevitLinkInstance)))
            {
                try
                {
                    Document linkDoc = inst.GetLinkDocument();
                    if (linkDoc == null) continue; // unloaded
                    list.Add(new LinkSource
                    {
                        Instance = inst,
                        Doc = linkDoc,
                        ToHost = inst.GetTotalTransform(),
                        KeyPrefix = "l" + inst.Id.GetIdValue() + ":",
                    });
                }
                catch (Exception ex) { Log.Info($"[MarkerEngine] link instance {inst.Id.GetIdValue()} skipped: {ex.Message}"); }
            }
            return list;
        }

        private static IEnumerable<Element> LinkedElementsInView(Document hostDoc, View view, LinkSource link, ElementFilter catFilter)
        {
#if REVIT2024_OR_GREATER
            // Exactly the linked elements visible in the host view (view range, crop, link visibility overrides).
            return new FilteredElementCollector(hostDoc, view.Id, link.Instance.Id)
                .WherePasses(catFilter)
                .WhereElementIsNotElementType()
                .ToElements();
#else
            // Revit 2023: no host-view collector for links. Take the category and keep what lies in the view's depth.
            var depth = ViewDepth.For(view);
            return new FilteredElementCollector(link.Doc)
                .WherePasses(catFilter)
                .WhereElementIsNotElementType()
                .Where(el =>
                {
                    var bb = el.get_BoundingBox(null);
                    return bb != null && depth.Contains(TransformBox(bb, link.ToHost));
                })
                .ToList();
#endif
        }

        // Axis-aligned box (host coordinates) around a box given in another model's coordinates.
        private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ bb, Transform toHost)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            var own = bb.Transform;
            for (int i = 0; i < 8; i++)
            {
                var p = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                if (own != null) p = own.OfPoint(p);
                p = toHost.OfPoint(p);
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return new BoundingBoxXYZ { Min = new XYZ(x0, y0, z0), Max = new XYZ(x1, y1, z1) };
        }

#if !REVIT2024_OR_GREATER
        // Revit 2023 fallback: does a host-coordinate box overlap the view's depth (plan cut range / section depth)?
        private class ViewDepth
        {
            private enum Kind { None, PlanZ, Section }
            private Kind _kind;
            private double _min, _max;
            private XYZ _axis, _origin;

            internal static ViewDepth For(View view)
            {
                var d = new ViewDepth { _kind = Kind.None };
                try
                {
                    if (view is ViewPlan plan && plan.GenLevel != null)
                    {
                        var vr = plan.GetViewRange();
                        double lz = plan.GenLevel.Elevation;
                        double a = lz + vr.GetOffset(PlanViewPlane.TopClipPlane);
                        double b = lz + vr.GetOffset(PlanViewPlane.BottomClipPlane);
                        d._kind = Kind.PlanZ; d._min = Math.Min(a, b); d._max = Math.Max(a, b);
                    }
                    else if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation)
                    {
                        var cb = view.CropBox;
                        d._kind = Kind.Section;
                        d._axis = cb.Transform.BasisZ; d._origin = cb.Transform.Origin;
                        d._min = Math.Min(cb.Min.Z, cb.Max.Z); d._max = Math.Max(cb.Min.Z, cb.Max.Z);
                    }
                }
                catch { d._kind = Kind.None; }
                return d;
            }

            internal bool Contains(BoundingBoxXYZ bb)
            {
                switch (_kind)
                {
                    case Kind.PlanZ:
                        return bb.Max.Z >= _min && bb.Min.Z <= _max;
                    case Kind.Section:
                        double lo = double.MaxValue, hi = double.MinValue;
                        for (int i = 0; i < 8; i++)
                        {
                            var p = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                            (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                            (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                            double t = p.Subtract(_origin).DotProduct(_axis);
                            lo = Math.Min(lo, t); hi = Math.Max(hi, t);
                        }
                        return hi >= _min && lo <= _max;
                    default:
                        return true;
                }
            }
        }
#endif

        private static void Publish(List<Hit> hits, Dictionary<string, SetResult> results, string note)
        {
            lock (_lock)
            {
                _hits = hits;
                _results = results;
            }
            _lastViewNote = note;
            ViolationOverlayController.UpdateLegend(BuildLegend(results, note == null));
            try { Evaluated?.Invoke(); }
            catch (Exception ex) { Log.Warn($"[MarkerEngine] Evaluated handler failed: {ex.Message}"); }
        }

        private static List<OverlayLegendRow> BuildLegend(Dictionary<string, SetResult> results, bool viewOk)
        {
            if (!viewOk || !MarkerSetStore.ShowMarkers || !MarkerSetStore.ShowLegend) return null;
            var rows = new List<OverlayLegendRow>();
            foreach (var set in MarkerSetStore.Sets.Where(s => s.Enabled))
            {
                if (!results.TryGetValue(set.Id, out var r) || r.Error != null) continue;
                rows.Add(new OverlayLegendRow { IsHeader = true, Label = set.Name });
                for (int i = 0; i < set.Entries.Count; i++)
                {
                    var e = set.Entries[i];
                    if (!e.Enabled) continue;
                    var c = ParseColor(e.Color);
                    rows.Add(new OverlayLegendRow
                    {
                        Label = string.IsNullOrWhiteSpace(e.Label) ? DescribeMatch(e) : e.Label,
                        Count = i < r.EntryCounts.Length ? r.EntryCounts[i] : 0,
                        R = c.R, G = c.G, B = c.B,
                        Icon = e.Icon,
                        IconOnly = !e.ShowRing,
                    });
                }
            }
            return rows.Count > 0 ? rows : null;
        }

        internal static string DescribeMatch(MarkerSet.Entry e)
        {
            switch (e.Match)
            {
                case MarkerSet.MatchKind.AnyValue: return "(any value)";
                case MarkerSet.MatchKind.Empty:    return "(empty)";
                case MarkerSet.MatchKind.Contains: return $"contains \"{e.Value}\"";
                case MarkerSet.MatchKind.Regex:    return $"/{e.Value}/";
                default:                           return string.IsNullOrEmpty(e.Value) ? "(blank)" : e.Value;
            }
        }

        // One matcher per entry (null = disabled entry). Regexes compiled once per evaluation.
        private static Func<string, bool>[] BuildMatchers(MarkerSet set, out string error)
        {
            error = null;
            var result = new Func<string, bool>[set.Entries.Count];
            for (int i = 0; i < set.Entries.Count; i++)
            {
                var e = set.Entries[i];
                if (!e.Enabled) continue;
                string want = (e.Value ?? "").Trim();
                switch (e.Match)
                {
                    case MarkerSet.MatchKind.Equals:
                        result[i] = v => string.Equals(v.Trim(), want, StringComparison.OrdinalIgnoreCase);
                        break;
                    case MarkerSet.MatchKind.Contains:
                        result[i] = v => v.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
                        break;
                    case MarkerSet.MatchKind.AnyValue:
                        result[i] = v => !string.IsNullOrWhiteSpace(v);
                        break;
                    case MarkerSet.MatchKind.Empty:
                        result[i] = v => string.IsNullOrWhiteSpace(v);
                        break;
                    case MarkerSet.MatchKind.Regex:
                        try
                        {
                            var rx = new Regex(e.Value ?? "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                                               TimeSpan.FromMilliseconds(50));
                            result[i] = v => { try { return rx.IsMatch(v); } catch (RegexMatchTimeoutException) { return false; } };
                        }
                        catch (ArgumentException ex)
                        {
                            error = $"Entry {i + 1}: invalid pattern ({ex.Message})";
                            return result;
                        }
                        break;
                }
            }
            return result;
        }

        // ── Parameter access (shared with the panel's inspection) ──────────────

        private static readonly string[] PseudoFields = { "Family", "Type", "Family and Type", "Category", "Level", "Model" };

        /// <summary>Instance parameter, else type parameter, else pseudo field. found=false when none exists.</summary>
        internal static string ReadValue(Document doc, Element el, string name, Dictionary<string, string> row, out bool found)
        {
            found = false;
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (row != null && row.TryGetValue(name, out string cached)) { found = true; return cached; }

            Parameter p = el.LookupParameter(name);
            if (p == null)
            {
                var type = doc.GetElement(el.GetTypeId());
                p = type?.LookupParameter(name);
            }
            if (p != null) { found = true; return ParamText(p); }

            string pseudo = Pseudo(doc, el, name);
            if (pseudo != null) { found = true; return pseudo; }
            return "";
        }

        private static string Pseudo(Document doc, Element el, string name)
        {
            var type = doc.GetElement(el.GetTypeId()) as ElementType;
            string family = (el as FamilyInstance)?.Symbol?.FamilyName ?? type?.FamilyName ?? "";
            switch (name.Trim().ToLowerInvariant())
            {
                case "family":          return family;
                case "type":            return type?.Name ?? "";
                case "family and type": return family + ": " + (type?.Name ?? "");
                case "category":        return el.Category?.Name ?? "";
                case "level":           return (doc.GetElement(el.LevelId) as Level)?.Name ?? "";
                case "model":           return doc.Title ?? ""; // host or linked model the element is in
                default:                return null;
            }
        }

        internal static string ParamText(Parameter p)
        {
            try
            {
                if (!p.HasValue) return "";
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString() ?? "";
                    case StorageType.Integer: return p.AsValueString() ?? p.AsInteger().ToString();
                    default: return p.AsValueString() ?? "";
                }
            }
            catch { return ""; }
        }

        /// <summary>All type then instance parameters (instance wins) plus pseudo fields, case-insensitive.</summary>
        internal static Dictionary<string, string> BuildRow(Document doc, Element el)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var type = doc.GetElement(el.GetTypeId());
            if (type != null)
                foreach (Parameter p in type.Parameters)
                    if (p.Definition != null) row[p.Definition.Name] = ParamText(p);
            foreach (Parameter p in el.Parameters)
                if (p.Definition != null) row[p.Definition.Name] = ParamText(p);
            foreach (var f in PseudoFields)
                if (!row.ContainsKey(f)) row[f] = Pseudo(doc, el, f) ?? "";
            return row;
        }

        internal static List<BuiltInCategory> ParseCategories(IEnumerable<string> names)
        {
            var list = new List<BuiltInCategory>();
            foreach (var n in names ?? Enumerable.Empty<string>())
                if (Enum.TryParse(n, out BuiltInCategory bic) && bic != BuiltInCategory.INVALID) list.Add(bic);
            return list;
        }

        internal static System.Windows.Media.Color ParseColor(string hex)
        {
            try
            {
                var o = System.Windows.Media.ColorConverter.ConvertFromString(hex ?? "");
                if (o is System.Windows.Media.Color c) return c;
            }
            catch { /* fall through */ }
            return System.Windows.Media.Color.FromRgb(0xE6, 0x00, 0x7E);
        }

        // ── Inspection for the panel (whole model, not just the view) ──────────

        internal class Inspection
        {
            internal int Considered, Matched;
            internal int Models = 1; // host + loaded linked models searched
            internal string Error;
            internal List<string> ParameterNames = new List<string>();
            internal List<KeyValuePair<string, int>> Values = new List<KeyValuePair<string, int>>(); // set's parameter
            /// <summary>Distinct values (most common first) of every parameter the set or its entries read.</summary>
            internal Dictionary<string, List<KeyValuePair<string, int>>> ValuesByParameter =
                new Dictionary<string, List<KeyValuePair<string, int>>>(StringComparer.OrdinalIgnoreCase);

            internal List<KeyValuePair<string, int>> ValuesOf(string parameter) =>
                parameter != null && ValuesByParameter.TryGetValue(parameter.Trim(), out var v) ? v : new List<KeyValuePair<string, int>>();
        }

        /// <summary>
        /// Parameter names available on the set's elements and the distinct values of its parameter, across the
        /// whole model and (if the set includes links) every loaded linked model, so a set can be set up from any view.
        /// Needs an API context.
        /// </summary>
        internal static Inspection Inspect(Document doc, MarkerSet set)
        {
            var info = new Inspection();
            var cats = ParseCategories(set.Categories);
            if (doc == null || cats.Count == 0) return info;

            var names = new HashSet<string>(PseudoFields, StringComparer.OrdinalIgnoreCase);
            var wanted = new List<string>();
            foreach (var p in new[] { set.Parameter }.Concat(set.Entries.Select(e => e.Parameter)))
                if (!string.IsNullOrWhiteSpace(p) && !wanted.Contains(p.Trim(), StringComparer.OrdinalIgnoreCase)) wanted.Add(p.Trim());
            var values = wanted.ToDictionary(p => p, p => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                                             StringComparer.OrdinalIgnoreCase);
            bool hasFilter = !string.IsNullOrWhiteSpace(set.Filter);

            var catFilter = new ElementMulticategoryFilter(cats);
            var docs = new List<Document> { doc };
            if (set.IncludeLinks)
            {
                var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RevitLinkInstance inst in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    Document ld = null;
                    try { ld = inst.GetLinkDocument(); } catch { /* unloaded / inaccessible */ }
                    if (ld != null && seenDocs.Add(ld.PathName + "|" + ld.Title)) docs.Add(ld);
                }
            }
            info.Models = docs.Count;

            foreach (var src in docs)
            {
                var seenTypes = new HashSet<ElementId>(); // ids are per document
                int namesFromThisDoc = 0;
                var collector = new FilteredElementCollector(src).WherePasses(catFilter).WhereElementIsNotElementType();
                foreach (Element el in collector)
                {
                    if (info.Considered >= 20000 || info.Error != null) break;
                    info.Considered++;
                    Dictionary<string, string> row = null;
                    if (hasFilter)
                    {
                        row = BuildRow(src, el);
                        if (!FilterEvaluator.TryEvaluate(set.Filter, row, out bool pass, out string err))
                        {
                            info.Error = "Filter: " + err;
                            break;
                        }
                        if (!pass) continue;
                    }
                    info.Matched++;

                    if (++namesFromThisDoc <= 300)
                    {
                        foreach (Parameter p in el.Parameters)
                            if (p.Definition != null) names.Add(p.Definition.Name);
                        var typeId = el.GetTypeId();
                        if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId))
                        {
                            var type = src.GetElement(typeId);
                            if (type != null)
                                foreach (Parameter p in type.Parameters)
                                    if (p.Definition != null) names.Add(p.Definition.Name);
                        }
                    }

                    foreach (var kv in values)
                    {
                        string v = ReadValue(src, el, kv.Key, row, out _).Trim();
                        kv.Value.TryGetValue(v, out int n);
                        kv.Value[v] = n + 1;
                    }
                }
            }

            info.ParameterNames = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var kv in values)
                info.ValuesByParameter[kv.Key] = kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
            info.Values = info.ValuesOf(set.Parameter);
            return info;
        }

        /// <summary>Model categories in the document, by name, for the panel's category list.</summary>
        internal static List<KeyValuePair<string, string>> ModelCategories(Document doc)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (doc == null) return list;
            foreach (Category c in doc.Settings.Categories)
            {
                try
                {
                    if (c.CategoryType != CategoryType.Model || c.IsTagCategory) continue;
                    var bic = (BuiltInCategory)(int)c.Id.GetIdValue();
                    if (!Enum.IsDefined(typeof(BuiltInCategory), bic) || bic == BuiltInCategory.INVALID) continue;
                    list.Add(new KeyValuePair<string, string>(bic.ToString(), c.Name));
                }
                catch { /* odd categories: skip */ }
            }
            return list.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
