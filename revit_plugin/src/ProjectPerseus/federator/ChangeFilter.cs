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

        // Multi-select filters (the panel's tick lists). Null = any; an empty set matches nothing. ANDed with the single ones above.
        /// <summary>Label for an empty category / level value in the tick lists.</summary>
        public const string NoValue = "(none)";
        /// <summary>Kinds shown.</summary>
        public HashSet<ChangeKind> Kinds { get; set; }
        /// <summary>Modified changes must have one of these aspects (None = any). Only used when Modified is in Kinds (or Kinds is null).</summary>
        public ChangeAspect ModifiedAspects { get; set; }
        public HashSet<string> Categories { get; set; }
        public HashSet<string> Levels { get; set; }
        /// <summary>ReviewStatus values.</summary>
        public HashSet<string> Statuses { get; set; }
        /// <summary>Names, AssigneeMe and / or Unassigned: matches when any of them matches.</summary>
        public HashSet<string> Assignees { get; set; }

        public bool Matches(ElementChange c, ElementReviewState s)
        {
            if (c == null) return false;
            s = s ?? new ElementReviewState();

            if (Kinds != null && !Kinds.Contains(c.Kind)) return false;
            if (ModifiedAspects != ChangeAspect.None && c.Kind == ChangeKind.Modified && (c.Aspects & ModifiedAspects) == 0) return false;
            if (Categories != null && !Categories.Contains(OrNone(c.Category))) return false;
            if (Levels != null && !Levels.Contains(OrNone(c.Level))) return false;
            if (Statuses != null && !Statuses.Contains(s.Status ?? ReviewStatus.Unreviewed)) return false;
            if (Assignees != null && !Assignees.Any(a => AssigneeMatches(a, s))) return false;

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

            if (!string.IsNullOrEmpty(Assignee) && !AssigneeMatches(Assignee, s)) return false;

            // InView holds the visible linked ids plus deleted elements whose old box is in the view (FederatorWindow.ComputeInView).
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

        private bool AssigneeMatches(string assignee, ElementReviewState s)
        {
            if (assignee == Unassigned) return s.OpenActions.Count == 0;
            string who = assignee == AssigneeMe ? Me : assignee;
            return s.OpenActions.Any(a => Eq(a.Assignee, who));
        }

        /// <summary>The tick-list key of a category / level value.</summary>
        public static string OrNone(string v) => string.IsNullOrEmpty(v) ? NoValue : v;

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        private static bool Eq(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
