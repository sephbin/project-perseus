using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Learns from people's replacement decisions (ReviewEvent.ReplacementOp + ReplacementPattern): per pattern
    /// (category | old type | new type, see ReplacementGroups.PatternKey) how many deleted elements were linked and how
    /// many rejected. The latest decision per deleted element counts (a link that was later broken counts as rejected).
    /// Used to re-rank ReplacementMatcher candidates (Bonus), to put learnt patterns first in ui/ReplacementWindow and
    /// to tell the user "you linked 40 like this". Learns across all links and comparisons of the host model. Pure:
    /// unit-tested in ProjectPerseusTests/ReplacementGroupsTest.cs.
    /// </summary>
    public class ReplacementLearning
    {
        public class Stats
        {
            public int Linked { get; set; }
            public int Rejected { get; set; }
            public int Total => Linked + Rejected;
        }

        private const double MaxBonus = 0.3;
        private readonly Dictionary<string, Stats> _byPattern = new Dictionary<string, Stats>(StringComparer.Ordinal);

        public static readonly ReplacementLearning Empty = new ReplacementLearning();

        public bool IsEmpty => _byPattern.Count == 0;

        public static ReplacementLearning From(IEnumerable<ReviewEvent> events)
        {
            var l = new ReplacementLearning();
            if (events == null) return l;
            // Latest decision per (link instance, deleted element).
            var latest = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);   // key -> (op, pattern)
            foreach (var e in events.Where(x => x != null && !string.IsNullOrEmpty(x.ReplacementOp) && !string.IsNullOrEmpty(x.LinkedElementUid))
                                    .OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                string key = (e.LinkInstanceUid ?? "") + "|" + e.LinkedElementUid;
                string pattern = e.ReplacementPattern;
                // A reject without a pattern keeps the pattern of the link it broke, when known.
                if (string.IsNullOrEmpty(pattern) && latest.TryGetValue(key, out var prev)) pattern = prev.Value;
                latest[key] = new KeyValuePair<string, string>(e.ReplacementOp, pattern);
            }
            foreach (var kv in latest.Values)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (!l._byPattern.TryGetValue(kv.Value, out var s)) l._byPattern[kv.Value] = s = new Stats();
                if (kv.Key == ReplacementLinks.OpLink) s.Linked++;
                else if (kv.Key == ReplacementLinks.OpReject) s.Rejected++;
            }
            return l;
        }

        /// <summary>Decisions on this pattern, or null when nobody decided one yet.</summary>
        public Stats For(string pattern) =>
            pattern != null && _byPattern.TryGetValue(pattern, out var s) && s.Total > 0 ? s : null;

        /// <summary>
        /// -1 (always rejected) .. +1 (always linked), damped for few decisions: (linked - rejected) / (total + 2).
        /// </summary>
        public double Bias(string pattern)
        {
            var s = For(pattern);
            return s == null ? 0 : (double)(s.Linked - s.Rejected) / (s.Total + 2);
        }

        /// <summary>Score adjustment for a deleted / added pair (ReplacementMatcher.Suggest).</summary>
        public double Bonus(ElementChange deleted, ElementChange added) =>
            MaxBonus * Bias(ReplacementGroups.PatternKey(deleted, added));

        /// <summary>"you linked 40 like this", "rejected 3 like this", or null.</summary>
        public string Describe(string pattern)
        {
            var s = For(pattern);
            if (s == null) return null;
            if (s.Rejected == 0) return $"linked {s.Linked:N0} like this before";
            if (s.Linked == 0) return $"rejected {s.Rejected:N0} like this before";
            return $"{s.Linked:N0} linked, {s.Rejected:N0} rejected like this before";
        }
    }
}
