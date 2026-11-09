using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    public class TriageOptions
    {
        /// <summary>Group by family + type (true) or by category only.</summary>
        public bool ByType { get; set; } = true;
        /// <summary>Also split groups by the exact before → after values.</summary>
        public bool SplitByValues { get; set; }
        /// <summary>Most common transitions kept per parameter.</summary>
        public int TopTransitions { get; set; } = 3;
    }

    /// <summary>
    /// Groups changes that look alike so they can be reviewed in bulk: same category (and type), same kind, and for
    /// modifications the same aspects, the same set of changed parameters and the same move-distance bucket. Largest
    /// groups first. Pure: unit-tested in ProjectPerseusTests/ChangeTriageTest.cs.
    /// </summary>
    public static class ChangeTriage
    {
        private const double Mm = 1 / 304.8;

        public static List<TriageGroup> Group(IEnumerable<ElementChange> changes, TriageOptions options = null)
        {
            options = options ?? new TriageOptions();
            var groups = new Dictionary<string, TriageGroup>(StringComparer.Ordinal);
            foreach (var c in changes ?? Enumerable.Empty<ElementChange>())
            {
                if (c == null) continue;
                var parms = c.Kind == ChangeKind.Modified && c.ParameterChanges != null
                    ? c.ParameterChanges.Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
                string type = options.ByType ? ChangeDescriber.TypeName(c.Family, c.Type) : "";
                string description = Describe(c, parms);
                string key = string.Join("\u001f", c.Category ?? "", type, c.Kind.ToString(), description);
                if (options.SplitByValues && c.ParameterChanges != null)
                    key += "\u001f" + string.Join("\u001e", c.ParameterChanges.OrderBy(p => p.Name, StringComparer.Ordinal)
                                                                         .Select(p => p.Name + "=" + p.Old + "→" + p.New));
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new TriageGroup { Key = key, Category = c.Category ?? "", Type = type, Kind = c.Kind, Description = description, Parameters = parms };
                    groups.Add(key, g);
                }
                g.Changes.Add(c);
            }
            foreach (var g in groups.Values) g.Transitions = Transitions(g, options.TopTransitions);
            return groups.Values
                .OrderByDescending(g => g.Changes.Count)
                .ThenBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.Type, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The pattern of a change, e.g. "Parameters: Comments, Mark" or "Moved ≤ 5 mm · Type".</summary>
        public static string Describe(ElementChange c, IList<string> parameters)
        {
            switch (c.Kind)
            {
                case ChangeKind.Added: return "Added";
                case ChangeKind.Deleted: return "Deleted";
            }
            var parts = new List<string>();
            if (c.Aspects.HasFlag(ChangeAspect.Moved)) parts.Add(ChangeDescriber.MoveLabel(c) + " " + MoveBucket(c.MoveDistance));
            if (c.Aspects.HasFlag(ChangeAspect.Geometry)) parts.Add("Geometry");
            if (c.Aspects.HasFlag(ChangeAspect.Type)) parts.Add("Type");
            if (c.Aspects.HasFlag(ChangeAspect.Level)) parts.Add("Level");
            if (c.Aspects.HasFlag(ChangeAspect.Category)) parts.Add("Category");
            if (c.Aspects.HasFlag(ChangeAspect.Workset)) parts.Add("Workset");
            if (c.Aspects.HasFlag(ChangeAspect.Parameters) && parameters != null && parameters.Count > 0)
                parts.Add("Parameters: " + string.Join(", ", parameters));
            return parts.Count == 0 ? "Modified" : string.Join(" · ", parts);
        }

        public static string MoveBucket(double feet)
        {
            double mm = feet / Mm;
            if (mm <= 5) return "≤ 5 mm";
            if (mm <= 50) return "≤ 50 mm";
            if (mm <= 500) return "≤ 500 mm";
            return "> 500 mm";
        }

        private static List<ParameterTransition> Transitions(TriageGroup g, int top)
        {
            var list = new List<ParameterTransition>();
            foreach (var name in g.Parameters)
            {
                var pairs = g.Changes
                    .Select(c => c.ParameterChanges?.FirstOrDefault(p => p.Name == name))
                    .Where(p => p != null)
                    .GroupBy(p => (p.Old ?? "\u0000") + "\u001f" + (p.New ?? "\u0000"))
                    .Select(x => new { First = x.First(), Count = x.Count() })
                    .OrderByDescending(x => x.Count)
                    .ToList();
                foreach (var x in pairs.Take(top))
                    list.Add(new ParameterTransition { Parameter = name, Old = x.First.Old, New = x.First.New, Count = x.Count, Distinct = pairs.Count });
            }
            return list;
        }
    }
}
