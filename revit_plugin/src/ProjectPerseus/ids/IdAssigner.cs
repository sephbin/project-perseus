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
        public int Errors;
        public int ElementsChecked;
        public int ActiveRules;
        public int ParametersFound;     // Perseus_ID + rule parameters actually bound in this model
        public int MissingOnElement;    // element did not carry the parameter (not bound to its category)
        public readonly SortedSet<string> NoPrefixCategories = new SortedSet<string>();

        public int TotalChanges => PerseusIdSet + Assigned;

        public string Summary() =>
            $"Perseus_ID written: {PerseusIdSet}\n" +
            $"Architectural IDs assigned: {Assigned} (of which {Reassigned} replaced duplicates or invalid values)\n" +
            $"Skipped (checked out by another user): {SkippedNotOwned}\n" +
            $"Errors: {Errors}\n" +
            $"Checked {ElementsChecked} elements against {ActiveRules} active rule(s); {ParametersFound} parameter(s) found in the model." +
            (MissingOnElement > 0 ? $"\n{MissingOnElement} element/parameter pairs had no such parameter." : "") +
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
        }

        // Per-document index of rule values, so edits don't rescan the whole model. Rebuilt on full runs,
        // after Reload Latest / sync, when the rules change, or when any planned write is skipped.
        private class IdIndex
        {
            public string Signature;
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

        /// <summary>
        /// Runs the rules over <paramref name="onlyIds"/>, or the whole model when null (audit).
        /// Opens its own transaction; must not be called while the document is modifiable.
        /// </summary>
        internal static IdAssignResult Run(Document doc, string docGuid, IdRulesDto rules, ICollection<long> onlyIds)
        {
            var result = new IdAssignResult();
            if (doc == null || doc.IsReadOnly || doc.IsModifiable) return result;

            var names = new List<string> { SharedParameterManager.PerseusIdName };
            names.AddRange(rules.Rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ParameterName))
                                      .Select(r => r.ParameterName.Trim()));
            var guids = SharedParameterManager.ResolveGuids(doc, names);

            var active = new List<ActiveRule>();
            foreach (var r in rules.Rules.Where(r => r.Enabled))
            {
                var pattern = IdPattern.Parse(r.Pattern);
                string name = r.ParameterName?.Trim();
                if (pattern == null || string.IsNullOrEmpty(name) || !guids.TryGetValue(name, out var g)) continue;
                active.Add(new ActiveRule { Dto = r, Pattern = pattern, ParamGuid = g });
            }
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
            foreach (var el in ordered)
            {
                long id = el.Id.GetIdValue();

                if (hasPerseusId)
                {
                    var p = el.get_Parameter(perseusIdGuid);
                    if (p == null) result.MissingOnElement++;
                    if (p != null && !p.IsReadOnly && p.AsString() != el.UniqueId)
                        changes.Add(new Change { Element = el, Param = p, NewValue = el.UniqueId, IsPerseusId = true });
                }

                foreach (var rule in active)
                {
                    if (!Applies(rule.Dto, el)) continue;
                    var p = el.get_Parameter(rule.ParamGuid);
                    if (p == null) result.MissingOnElement++;
                    if (p == null || p.IsReadOnly) continue;

                    string prefix = "";
                    if (rule.Pattern.UsesCategoryPrefix)
                    {
                        prefix = rules.PrefixFor(el.Category.Name);
                        if (string.IsNullOrEmpty(prefix)) { result.NoPrefixCategories.Add(el.Category.Name); continue; } // no prefix configured
                    }

                    string name = rule.Dto.ParameterName.Trim();
                    string current = p.AsString();
                    index.Set(name, id, current); // sync index with what is actually in the model right now

                    bool valid = rule.Pattern.TryExtractNumber(current, prefix, out long n);
                    if (valid)
                    {
                        index.NoteNumber(name, prefix, n);
                        if (index.LowestHolder(name, current) == id) continue; // keep
                    }

                    long next = index.Allocate(name, prefix);
                    string value = rule.Pattern.Render(prefix, next);
                    index.Set(name, id, value);
                    changes.Add(new Change
                    {
                        Element = el, Param = p, NewValue = value,
                        IsReassign = !string.IsNullOrEmpty(current),
                    });
                }
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
            return result;
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
            IdAutoAssignController.IsAssigning = true;
            try
            {
                using (var tx = new Transaction(doc, TransactionName))
                {
                    tx.Start();
                    foreach (var c in changes)
                    {
                        try
                        {
                            // Only skip what genuinely can't be edited: owned by someone else, or stale vs central.
                            // Unowned elements are fine - Revit borrows them on edit.
                            if (doc.IsWorkshared && !(editable.TryGetValue(c.Element.Id, out bool canEdit) && canEdit))
                            {
                                result.SkippedNotOwned++;
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
                    tx.Commit();
                    Log.Info($"[IdAssigner] timing: set parameters {setMs} ms, commit {clock.ElapsedMilliseconds - writeStart - setMs} ms");
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
            if (active.Count > 0)
            {
                foreach (var el in all ?? Collect(doc))
                {
                    long id = el.Id.GetIdValue();
                    foreach (var rule in active)
                    {
                        if (!Applies(rule.Dto, el)) continue;
                        var p = el.get_Parameter(rule.ParamGuid);
                        if (p == null) continue;
                        string value = p.AsString();
                        if (string.IsNullOrEmpty(value)) continue;

                        string name = rule.Dto.ParameterName.Trim();
                        index.Set(name, id, value);
                        string prefix = rule.Pattern.UsesCategoryPrefix ? (rules.PrefixFor(el.Category.Name) ?? "") : "";
                        if (rule.Pattern.TryExtractNumber(value, prefix, out long n))
                            index.NoteNumber(name, prefix, n);
                    }
                }
            }
            _indexes[key] = index;
            return index;
        }

        private static bool Applies(IdRuleDto rule, Element el) =>
            rule.ApplyToAll || rule.Categories.Any(c => string.Equals(c, el.Category.Name, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<Element> Collect(Document doc) =>
            new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().Where(IsEligible);

        private static bool IsEligible(Element e) =>
            e != null && e.IsValidObject && !(e is ElementType) &&
            e.Category != null && e.Category.CategoryType == CategoryType.Model && e.Category.AllowsBoundParameters;
    }
}
