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

        public int TotalChanges => PerseusIdSet + Assigned;

        public string Summary() =>
            (RolledBack
                ? "NOTHING WAS WRITTEN. Revit tried to delete or remove other elements while updating these IDs, so the " +
                  "change was cancelled. Problem families: " + string.Join(", ", BlockedBy) + "\n"
                : "") +
            $"Perseus_ID written: {PerseusIdSet}\n" +
            $"Architectural IDs assigned: {Assigned} (of which {Reassigned} replaced duplicates or invalid values)\n" +
            $"Skipped (checked out by another user): {SkippedNotOwned}\n" +
            (SkippedInGroups > 0 ? $"Skipped (members of groups): {SkippedInGroups}\n" : "") +
            $"Errors: {Errors}\n" +
            $"Checked {ElementsChecked} elements against {ActiveRules} active rule(s); {ParametersFound} parameter(s) found in the model." +
            (MissingOnElement > 0 ? $"\n{MissingOnElement} element/parameter pairs had no such parameter." : "") +
            (UnresolvedTokens.Count > 0
                ? "\nSome elements were skipped because these template values were empty: " + string.Join(", ", UnresolvedTokens)
                : "") +
            (NoPrefixCategories.Count > 0
                ? "\nNo category prefix configured for: " + string.Join(", ", NoPrefixCategories.Take(12)) +
                  (NoPrefixCategories.Count > 12 ? ", ..." : "")
                : "");
    }

    /// <summary>
    /// Applies the ID rules to elements: fills Perseus_ID with the UniqueId, and keeps each rule parameter
    /// valid and unique. A value is kept if it matches the rule's pattern for the element's category and no
    /// lower-ElementId element holds the same value; otherwise the next number for that prefix is allocated.
    /// Counters come from the local model state (maximum existing number + 1).
    /// </summary>
    internal static class IdAssigner
    {
        private class ActiveRule
        {
            public IdRuleDto Dto;
            public IdPattern Pattern;
            public Guid ParamGuid;
        }

        private class Change
        {
            public Element Element;
            public Parameter Param;
            public string NewValue;
            public bool IsPerseusId;
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
                foreach (var rule in ValueById.Keys.ToList()) Set(rule, id, null);
                Uids.Remove(id);
            }

            public long LowestHolder(string rule, string value) =>
                Holders.TryGetValue(rule, out var byValue) && byValue.TryGetValue(value, out var set) && set.Count > 0
                    ? set.Min : -1;

            public void NoteNumber(string rule, string prefix, long n)
            {
                string key = rule + "|" + prefix;
                if (!Max.TryGetValue(key, out var cur) || n > cur) Max[key] = n;
            }

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
            var names = new List<string> { SharedParameterManager.PerseusIdName };
            names.AddRange(rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName))
                                      .Select(r => r.ParameterName.Trim()));
            guids = SharedParameterManager.ResolveGuids(doc, names);

            var active = new List<ActiveRule>();
            foreach (var r in rules.Rules.Where(r => r.Enabled))
            {
                var pattern = IdPattern.Parse(r.Pattern);
                string name = r.ParameterName?.Trim();
                if (pattern == null || string.IsNullOrEmpty(name) || !guids.TryGetValue(name, out var g)) continue;
                active.Add(new ActiveRule { Dto = r, Pattern = pattern, ParamGuid = g });
            }
            return active;
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
            foreach (var rule in active)
            {
                if (!Applies(rule.Dto, el)) continue;
                string name = rule.Dto.ParameterName.Trim();
                string value = el.get_Parameter(rule.ParamGuid)?.AsString();
                index.Set(name, id, value);
                if (string.IsNullOrEmpty(value)) continue;
                any = true;
                if (resolve == null) resolve = resolver.For(el);
                string scope = rule.Pattern.ScopeKey(resolve);
                if (scope != null && rule.Pattern.TryExtractNumber(value, resolve, out long n))
                    index.NoteNumber(name, scope, n);
            }
            if (any) index.Uids[id] = el.UniqueId;
            else index.Uids.Remove(id);
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
        /// </summary>
        internal static IdAssignResult Run(Document doc, string docGuid, IdRulesDto rules, ICollection<long> onlyIds,
                                           bool renumber = false, IdClashFix fix = null, bool dryRun = false)
        {
            var result = new IdAssignResult();
            if (doc == null) return result;
            if (!dryRun && (doc.IsReadOnly || doc.IsModifiable)) return result;

            var active = ResolveActive(doc, rules, out var guids);
            bool hasPerseusId = guids.TryGetValue(SharedParameterManager.PerseusIdName, out var perseusIdGuid);
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
                    var resolve = resolver.For(el);
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
                    index.Set(name, id, current); // sync index with what is actually in the model right now
                    if (!string.IsNullOrEmpty(current)) index.Uids[id] = el.UniqueId;

                    // A value the server's registry says another element holds (see IdClaims) must be replaced even
                    // though it is unique within this local model.
                    bool forced = fix != null && fix.Force.Contains(IdClashFix.ForceKey(name, id));
                    bool valid = rule.Pattern.TryExtractNumber(current, resolve, out long n);
                    if (valid)
                    {
                        index.NoteNumber(name, scope, n);
                        if (!forced && index.LowestHolder(name, current) == id) continue; // keep
                    }

                    if (dryRun)
                    {
                        // No number is allocated, so the index counters stay exactly as the model has them.
                        changes.Add(new Change { Element = el, Param = p, NewValue = null, IsReassign = !string.IsNullOrEmpty(current) });
                        continue;
                    }

                    string value;
                    do
                    {
                        value = rule.Pattern.Render(resolve, index.Allocate(name, scope));
                    } while (fix != null && fix.Taken.Contains(IdClashFix.TakenKey(name, value)));
                    index.Set(name, id, value);
                    index.Uids[id] = el.UniqueId;
                    changes.Add(new Change
                    {
                        Element = el, Param = p, NewValue = value,
                        IsReassign = !string.IsNullOrEmpty(current),
                        RuleName = name, OldValue = current,
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

            var enabled = rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName)).ToList();
            if (enabled.Count == 0) return items;
            var guids = SharedParameterManager.ResolveGuids(doc, enabled.Select(r => r.ParameterName.Trim()).ToList());

            var elements = Collect(doc).ToList();
            foreach (var rule in enabled)
            {
                string name = rule.ParameterName.Trim();
                if (!guids.TryGetValue(name, out var guid)) continue;
                foreach (var el in elements)
                {
                    if (!Applies(rule, el)) continue;
                    string value = el.get_Parameter(guid)?.AsString();
                    if (string.IsNullOrEmpty(value)) continue;
                    items.Add(new IdClaimItem { Parameter = name, Uid = el.UniqueId, ElementId = el.Id.GetIdValue(), Value = value });
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
            foreach (var t in pattern.Tokens)
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
                                else if (!c.IsPerseusId)
                                    skippedAny = true;
                                continue;
                            }
                            if (!c.Param.Set(c.NewValue)) { result.Errors++; skippedAny = true; continue; }

                            if (c.IsPerseusId) result.PerseusIdSet++;
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
