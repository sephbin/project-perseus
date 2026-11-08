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
    /// Status: the latest status event written in the comparison being viewed, or in any comparison when the
    /// element's fingerprint (ElementChange.Signature) is the same as when the status was set - i.e. the element
    /// hasn't changed since it was reviewed, so the review still holds (StatusCarried). An element that changed
    /// again is Unreviewed until someone looks at it.
    /// Actions: open from the event that carries them until an event closes them, across comparisons.
    /// Thread: every event on the element across all comparisons.
    /// </summary>
    public static class ReviewLog
    {
        public static string Key(string linkInstanceUid, string linkedElementUid) => linkInstanceUid + "|" + linkedElementUid;

        /// <param name="signatures">Key → current ElementChange.Signature for the comparison being viewed (optional).</param>
        public static Dictionary<string, ElementReviewState> Fold(IEnumerable<ReviewEvent> events, string comparisonId,
                                                                  IDictionary<string, string> signatures = null)
        {
            var states = new Dictionary<string, ElementReviewState>(StringComparer.Ordinal);
            if (events == null) return states;

            foreach (var e in events.Where(x => x != null)
                                    .OrderBy(x => x.CreatedUtc)
                                    .ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                if (e.Action != null && !string.IsNullOrEmpty(e.Action.Id))
                {
                    e.Action.CreatedBy = e.Author;
                    e.Action.CreatedUtc = e.CreatedUtc;
                    e.Action.ComparisonId = e.ComparisonId;
                }
                // A batch event applies to each of its elements as if it had been written for that element.
                IEnumerable<string> uids = e.Elements != null && e.Elements.Count > 0 ? (IEnumerable<string>)e.Elements : new[] { e.LinkedElementUid };
                foreach (var uid in uids) Apply(states, e, uid, comparisonId, signatures);
            }

            foreach (var s in states.Values)
                s.Assignees = string.Join(", ", s.OpenActions.Select(a => a.Assignee)
                                                             .Where(a => !string.IsNullOrWhiteSpace(a))
                                                             .Distinct(StringComparer.OrdinalIgnoreCase));
            return states;
        }

        private static void Apply(Dictionary<string, ElementReviewState> states, ReviewEvent e, string uid, string comparisonId,
                                  IDictionary<string, string> signatures)
        {
            {
                string key = Key(e.LinkInstanceUid, uid);
                if (!states.TryGetValue(key, out var s))
                {
                    s = new ElementReviewState { Key = key };
                    states.Add(key, s);
                }
                s.Events.Add(e);
                if (!string.IsNullOrWhiteSpace(e.Text)) s.CommentCount++;

                if (!string.IsNullOrEmpty(e.Status))
                {
                    bool sameComparison = string.Equals(e.ComparisonId, comparisonId, StringComparison.Ordinal);
                    string current = null;
                    signatures?.TryGetValue(key, out current);
                    string written = e.Signature;
                    if (e.Signatures != null && e.Signatures.TryGetValue(uid ?? "", out var perElement)) written = perElement;
                    bool unchangedSince = !sameComparison && !string.IsNullOrEmpty(written)
                                          && string.Equals(written, current, StringComparison.Ordinal);
                    if (sameComparison || unchangedSince)
                    {
                        s.Status = e.Status;
                        s.StatusBy = e.Author;
                        s.StatusUtc = e.CreatedUtc;
                        s.StatusCarried = !sameComparison;
                        s.StatusComparisonId = e.ComparisonId;
                    }
                }

                if (e.Action != null && !string.IsNullOrEmpty(e.Action.Id)) s.OpenActions.Add(e.Action);
                if (!string.IsNullOrEmpty(e.ClosesActionId))
                    s.OpenActions.RemoveAll(a => a.Id == e.ClosesActionId);
            }
        }
    }
}
