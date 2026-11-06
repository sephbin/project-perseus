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
    /// the result for the overlay: one ring per (element, set) in the colour/icon of the first legend entry the
    /// element's parameter value matches. Runs on Idling, debounced, only when something changed (view switch,
    /// model edit, sets edited) — never per pan/zoom: the view-scoped collector already gives everything in the view,
    /// and queue/ViolationScreenPositionEvent re-projects the cached hits each tick.
    /// </summary>
    internal static class MarkerEngine
    {
        private const int MaxHits = 4000;
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);

        internal struct Hit
        {
            internal ElementId Id;
            internal BoundingBoxXYZ BBox;
            internal byte R, G, B;
            internal string Icon;
            internal int Ring; // 0 = innermost ring on this element
        }

        /// <summary>What one set found in the last evaluation (for the panel's status line).</summary>
        internal class SetResult
        {
            internal string SetId;
            internal int Considered;   // elements of the set's categories in the view
            internal int Matched;      // ... that passed the filter
            internal int Marked;       // ... whose value matched an entry
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
                var rings = new Dictionary<ElementId, int>();
                foreach (var set in sets)
                {
                    var r = EvaluateSet(doc, view, set, rings, hits);
                    results[set.Id] = r;
                    if (r.Error != null) Log.Info($"[MarkerEngine] set '{set.Name}': {r.Error}");
                }
                if (hits.Count >= MaxHits) note = $"Showing the first {MaxHits:N0} markers in this view.";
            }
            Publish(hits, results, note);
        }

        private static SetResult EvaluateSet(Document doc, View view, MarkerSet set,
                                             Dictionary<ElementId, int> rings, List<Hit> hits)
        {
            var r = new SetResult { SetId = set.Id, EntryCounts = new int[set.Entries.Count] };
            var cats = ParseCategories(set.Categories);
            if (cats.Count == 0) { r.Error = "Pick at least one category."; return r; }
            if (!set.Entries.Any(x => x.Enabled)) { r.Error = "Add a legend entry."; return r; }

            var matchers = BuildMatchers(set, out string regexError);
            if (regexError != null) { r.Error = regexError; return r; }

            var colors = set.Entries.Select(x => ParseColor(x.Color)).ToArray();
            bool hasFilter = !string.IsNullOrWhiteSpace(set.Filter);

            var collector = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType();

            foreach (Element el in collector)
            {
                if (hits.Count >= MaxHits) break;
                r.Considered++;

                Dictionary<string, string> row = null;
                if (hasFilter)
                {
                    row = BuildRow(doc, el);
                    if (!FilterEvaluator.TryEvaluate(set.Filter, row, out bool pass, out string err))
                    {
                        r.Error = "Filter: " + err;
                        return r;
                    }
                    if (!pass) continue;
                }
                r.Matched++;

                string value = ReadValue(doc, el, set.Parameter, row, out bool found);
                if (!found) r.ParamMissing++;

                int entry = -1;
                for (int i = 0; i < matchers.Length; i++)
                    if (matchers[i] != null && matchers[i](value)) { entry = i; break; }
                if (entry < 0) continue;

                BoundingBoxXYZ bb = el.get_BoundingBox(null);
                if (bb == null) continue;

                r.Marked++;
                r.EntryCounts[entry]++;
                rings.TryGetValue(el.Id, out int ring);
                rings[el.Id] = ring + 1;
                var c = colors[entry];
                hits.Add(new Hit { Id = el.Id, BBox = bb, R = c.R, G = c.G, B = c.B, Icon = set.Entries[entry].Icon, Ring = ring });
            }
            return r;
        }

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

        private static readonly string[] PseudoFields = { "Family", "Type", "Family and Type", "Category", "Level" };

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
            internal string Error;
            internal List<string> ParameterNames = new List<string>();
            internal List<KeyValuePair<string, int>> Values = new List<KeyValuePair<string, int>>();
        }

        /// <summary>
        /// Parameter names available on the set's elements and the distinct values of its parameter, across the
        /// whole model (so a set can be set up from any view). Needs an API context.
        /// </summary>
        internal static Inspection Inspect(Document doc, MarkerSet set)
        {
            var info = new Inspection();
            var cats = ParseCategories(set.Categories);
            if (doc == null || cats.Count == 0) return info;

            var names = new HashSet<string>(PseudoFields, StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool hasFilter = !string.IsNullOrWhiteSpace(set.Filter);
            var seenTypes = new HashSet<ElementId>();

            var collector = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType();
            foreach (Element el in collector)
            {
                if (info.Considered >= 20000) break;
                info.Considered++;
                Dictionary<string, string> row = null;
                if (hasFilter)
                {
                    row = BuildRow(doc, el);
                    if (!FilterEvaluator.TryEvaluate(set.Filter, row, out bool pass, out string err))
                    {
                        info.Error = "Filter: " + err;
                        break;
                    }
                    if (!pass) continue;
                }
                info.Matched++;

                if (info.Matched <= 300)
                {
                    foreach (Parameter p in el.Parameters)
                        if (p.Definition != null) names.Add(p.Definition.Name);
                    var typeId = el.GetTypeId();
                    if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId))
                    {
                        var type = doc.GetElement(typeId);
                        if (type != null)
                            foreach (Parameter p in type.Parameters)
                                if (p.Definition != null) names.Add(p.Definition.Name);
                    }
                }

                if (!string.IsNullOrWhiteSpace(set.Parameter))
                {
                    string v = ReadValue(doc, el, set.Parameter, row, out _).Trim();
                    values.TryGetValue(v, out int n);
                    values[v] = n + 1;
                }
            }

            info.ParameterNames = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            info.Values = values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
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
