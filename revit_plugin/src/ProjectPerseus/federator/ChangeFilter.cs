using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// The review panel's filters, combined with AND. Null / empty means "any". Pure: unit-tested in
    /// ProjectPerseusTests/ChangeFilterTest.cs.
    /// </summary>
    public class ChangeFilter
    {
        public const string NeedsAttention = "Needs attention"; // Unreviewed or Query
        public const string AssigneeMe = "(Me)";
        public const string Unassigned = "(Unassigned)";

        public string Search { get; set; }
        public ChangeKind? Kind { get; set; }
        /// <summary>Only modifications with this aspect (None = any).</summary>
        public ChangeAspect Aspect { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        /// <summary>A ReviewStatus value, or NeedsAttention.</summary>
        public string Status { get; set; }
        /// <summary>A name, AssigneeMe or Unassigned (matches open actions).</summary>
        public string Assignee { get; set; }
        /// <summary>The current user, for AssigneeMe.</summary>
        public string Me { get; set; }
        /// <summary>Linked element ids visible in the active view, or null when that filter is off.</summary>
        public HashSet<long> InView { get; set; }

        public bool Matches(ElementChange c, ElementReviewState s)
        {
            if (c == null) return false;
            s = s ?? new ElementReviewState();

            if (Kind.HasValue && c.Kind != Kind.Value) return false;
            if (Aspect != ChangeAspect.None && (c.Kind != ChangeKind.Modified || (c.Aspects & Aspect) == 0)) return false;
            if (!string.IsNullOrEmpty(Category) && !Eq(c.Category, Category)) return false;
            if (!string.IsNullOrEmpty(Level) && !Eq(c.Level, Level)) return false;

            if (!string.IsNullOrEmpty(Status))
            {
                if (Status == NeedsAttention)
                {
                    if (s.Status != ReviewStatus.Unreviewed && s.Status != ReviewStatus.Query) return false;
                }
                else if (!Eq(s.Status, Status)) return false;
            }

            if (!string.IsNullOrEmpty(Assignee))
            {
                if (Assignee == Unassigned) { if (s.OpenActions.Count > 0) return false; }
                else
                {
                    string who = Assignee == AssigneeMe ? Me : Assignee;
                    if (!s.OpenActions.Any(a => Eq(a.Assignee, who))) return false;
                }
            }

            // Deleted elements aren't in the link any more, so they can't be "in the view": the filter hides them.
            if (InView != null && !InView.Contains(c.Id)) return false;

            if (!string.IsNullOrWhiteSpace(Search) && !MatchesSearch(c, s, Search.Trim())) return false;
            return true;
        }

        private static bool MatchesSearch(ElementChange c, ElementReviewState s, string q)
        {
            if (Has(c.Category, q) || Has(c.Family, q) || Has(c.Type, q) || Has(c.Level, q) || Has(c.OldType, q)
                || Has(c.UniqueId, q) || c.Id.ToString() == q || Has(ChangeDescriber.Summary(c), q))
                return true;
            if (c.ParameterChanges != null && c.ParameterChanges.Any(p => Has(p.Name, q) || Has(p.Old, q) || Has(p.New, q)))
                return true;
            return s.Events.Any(e => Has(e.Text, q) || Has(e.Author, q) || Has(e.Action?.Assignee, q) || Has(e.Action?.Type, q));
        }

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
