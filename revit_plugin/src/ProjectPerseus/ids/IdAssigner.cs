using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.util;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.ids
{
    internal class IdAssignResult
    {
        public int PerseusIdSet;
        public int Assigned;      // architectural IDs written to elements that had none / an invalid one
        public int ValuesWritten; // value-rule parameters written (copied project info, level, room...)
        public readonly SortedSet<string> TransformErrors = new SortedSet<string>(); // "rule: error", value not written
        public int Reassigned;    // subset of Assigned that replaced a duplicate or mismatched value
        public int SkippedNotOwned;
        public int SkippedInGroups;     // members of model/detail groups; writing them re-resolves the group and can delete dimensions
        public int Errors;
        public int Planned;             // dry run only: writes that would be made to elements this user can edit
        public readonly HashSet<long> PlannedIds = new HashSet<long>(); // dry run only: those elements
        public int ElementsChecked;
        public int ActiveRules;
        public int ParametersFound;     // Perseus_ID + rule parameters actually bound in this model
        public int MissingOnElement;    // element did not carry the parameter (not bound to its category)
        public readonly SortedSet<string> NoPrefixCategories = new SortedSet<string>();
        public readonly SortedSet<string> UnresolvedTokens = new SortedSet<string>(); // e.g. Level.Name with no level
        public bool RolledBack;         // Revit tried to delete/remove other elements, so the write was cancelled
        public readonly SortedSet<string> BlockedBy = new SortedSet<string>(); // families Revit reported errors in

        public int TotalChanges => PerseusIdSet + Assigned + ValuesWritten;

        public string Summary() =>
            (RolledBack
                ? "NOTHING WAS WRITTEN. Revit tried to delete or remove other elements while updating these IDs, so the " +
                  "change was cancelled. Problem families: " + string.Join(", ", BlockedBy) + "\n"
                : "") +
            $"Perseus_ID written: {PerseusIdSet}\n" +
            $"Architectural IDs assigned: {Assigned} (of which {Reassigned} replaced duplicates or invalid values)\n" +
            $"Values updated: {ValuesWritten}\n" +
            $"Skipped (checked out by another user): {SkippedNotOwned}\n" +
            (SkippedInGroups > 0 ? $"Skipped (members of groups): {SkippedInGroups}\n" : "") +
            $"Errors: {Errors}\n" +
            $"Checked {ElementsChecked} elements against {ActiveRules} active rule(s); {ParametersFound} parameter(s) found in the model." +
            (MissingOnElement > 0 ? $"\n{MissingOnElement} element/parameter pairs had no such parameter." : "") +
            (UnresolvedTokens.Count > 0
                ? "\nThese template values were empty for some elements (IDs skipped, values cleared): " + string.Join(", ", UnresolvedTokens)
                : "") +
            (TransformErrors.Count > 0
                ? "\nTransform / template errors (values not written): " + string.Join("; ", TransformErrors.Take(5))
                : "") +
            (NoPrefixCategories.Count > 0
                ? "\nNo category prefix configured for: " + string.Join(", ", NoPrefixCategories.Take(12)) +
                  (NoPrefixCategories.Count > 12 ? ", ..." : "")
                : "");
    }

    /// <summary>
    /// Applies the Auto Parameters rules to elements: fills Perseus_ID with the UniqueId, keeps each value rule's
    /// parameter equal to its pattern (+ transform; util/AutoValue), and keeps each ID rule parameter
    /// valid and unique. A value is kept if it matches the rule's pattern for the element's category and no
    /// lower-ElementId element holds the same value; otherwise the next number for that prefix is allocated.
    /// Counters come from the local model state (maximum existing number + 1).
    /// </summary>
    internal static class IdAssigner
    {
        private class ActiveRule
        {
            public IdRuleDto Dto;
            public IdPattern Pattern;          // ID rules; value rules: their own pattern / default template (Perseus syntax)
            public JinjaTemplate Jinja;        // value rules whose own pattern is a Jinja template
            public string TemplateSource;      // value rules: token whose text is the template, e.g. "Type.Tag Template"
            public Guid ParamGuid;
            public bool IsValue => Dto.IsValue;
        }

        /// <summary>A value template: Perseus pattern ({Level.Name}) or Jinja ({{ Level.Name }}, {% if %}); Error = invalid.</summary>
        private sealed class Compiled
        {
            public IdPattern Pattern;
            public JinjaTemplate Jinja;
            public string Error;
            public bool IsEmpty => Pattern == null && Jinja == null;
        }

        private static readonly Compiled Nothing = new Compiled();
        private static readonly ConcurrentDictionary<string, Compiled> _compiled = new ConcurrentDictionary<string, Compiled>();

        // Parsed once per distinct text (types holding the same template share it).
        private static Compiled Compile(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Nothing;
            if (_compiled.Count > 5000) _compiled.Clear();
            return _compiled.GetOrAdd(text, t =>
            {
                if (JinjaTemplate.IsJinja(t))
                {
                    var j = JinjaTemplate.Parse(t, out string err);
                    return j != null ? new Compiled { Jinja = j } : new Compiled { Error = err };
                }
                var p = IdPattern.ForRule(t, true);
                return p != null ? new Compiled { Pattern = p } : new Compiled { Error = "unknown token or unmatched brace" };
            });
        }

        /// <summary>
        /// True when a rule can run: a valid pattern for its kind; value rules may instead (or also) name a template source,
        /// and their pattern may be a Jinja template.
        /// </summary>
        internal static bool IsUsable(IdRuleDto r)
        {
            if (!r.IsValue) return IdPattern.ForRule(r.Pattern, false, r.UniqueWith) != null;
            bool hasSource = !string.IsNullOrWhiteSpace(r.TemplateSource);
            if (hasSource && IdPattern.ParseSource(r.TemplateSource) == null) return false;
            if (string.IsNullOrWhiteSpace(r.Pattern)) return hasSource;
            return Compile(r.Pattern).Error == null;
        }

        /// <summary>
        /// The template a value rule renders for an element: the text held in its source parameter (the type's "Tag
        /// Template"), else the rule's own pattern (the default; empty = nothing, the value is cleared).
        /// <paramref name="badText"/> = the source text when it isn't a valid template (Error says why).
        /// </summary>
        private static Compiled TemplateFor(ActiveRule rule, Func<string, string> resolve, out string badText)
        {
            badText = null;
            if (rule.TemplateSource != null)
            {
                string text = resolve(rule.TemplateSource);
                if (!string.IsNullOrEmpty(text))
                {
                    var c = Compile(text);
                    if (c.Error != null) badText = text;
                    return c;
                }
            }
            return rule.Jinja != null ? new Compiled { Jinja = rule.Jinja } : rule.Pattern != null ? new Compiled { Pattern = rule.Pattern } : Nothing;
        }

        /// <summary>Tokens a value rule reads for this element (its template source and the template's tokens / objects).</summary>
        private static IEnumerable<string> ValueTokens(ActiveRule rule, Func<string, string> resolve)
        {
            var tokens = new List<string>();
            if (rule.TemplateSource != null) tokens.Add(rule.TemplateSource);
            var c = TemplateFor(rule, resolve, out _);
            if (c.Pattern != null) tokens.AddRange(c.Pattern.Tokens);
            return tokens;
        }

        /// <summary>Everything a value rule reads for this element: tokens, the template source, and Jinja chains (Host.Room).</summary>
        private static void AddValueDependencies(ActiveRule rule, Element el, RelatedValueResolver resolver, Func<string, string> resolve, ISet<long> deps)
        {
            resolver.AddDependencies(el, ValueTokens(rule, resolve), deps);
            var c = TemplateFor(rule, resolve, out _);
            if (c.Jinja == null) return;
            resolver.AddPathDependencies(el, c.Jinja.Paths, deps);
            // Reference collections the template loops over ({% for s in Signs %}): the owner and every member.
            if (resolver.Collections.Count > 0)
            {
                var named = c.Jinja.Mentions(resolver.CollectionNames);
                if (named.Count > 0) resolver.AddCollectionDependencies(el, named, deps);
            }
        }

        /// <summary>
        /// The value a value rule wants on this element; false = not written (template / transform error, recorded).
        /// </summary>
        private static bool TryValue(ActiveRule rule, Element el, RelatedValueResolver resolver, IDictionary<string, string> pending,
                                     Func<string, string> resolve, IdAssignResult result, out string target)
        {
            target = "";
            string name = rule.Dto.ParameterName.Trim();
            var c = TemplateFor(rule, resolve, out string badText);
            if (c.Error != null)
            {
                string shown = badText ?? rule.Dto.Pattern ?? "";
                if (shown.Length > 60) shown = shown.Substring(0, 57) + "...";
                result.TransformErrors.Add($"{name}: template '{shown}'{(badText != null ? " from " + rule.TemplateSource : "")} isn't valid: {c.Error}");
                return false;
            }
            string err;
            if (c.Jinja != null)
            {
                var values = new Dictionary<string, object>
                {
                    { "CategoryPrefix", resolve("CategoryPrefix") },
                    { "Workset", resolve("Workset") },
                };
                if (!c.Jinja.TryRender(resolver.TemplateSource(el, pending), el, values, out string rendered, out err))
                {
                    result.TransformErrors.Add($"{name}: {err}");
                    return false;
                }
                if (!AutoValue.TryTransform(rendered, rule.Dto.Transform, null, out target, out err))
                {
                    result.TransformErrors.Add($"{name}: {err}");
                    return false;
                }
                return true;
            }
            if (c.IsEmpty) return true; // no template and no default: nothing to show
            var outcome = AutoValue.Compute(c.Pattern, rule.Dto.Transform, resolve, out target, out err);
            if (outcome == AutoValue.Outcome.TransformError)
            {
                result.TransformErrors.Add($"{name}: {err}");
                return false;
            }
            // A token with no value (element not in a room, parameter missing) clears the value rather than leaving a
            // stale one behind.
            if (outcome == AutoValue.Outcome.Unresolved) NoteUnresolved(result, c.Pattern, resolve, el);
            return true;
        }

        private class Change
        {
            public Element Element;
            public Parameter Param;
            public string NewValue;
            public bool IsPerseusId;
            public bool IsValue;
            public bool IsReassign;
            // For rule writes planned in the index: lets a skipped write be taken back out of the index.
            public string RuleName;
            public string OldValue;
        }

        // Per-document index of rule values, so edits and the pre-sync claims check don't rescan the whole model.
        // Built in small slices at idle time (IndexBuilder, driven by IdIndexMaintainer), kept current with
        // ApplyChanges (local edits, and other users' changes after a sync / Reload Latest), and rebuilt from scratch
        // on full runs, when the rules change, or when a planned write was skipped.
        private class IdIndex
        {
            public string Signature;
            // element id -> UniqueId, for every element holding at least one value
            public readonly Dictionary<long, string> Uids = new Dictionary<long, string>();
            // rule name -> value -> element ids holding it
            public readonly Dictionary<string, Dictionary<string, SortedSet<long>>> Holders =
                new Dictionary<string, Dictionary<string, SortedSet<long>>>(StringComparer.OrdinalIgnoreCase);
            // rule name -> element id -> its current value
            public readonly Dictionary<string, Dictionary<long, string>> ValueById =
                new Dictionary<string, Dictionary<long, string>>(StringComparer.OrdinalIgnoreCase);
            // "rule|prefix" -> highest number seen
            public readonly Dictionary<string, long> Max = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            // Value rules: element id -> related elements its values are read from (level, room, host, type, group),
            // and the reverse, so changing a level / room / type refreshes the elements that copy it.
            public readonly Dictionary<long, HashSet<long>> DepsOf = new Dictionary<long, HashSet<long>>();
            public readonly Dictionary<long, HashSet<long>> DependentsOf = new Dictionary<long, HashSet<long>>();

            public void SetDeps(long id, HashSet<long> deps)
            {
                if (DepsOf.TryGetValue(id, out var old))
                {
                    if (deps != null && old.SetEquals(deps)) return;
                    foreach (var d in old)
                        if (DependentsOf.TryGetValue(d, out var set)) { set.Remove(id); if (set.Count == 0) DependentsOf.Remove(d); }
                    DepsOf.Remove(id);
                }
                if (deps == null || deps.Count == 0) return;
                DepsOf[id] = deps;
                foreach (var d in deps)
                {
                    if (!DependentsOf.TryGetValue(d, out var set)) DependentsOf[d] = set = new HashSet<long>();
                    set.Add(id);
                }
            }

            public void Set(string rule, long id, string value)
            {
                if (!Holders.TryGetValue(rule, out var byValue)) Holders[rule] = byValue = new Dictionary<string, SortedSet<long>>();
                if (!ValueById.TryGetValue(rule, out var byId)) ValueById[rule] = byId = new Dictionary<long, string>();

                if (byId.TryGetValue(id, out var old))
                {
                    if (old == value) return;
                    if (byValue.TryGetValue(old, out var oldSet)) { oldSet.Remove(id); if (oldSet.Count == 0) byValue.Remove(old); }
                    byId.Remove(id);
                }
                if (string.IsNullOrEmpty(value)) return;

                byId[id] = value;
                if (!byValue.TryGetValue(value, out var set)) byValue[value] = set = new SortedSet<long>();
                set.Add(id);
            }

            // Forgets a (deleted) element everywhere. Counters are left alone: a gap in the numbering is harmless.
            public void Remove(long id)
            {
                foreach (var rule in ValueById.Keys.ToList()) { Set(rule, id, null); ForgetNumber(rule, id); }
                Uids.Remove(id);
                SetDeps(id, null);
            }

            public long LowestHolder(string rule, string value) =>
                Holders.TryGetValue(rule, out var byValue) && byValue.TryGetValue(value, out var set) && set.Count > 0
                    ? set.Min : -1;

            public void NoteNumber(string rule, string prefix, long n)
            {
                string key = rule + "|" + prefix;
                if (!Max.TryGetValue(key, out var cur) || n > cur) Max[key] = n;
            }

            // "rule|scope" -> element id -> the number it holds now (for next / fill numbering), and
            // "rule|element id" -> the "rule|scope" it is listed under.
            private readonly Dictionary<string, Dictionary<long, long>> _held = new Dictionary<string, Dictionary<long, long>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _heldIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public void NoteNumber(string rule, string scope, long id, long n)
            {
                NoteNumber(rule, scope, n);
                ForgetNumber(rule, id);
                string key = rule + "|" + scope;
                if (!_held.TryGetValue(key, out var map)) _held[key] = map = new Dictionary<long, long>();
                map[id] = n;
                _heldIn[rule + "|" + id] = key;
            }

            public void ForgetNumber(string rule, long id)
            {
                string k = rule + "|" + id;
                if (!_heldIn.TryGetValue(k, out var key)) return;
                _heldIn.Remove(k);
                if (_held.TryGetValue(key, out var map)) { map.Remove(id); if (map.Count == 0) _held.Remove(key); }
            }

            /// <summary>Numbers held by other elements in the counter group.</summary>
            public IEnumerable<long> HeldByOthers(string rule, string scope, long id) =>
                _held.TryGetValue(rule + "|" + scope, out var map)
                    ? map.Where(kv => kv.Key != id).Select(kv => kv.Value)
                    : Enumerable.Empty<long>();

            public long Allocate(string rule, string prefix)
            {
                string key = rule + "|" + prefix;
                Max.TryGetValue(key, out var cur);
                Max[key] = cur + 1;
                return cur + 1;
            }
        }

        private static readonly ConcurrentDictionary<string, IdIndex> _indexes =
            new ConcurrentDictionary<string, IdIndex>();

        internal static void InvalidateIndex(string docGuid)
        {
            if (!string.IsNullOrEmpty(docGuid)) _indexes.TryRemove(docGuid, out _);
        }

        private static List<ActiveRule> ResolveActive(Document doc, IdRulesDto rules, out Dictionary<string, Guid> guids)
        {
            var names = new List<string> { naming.ParameterNames.For(doc, naming.ParameterNames.PerseusId) };
            names.AddRange(rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName))
                                      .Select(r => r.ParameterName.Trim()));
            guids = SharedParameterManager.ResolveGuids(doc, names);

            var active = new List<ActiveRule>();
            foreach (var r in rules.Rules.Where(r => r.Enabled))
            {
                string name = r.ParameterName?.Trim();
                if (!IsUsable(r) || string.IsNullOrEmpty(name) || !guids.TryGetValue(name, out var g)) continue;
                if (!r.IsValue)
                {
                    active.Add(new ActiveRule { Dto = r, Pattern = IdPattern.ForRule(r.Pattern, false, r.UniqueWith), ParamGuid = g });
                    continue;
                }
                var own = Compile(r.Pattern);
                active.Add(new ActiveRule
                {
                    Dto = r, Pattern = own.Pattern, Jinja = own.Jinja, ParamGuid = g,
                    TemplateSource = IdPattern.ParseSource(r.TemplateSource),
                });
            }
            // Value rules first, so an ID rule can number with a value copied in the same run ({Element.Param}).
            return active.Where(a => a.IsValue).Concat(active.Where(a => !a.IsValue)).ToList();
        }

        /// <summary>True when a current index (matching these rules) is in memory, so no full scan is needed.</summary>
        internal static bool IsIndexReady(string docGuid, IdRulesDto rules) =>
            rules != null && !string.IsNullOrEmpty(docGuid) &&
            _indexes.TryGetValue(docGuid, out var idx) && idx.Signature == rules.Signature;

        // Records one element's rule values (and counter numbers) in the index.
        private static void Feed(IdIndex index, Element el, List<ActiveRule> active, RelatedValueResolver resolver)
        {
            long id = el.Id.GetIdValue();
            Func<string, string> resolve = null;
            bool any = false;
            HashSet<long> deps = null;
            foreach (var rule in active)
            {
                if (!Applies(rule.Dto, el)) continue;
                if (rule.IsValue)
                {
                    // Value rules aren't unique (no claims, no counters); only what they read from is indexed.
                    if (deps == null) deps = new HashSet<long>();
                    AddValueDependencies(rule, el, resolver, resolve ?? (resolve = resolver.For(el)), deps);
                    continue;
                }
                string name = rule.Dto.ParameterName.Trim();
                string value = el.get_Parameter(rule.ParamGuid)?.AsString();
                if (resolve == null) resolve = resolver.For(el);
                if (rule.Pattern.HasUniqueWith)
                {
                    // A change to what it is unique with (the door's room) re-checks the ID.
                    if (deps == null) deps = new HashSet<long>();
                    resolver.AddDependencies(el, rule.Pattern.UniqueWithTokens, deps);
                }
                index.Set(name, id, rule.Pattern.UniqueKey(value, resolve));
                if (string.IsNullOrEmpty(value)) { index.ForgetNumber(name, id); continue; }
                any = true;
                string scope = rule.Pattern.ScopeKey(resolve);
                if (scope != null && rule.Pattern.TryExtractNumber(value, resolve, out long n))
                    index.NoteNumber(name, scope, id, n);
                else
                    index.ForgetNumber(name, id);
            }
            if (any) index.Uids[id] = el.UniqueId;
            else index.Uids.Remove(id);
            index.SetDeps(id, deps);
        }

        /// <summary>
        /// Elements whose value-rule parameters read from any of <paramref name="changed"/> (a renamed level, an edited
        /// room or type...), from the in-memory index. Empty when the index isn't ready. Main thread; no model access.
        /// </summary>
        internal static HashSet<long> Dependents(string docGuid, IdRulesDto rules, IEnumerable<long> changed)
        {
            var result = new HashSet<long>();
            if (!IsIndexReady(docGuid, rules)) return result;
            var index = _indexes[docGuid];
            foreach (var id in changed)
                if (index.DependentsOf.TryGetValue(id, out var set)) result.UnionWith(set);
            return result;
        }

        /// <summary>True when an enabled value rule reads Project Information ({Project.*}).</summary>
        internal static bool UsesProject(IdRulesDto rules) =>
            rules != null && rules.Rules.Any(r => r.Enabled && r.IsValue && UsesProject(r));

        // A template rule may use {Project.*} in any type's template, so it counts as reading Project Information.
        private static bool UsesProject(IdRuleDto r)
        {
            if (!string.IsNullOrWhiteSpace(r.TemplateSource)) return true;
            var c = Compile(r.Pattern);
            if (c.Jinja != null) return c.Jinja.Paths.Any(p => p.Contains("Project"));
            return c.Pattern?.Tokens.SelectMany(IdPattern.Alternatives)
                .Any(t => t.StartsWith("Project.", StringComparison.Ordinal)) == true;
        }

        /// <summary>Every element a {Project.*} value rule applies to (after Project Information changed). Read-only collector.</summary>
        internal static List<long> ProjectDependents(Document doc, IdRulesDto rules)
        {
            var active = ResolveActive(doc, rules, out _).Where(r => r.IsValue && UsesProject(r.Dto)).ToList();
            if (active.Count == 0) return new List<long>();
            return CandidateIds(doc, active).Select(i => i.GetIdValue()).ToList();
        }

        /// <summary>
        /// Builds a document's index a slice at a time so the full-model scan (seconds on large models) never runs in
        /// one go on the UI thread. Created and stepped by IdIndexMaintainer during Idling; the finished index is
        /// published only when complete. Main thread only.
        /// </summary>
        internal class IndexBuilder
        {
            private readonly Document _doc;
            private readonly string _docGuid;
            private readonly List<ActiveRule> _active;
            private readonly RelatedValueResolver _resolver;
            private readonly IdIndex _index;
            private readonly IList<ElementId> _ids;
            private int _cursor;
            private readonly System.Diagnostics.Stopwatch _total = new System.Diagnostics.Stopwatch();

            internal IndexBuilder(Document doc, string docGuid, IdRulesDto rules)
            {
                _doc = doc;
                _docGuid = docGuid;
                _active = ResolveActive(doc, rules, out _);
                _resolver = new RelatedValueResolver(doc, rules);
                _index = new IdIndex { Signature = rules.Signature };
                _total.Start();
                _ids = _active.Count == 0 ? (IList<ElementId>)new List<ElementId>() : CandidateIds(doc, _active);
                _total.Stop();
            }

            internal int Total => _ids.Count;
            internal int Done => _cursor;

            /// <summary>Processes elements until <paramref name="budgetMs"/> is used. True once the index is published.</summary>
            internal bool Step(int budgetMs)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _total.Start();
                try
                {
                    while (_cursor < _ids.Count)
                    {
                        Element el = _doc.GetElement(_ids[_cursor++]);
                        if (IsEligible(el)) Feed(_index, el, _active, _resolver);
                        if ((_cursor & 63) == 0 && clock.ElapsedMilliseconds >= budgetMs) return false;
                    }
                }
                finally { _total.Stop(); }

                _indexes[_docGuid] = _index;
                Log.Info($"[IdAssigner] index built in idle slices: {_ids.Count} candidates, {_index.Uids.Count} with IDs, " +
                         $"{_total.ElapsedMilliseconds} ms of work");
                return true;
            }
        }

        // Elements the rules can apply to. When every rule names its categories, only those are collected.
        private static IList<ElementId> CandidateIds(Document doc, List<ActiveRule> active)
        {
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (!active.Any(r => r.Dto.ApplyToAll))
            {
                var names = new HashSet<string>(active.SelectMany(r => r.Dto.Categories), StringComparer.OrdinalIgnoreCase);
                var cats = new List<ElementId>();
                foreach (Category c in doc.Settings.Categories)
                    if (names.Contains(c.Name)) cats.Add(c.Id);
                if (cats.Count == 0) return new List<ElementId>();
                collector = collector.WherePasses(new ElementMulticategoryFilter(cats));
            }
            return collector.ToElementIds().ToList();
        }

        /// <summary>
        /// Brings an existing index up to date with changed and deleted elements (no-op when there is no index; it
        /// will be built from scratch). Cheap: touches only the given elements. Main thread; read-only on the model.
        /// </summary>
        internal static void ApplyChanges(Document doc, string docGuid, IdRulesDto rules,
                                          ICollection<long> changed, ICollection<long> deleted)
        {
            if (rules == null || !_indexes.TryGetValue(docGuid, out var index) || index.Signature != rules.Signature) return;
            if (deleted != null) foreach (var id in deleted) index.Remove(id);
            if (changed == null || changed.Count == 0) return;

            var active = ResolveActive(doc, rules, out _);
            if (active.Count == 0) return;
            var resolver = new RelatedValueResolver(doc, rules);
            foreach (var id in changed)
            {
                Element el = doc.GetElement(RevitExtensions.CreateId(id));
                if (IsEligible(el)) Feed(index, el, active, resolver);
                else index.Remove(id);
            }
        }

        /// <summary>
        /// The claims snapshot (every non-empty rule value) straight from the in-memory index; null when the index
        /// isn't ready. This is what keeps the pre-sync check from scanning the whole model.
        /// </summary>
        internal static List<IdClaimItem> SnapshotFromIndex(string docGuid, IdRulesDto rules)
        {
            if (!IsIndexReady(docGuid, rules)) return null;
            var index = _indexes[docGuid];
            var items = new List<IdClaimItem>();
            foreach (var rule in index.ValueById)
                foreach (var kv in rule.Value)
                    if (index.Uids.TryGetValue(kv.Key, out var uid))
                        items.Add(new IdClaimItem { Parameter = rule.Key, Uid = uid, ElementId = kv.Key, Value = kv.Value });
            return items;
        }

        /// <summary>
        /// Runs the rules over <paramref name="onlyIds"/>, or the whole model when null (audit).
        /// Opens its own transaction; must not be called while the document is modifiable.
        /// With <paramref name="dryRun"/> nothing is written (safe inside document events such as the pre-sync
        /// hook): <see cref="IdAssignResult.Planned"/> counts the writes that would land on elements this user can
        /// edit, and no ID numbers are allocated.
        /// <paramref name="fresh"/> = elements this user just created (or pasted): for "next" / "fill" rules the value
        /// they arrived with (Revit's next Mark, a copied value) is minimised rather than kept.
        /// </summary>
        internal static IdAssignResult Run(Document doc, string docGuid, IdRulesDto rules, ICollection<long> onlyIds,
                                           bool renumber = false, IdClashFix fix = null, bool dryRun = false,
                                           ICollection<long> fresh = null)
        {
            var result = new IdAssignResult();
            if (doc == null) return result;
            if (!dryRun && (doc.IsReadOnly || doc.IsModifiable)) return result;

            var active = ResolveActive(doc, rules, out var guids);
            bool hasPerseusId = guids.TryGetValue(naming.ParameterNames.For(doc, naming.ParameterNames.PerseusId), out var perseusIdGuid);
            result.ActiveRules = active.Count;
            result.ParametersFound = guids.Count;
            if (!hasPerseusId && active.Count == 0) return result;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool full = onlyIds == null;
            IEnumerable<Element> candidates = full
                ? Collect(doc)
                : onlyIds.Select(id => doc.GetElement(RevitExtensions.CreateId(id))).Where(IsEligible);
            var ordered = candidates.OrderBy(e => e.Id.GetIdValue()).ToList();
            result.ElementsChecked = ordered.Count;
            long collectMs = clock.ElapsedMilliseconds;

            // A full run already holds every eligible element, so the index reuses that list.
            IdIndex index = GetIndex(doc, docGuid, rules, active, rebuild: full, all: full ? ordered : null);
            long indexMs = clock.ElapsedMilliseconds - collectMs;

            var changes = new List<Change>();
            var renumberGroups = new Dictionary<ActiveRule, Dictionary<string, List<RenumberItem>>>();
            var resolver = new RelatedValueResolver(doc, rules);
            foreach (var el in ordered)
            {
                long id = el.Id.GetIdValue();

                // Members of group instances share their values across the group's instances unless the parameter
                // is set to "values can vary by group instance" (Ensure sets that on our parameters). Only then
                // are they written; otherwise Revit re-resolves the group and can delete dimensions. Any such
                // deletion is still caught by the commit guard, which cancels the write.
                bool inGroup = el.GroupId != ElementId.InvalidElementId;
                bool countedGroupSkip = false;
                // Values this run is about to write on this element (parameter name -> value), read by {Element.*}.
                Dictionary<string, string> pending = null;

                if (hasPerseusId && rules.PerseusId.AppliesTo(el.Category.Name))
                {
                    var p = el.get_Parameter(perseusIdGuid);
                    if (p == null) result.MissingOnElement++;
                    else if (inGroup && !VariesAcrossGroups(p)) { result.SkippedInGroups++; countedGroupSkip = true; }
                    else if (!p.IsReadOnly && p.AsString() != el.UniqueId)
                        changes.Add(new Change { Element = el, Param = p, NewValue = el.UniqueId, IsPerseusId = true });
                }

                foreach (var rule in active)
                {
                    if (!Applies(rule.Dto, el)) continue;
                    var p = el.get_Parameter(rule.ParamGuid);
                    if (p == null) result.MissingOnElement++;
                    if (p == null || p.IsReadOnly) continue;
                    if (inGroup && !VariesAcrossGroups(p))
                    {
                        if (!countedGroupSkip) { result.SkippedInGroups++; countedGroupSkip = true; }
                        continue;
                    }

                    // Every token must resolve (category prefix, level, host...). The resolved values also
                    // define which counter the element belongs to, e.g. "walls on Level 02".
                    var resolve = resolver.For(el, pending);

                    if (rule.IsValue)
                    {
                        if (renumber) continue;
                        string now = p.AsString() ?? "";
                        if (rule.Dto.FillOnlyEmpty && now.Length > 0) continue;
                        // Perseus pattern or Jinja template, the rule's own or read from the element (its type's template).
                        if (!TryValue(rule, el, resolver, pending, resolve, result, out string target)) continue;
                        if (now == target) continue;
                        changes.Add(new Change { Element = el, Param = p, NewValue = target, IsValue = true, IsReassign = now.Length > 0 });
                        if (pending == null) pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        pending[rule.Dto.ParameterName.Trim()] = target;
                        continue;
                    }

                    string scope = rule.Pattern.ScopeKey(resolve);
                    if (scope == null) { NoteUnresolved(result, rule.Pattern, resolve, el); continue; }

                    // Renumber mode: just collect the element under its counter scope; numbers are assigned in
                    // positional order once every element has been seen.
                    if (renumber)
                    {
                        if (!renumberGroups.TryGetValue(rule, out var byScope))
                            renumberGroups[rule] = byScope = new Dictionary<string, List<RenumberItem>>();
                        if (!byScope.TryGetValue(scope, out var items)) byScope[scope] = items = new List<RenumberItem>();
                        items.Add(new RenumberItem { Element = el, Param = p, Resolve = resolve });
                        continue;
                    }

                    string name = rule.Dto.ParameterName.Trim();
                    string current = p.AsString();
                    // What has to be unique: the value, or the value + what it is unique with ("D01 [101]").
                    string currentKey = rule.Pattern.UniqueKey(current, resolve);
                    // Same value, but now unique with something else (the door's room number just changed).
                    string before = index.ValueById.TryGetValue(name, out var byId) && byId.TryGetValue(id, out var b) ? b : null;
                    bool regrouped = rule.Pattern.HasUniqueWith && !string.IsNullOrEmpty(current) && before != null &&
                                     before != currentKey && before.StartsWith(current + " [", StringComparison.Ordinal);
                    index.Set(name, id, currentKey); // sync index with what is actually in the model right now
                    if (!string.IsNullOrEmpty(current)) index.Uids[id] = el.UniqueId;

                    // A value the server's registry says another element holds (see IdClaims) must be replaced even
                    // though it is unique within this local model.
                    bool forced = fix != null && fix.Force.Contains(IdClashFix.ForceKey(name, id));
                    string mode = IdNumbering.Normalise(rule.Dto.IncrementMode);
                    Func<long, bool> taken = k => fix != null &&
                        fix.Taken.Contains(IdClashFix.TakenKey(name, rule.Pattern.UniqueKey(rule.Pattern.Render(resolve, k), resolve)));
                    bool valid = rule.Pattern.TryExtractNumber(current, resolve, out long n);
                    if (valid)
                    {
                        // next / fill: a number the element arrived with (new / pasted, or moved to another group) is
                        // only kept when it is already the number the mode would pick.
                        bool untrusted = IdNumbering.Minimises(mode) && ((fresh != null && fresh.Contains(id)) || regrouped);
                        bool keep = !untrusted || IdNumbering.Pick(mode, index.HeldByOthers(name, scope, id), taken) == n;
                        if (keep)
                        {
                            index.NoteNumber(name, scope, id, n);
                            if (!forced && index.LowestHolder(name, currentKey) == id) continue; // keep
                        }
                    }
                    index.ForgetNumber(name, id);

                    if (dryRun)
                    {
                        // No number is allocated, so the index counters stay exactly as the model has them.
                        changes.Add(new Change { Element = el, Param = p, NewValue = null, IsReassign = !string.IsNullOrEmpty(current) });
                        continue;
                    }

                    long number;
                    if (IdNumbering.Minimises(mode))
                        number = IdNumbering.Pick(mode, index.HeldByOthers(name, scope, id), taken);
                    else
                        do { number = index.Allocate(name, scope); } while (taken(number));
                    index.NoteNumber(name, scope, id, number);
                    string value = rule.Pattern.Render(resolve, number);
                    index.Set(name, id, rule.Pattern.UniqueKey(value, resolve));
                    index.Uids[id] = el.UniqueId;
                    changes.Add(new Change
                    {
                        Element = el, Param = p, NewValue = value,
                        IsReassign = !string.IsNullOrEmpty(current),
                        RuleName = name, OldValue = currentKey,
                    });
                }
            }

            if (renumber) PlanRenumber(renumberGroups, changes);

            if (dryRun)
            {
                foreach (var c in changes.Where(c => CanEdit(doc, c.Element.Id)))
                {
                    result.Planned++;
                    result.PlannedIds.Add(c.Element.Id.GetIdValue());
                }
                Log.Info($"[IdAssigner] dry run: {changes.Count} write(s) needed, {result.Planned} on elements this user can edit " +
                         $"({ordered.Count} elements checked, {clock.ElapsedMilliseconds} ms)");
                return result;
            }

            long planMs = clock.ElapsedMilliseconds - collectMs - indexMs;
            Log.Info($"[IdAssigner] timing: collect {collectMs} ms, index {indexMs} ms, plan {planMs} ms " +
                     $"({ordered.Count} elements, {changes.Count} writes planned)");

            if (changes.Count == 0)
            {
                Log.Info($"[IdAssigner] nothing to change | {result.Summary().Replace("\n", " | ")}");
                return result;
            }
            Apply(doc, docGuid, changes, result);
            if (renumber) InvalidateIndex(docGuid); // every number changed; rebuild from the model next time
            return result;
        }

        /// <summary>Every non-empty rule-parameter value in the model, for the pre-sync claims check (read-only).</summary>
        internal static List<IdClaimItem> Snapshot(Document doc, IdRulesDto rules)
        {
            var items = new List<IdClaimItem>();
            if (doc == null || rules == null) return items;

            var enabled = ResolveActive(doc, rules, out _).Where(r => !r.IsValue).ToList();
            if (enabled.Count == 0) return items;

            var resolver = new RelatedValueResolver(doc, rules);
            var elements = Collect(doc).ToList();
            foreach (var rule in enabled)
            {
                string name = rule.Dto.ParameterName.Trim();
                foreach (var el in elements)
                {
                    if (!Applies(rule.Dto, el)) continue;
                    string value = el.get_Parameter(rule.ParamGuid)?.AsString();
                    if (string.IsNullOrEmpty(value)) continue;
                    // Claimed as the unique key: "D01 [101]" when the rule is unique with the room.
                    items.Add(new IdClaimItem { Parameter = name, Uid = el.UniqueId, ElementId = el.Id.GetIdValue(),
                                                Value = rule.Pattern.UniqueKey(value, resolver.For(el)) });
                }
            }
            return items;
        }

        private class RenumberItem
        {
            public Element Element;
            public Parameter Param;
            public Func<string, string> Resolve;
        }

        // Within each counter scope, orders the elements by the rule's numbering order and numbers them 1..N.
        private static void PlanRenumber(Dictionary<ActiveRule, Dictionary<string, List<RenumberItem>>> groups, List<Change> changes)
        {
            foreach (var rg in groups)
            {
                var rule = rg.Key;
                var order = PositionOrder.Parse(rule.Dto.NumberingOrder);
                if (order == null)
                {
                    Log.Warn($"[IdAssigner] Invalid numbering order '{rule.Dto.NumberingOrder}' for '{rule.Dto.ParameterName}'; using ElementId order.");
                    order = PositionOrder.Parse("");
                }

                foreach (var items in rg.Value.Values)
                {
                    var sorted = items.Select(i => new { Item = i, Pos = ElementPosition.Of(i.Element) }).ToList();
                    sorted.Sort((a, b) => order.Compare(a.Pos, b.Pos));
                    for (int n = 0; n < sorted.Count; n++)
                    {
                        var item = sorted[n].Item;
                        string value = rule.Pattern.Render(item.Resolve, n + 1);
                        string current = item.Param.AsString();
                        if (current == value) continue;
                        changes.Add(new Change
                        {
                            Element = item.Element, Param = item.Param, NewValue = value,
                            IsReassign = !string.IsNullOrEmpty(current),
                        });
                    }
                }
            }
        }

        // Records the text of every warning Revit raises while the IDs are committed (counted per message) so
        // unexpected side effects can be diagnosed from the log. If Revit reports that it automatically resolved
        // an error by deleting or removing something (e.g. a family that regenerates badly), the whole write is
        // rolled back: an ID audit must never change the model in any other way.
        private class WarningLogger : IFailuresPreprocessor
        {
            private static readonly System.Text.RegularExpressions.Regex FamilyName =
                new System.Text.RegularExpressions.Regex("family \"([^\"]+)\"");

            public bool Blocked;
            public readonly SortedSet<string> Culprits = new SortedSet<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                try
                {
                    var counts = new Dictionary<string, int>();
                    foreach (var m in accessor.GetFailureMessages())
                    {
                        string d = m.GetDescriptionText();
                        counts.TryGetValue(d, out int n);
                        counts[d] = n + 1;

                        // Revit's "...was automatically resolved" family errors apply fixes such as Delete Type,
                        // Delete Element(s) or Remove Reference; a default resolution that deletes is also destructive.
                        bool autoResolved = d.IndexOf("automatically resolved", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool deletes = false;
                        try { deletes = m.HasResolutions() && m.GetCurrentResolutionType() == FailureResolutionType.DeleteElements; }
                        catch { /* not every message exposes a resolution type */ }
                        if (!autoResolved && !deletes) continue;

                        Blocked = true;
                        var fam = FamilyName.Match(d);
                        Culprits.Add(fam.Success ? fam.Groups[1].Value : (d.Length > 80 ? d.Substring(0, 80) : d));
                    }
                    foreach (var kv in counts)
                        Log.Warn($"[IdAssigner] Revit reported {kv.Value}x: {kv.Key}");
                }
                catch (Exception ex) { Log.Warn($"[IdAssigner] Could not read Revit warnings: {ex.Message}"); }

                if (Blocked)
                {
                    Log.Warn($"[IdAssigner] Rolling back: Revit auto-resolved errors by changing other elements ({string.Join("; ", Culprits)}).");
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                return FailureProcessingResult.Continue;
            }
        }

        private static bool VariesAcrossGroups(Parameter p) =>
            p.Definition is InternalDefinition d && d.VariesAcrossGroups;

        // Element and dimension totals, logged before and after the write so unintended deletions show up.
        private static string CountSnapshot(Document doc)
        {
            int all = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
            int dims = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Dimensions)
                .WhereElementIsNotElementType().GetElementCount();
            return $"{all} elements, {dims} dimensions";
        }

        // Records which token stopped an element getting an ID, so the audit summary can say what to configure.
        private static void NoteUnresolved(IdAssignResult result, IdPattern pattern, Func<string, string> resolve, Element el)
        {
            foreach (var t in pattern.AllTokens)
            {
                if (!string.IsNullOrEmpty(resolve(t))) continue;
                if (t == "CategoryPrefix") result.NoPrefixCategories.Add(el.Category.Name);
                else result.UnresolvedTokens.Add(t);
            }
        }

        // Whether this user could write the element now: not owned by someone else and not stale against central.
        // Unowned elements count as editable (Revit borrows them on edit).
        internal static bool CanEdit(Document doc, ElementId id)
        {
            if (!doc.IsWorkshared) return true;
            var owner = WorksharingUtils.GetCheckoutStatus(doc, id);
            if (owner == CheckoutStatus.OwnedByCurrentUser) return true;
            if (owner == CheckoutStatus.OwnedByOtherUser) return false;
            var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
            return updates != ModelUpdatesStatus.UpdatedInCentral && updates != ModelUpdatesStatus.DeletedInCentral;
        }

        // Name of the write transaction; ViolationDetector ignores it (it is not a user action).
        internal const string TransactionName = "Perseus: Assign IDs";

        private static void Apply(Document doc, string docGuid, List<Change> changes, IdAssignResult result)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var editable = new Dictionary<ElementId, bool>();
            if (doc.IsWorkshared)
            {
                // One pass over the distinct elements: classify each (owned by me / by someone else / unowned and
                // current / stale vs central) so the write loop below needs no further worksharing calls.
                var needCheckout = new List<ElementId>();
                foreach (var id in changes.Select(c => c.Element.Id).Distinct())
                {
                    var owner = WorksharingUtils.GetCheckoutStatus(doc, id);
                    if (owner == CheckoutStatus.OwnedByCurrentUser) { editable[id] = true; continue; }
                    if (owner == CheckoutStatus.OwnedByOtherUser) { editable[id] = false; continue; }

                    var updates = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                    bool ok = updates != ModelUpdatesStatus.UpdatedInCentral && updates != ModelUpdatesStatus.DeletedInCentral;
                    editable[id] = ok;
                    if (ok) needCheckout.Add(id);
                }

                // A single request for tens of thousands of elements is rejected by the central model, so ask in
                // chunks. A failed chunk isn't fatal: Revit borrows an unowned element when it is edited.
                const int chunk = 500;
                int failedChunks = 0;
                for (int i = 0; i < needCheckout.Count; i += chunk)
                {
                    try { WorksharingUtils.CheckoutElements(doc, needCheckout.Skip(i).Take(chunk).ToList()); }
                    catch (Exception ex)
                    {
                        if (++failedChunks == 1) Log.Warn($"[IdAssigner] Checkout chunk failed: {ex.Message}");
                    }
                }
                if (failedChunks > 0) Log.Warn($"[IdAssigner] {failedChunks} checkout chunk(s) failed; editing will borrow elements instead.");
                Log.Info($"[IdAssigner] timing: worksharing prep {clock.ElapsedMilliseconds} ms " +
                         $"({editable.Count} elements, {needCheckout.Count} to check out)");
            }

            bool skippedAny = false;
            long writeStart = clock.ElapsedMilliseconds;
            Log.Info($"[IdAssigner] before write: {CountSnapshot(doc)}");
            IdAutoAssignController.IsAssigning = true;
            try
            {
                using (var tx = new Transaction(doc, TransactionName))
                {
                    tx.Start();
                    var guard = new WarningLogger();
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(guard);
                    tx.SetFailureHandlingOptions(fho);
                    foreach (var c in changes)
                    {
                        try
                        {
                            // Only skip what genuinely can't be edited: owned by someone else, or stale vs central.
                            // Unowned elements are fine - Revit borrows them on edit.
                            if (doc.IsWorkshared && !(editable.TryGetValue(c.Element.Id, out bool canEdit) && canEdit))
                            {
                                result.SkippedNotOwned++;
                                // Put the index back to what the model really holds for this element, rather than
                                // throwing the whole index away (a full rebuild on large models) for one skipped write.
                                if (c.RuleName != null && _indexes.TryGetValue(docGuid, out var idx))
                                    idx.Set(c.RuleName, c.Element.Id.GetIdValue(), c.OldValue);
                                else if (!c.IsPerseusId && !c.IsValue)
                                    skippedAny = true;
                                continue;
                            }
                            if (!c.Param.Set(c.NewValue)) { result.Errors++; skippedAny |= !c.IsValue; continue; }

                            if (c.IsPerseusId) result.PerseusIdSet++;
                            else if (c.IsValue) result.ValuesWritten++;
                            else { result.Assigned++; if (c.IsReassign) result.Reassigned++; }
                        }
                        catch (Exception ex)
                        {
                            result.Errors++;
                            skippedAny = true;
                            Log.Warn($"[IdAssigner] Could not write {c.Param?.Definition?.Name} on {c.Element.UniqueId}: {ex.Message}");
                        }
                    }
                    long setMs = clock.ElapsedMilliseconds - writeStart;
                    var status = tx.Commit();
                    Log.Info($"[IdAssigner] timing: set parameters {setMs} ms, commit {clock.ElapsedMilliseconds - writeStart - setMs} ms ({status})");
                    if (status != TransactionStatus.Committed)
                    {
                        // Nothing was written: report that instead of the planned counts.
                        result.RolledBack = true;
                        result.BlockedBy.UnionWith(guard.Culprits);
                        result.PerseusIdSet = 0;
                        result.Assigned = 0;
                        result.ValuesWritten = 0;
                        result.Reassigned = 0;
                        skippedAny = true;
                    }
                    Log.Info($"[IdAssigner] after write: {CountSnapshot(doc)}");
                }
            }
            finally
            {
                IdAutoAssignController.IsAssigning = false;
            }

            // The index assumed every planned write succeeded; if any didn't, rebuild next time.
            if (skippedAny) InvalidateIndex(docGuid);
            Log.Info($"[IdAssigner] {result.Summary().Replace("\n", " | ")}");
        }

        private static IdIndex GetIndex(Document doc, string docGuid, IdRulesDto rules, List<ActiveRule> active, bool rebuild,
                                        IEnumerable<Element> all = null)
        {
            string key = docGuid ?? doc.PathName;
            if (!rebuild && _indexes.TryGetValue(key, out var existing) && existing.Signature == rules.Signature)
                return existing;

            var index = new IdIndex { Signature = rules.Signature };
            var resolver = new RelatedValueResolver(doc, rules);
            if (active.Count > 0)
                foreach (var el in all ?? Collect(doc)) Feed(index, el, active, resolver);
            _indexes[key] = index;
            return index;
        }

        private static bool Applies(IdRuleDto rule, Element el) =>
            rule.ApplyToAll || rule.Categories.Any(c => string.Equals(c, el.Category.Name, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<Element> Collect(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Where(IsEligible);

        /// <summary>True when at least one of the Perseus_ID / ID rules covers this element's category.</summary>
        internal static bool IsApplicable(IdRulesDto rules, Element el) =>
            el.Category != null &&
            (rules.PerseusId.AppliesTo(el.Category.Name) || rules.Rules.Any(r => r.Enabled && Applies(r, el)));

        internal static bool IsEligible(Element e) =>
            e != null && e.IsValidObject && !(e is ElementType) &&
            e.Category != null && e.Category.CategoryType == CategoryType.Model && e.Category.AllowsBoundParameters;
    }
}
