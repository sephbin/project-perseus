using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Folds the append-only review events into per-element state. Pure (no Revit API): unit-tested in
    /// ProjectPerseusTests/ReviewLogTest.cs.
    ///
    /// Status is per comparison: an element accepted in last month's comparison is Unreviewed again when it
    /// changes in this month's. The comment thread is per element across every comparison, so earlier
    /// discussion stays visible.
    /// </summary>
    public static class ReviewLog
    {
        public static string Key(string linkInstanceUid, string linkedElementUid) => linkInstanceUid + "|" + linkedElementUid;

        public static Dictionary<string, ElementReviewState> Fold(IEnumerable<ReviewEvent> events, string comparisonId)
        {
            var states = new Dictionary<string, ElementReviewState>(StringComparer.Ordinal);
            if (events == null) return states;

            foreach (var e in events.Where(x => x != null)
                                    .OrderBy(x => x.CreatedUtc)
                                    .ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                string key = Key(e.LinkInstanceUid, e.LinkedElementUid);
                if (!states.TryGetValue(key, out var s))
                {
                    s = new ElementReviewState { Key = key };
                    states.Add(key, s);
                }
                s.Events.Add(e);
                if (!string.IsNullOrEmpty(e.Status) && string.Equals(e.ComparisonId, comparisonId, StringComparison.Ordinal))
                {
                    s.Status = e.Status;
                    s.StatusBy = e.Author;
                    s.StatusUtc = e.CreatedUtc;
                }
            }
            return states;
        }
    }
}
