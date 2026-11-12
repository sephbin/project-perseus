using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>A suggested (deleted -> added) replacement, as grouped by ReplacementGroups. Tag = the caller's own object.</summary>
    public class ReplacementPair
    {
        public ElementChange Deleted { get; set; }
        public ElementChange Added { get; set; }
        public double Score { get; set; }
        public object Tag { get; set; }
        public string Pattern { get; set; }
        public string Relation { get; set; }
    }

    /// <summary>Pairs of one pattern (category, old type -> new type) and spatial relation, best first.</summary>
    public class ReplacementGroup
    {
        public string Key { get; set; }
        public string Pattern { get; set; }
        public string Relation { get; set; }
        public string Category { get; set; }
        public string OldType { get; set; }
        public string NewType { get; set; }
        public List<ReplacementPair> Pairs { get; set; } = new List<ReplacementPair>();
        public double MeanScore { get; set; }
        public double MinScore { get; set; }
        /// <summary>Learnt from earlier decisions: +1 always linked .. -1 always rejected (ReplacementLearning.Bias).</summary>
        public double LearntBias { get; set; }
        public string Learnt { get; set; }
        public ReplacementPair Example => Pairs.Count > 0 ? Pairs[0] : null;

        public string Title => OldType == NewType
            ? $"{Category} · {OldType} → same type"
            : $"{Category} · {OldType} → {NewType}";
    }

    /// <summary>
    /// Groups replacement suggestions so they can be approved in bulk: same category, same old type -> new type, same
    /// spatial relation (identical place and size / same place resized / shifted / moved). Learnt patterns come first,
    /// then confident groups (mean match 80 %+), then by size; rejected patterns last. Pure: unit-tested in
    /// ProjectPerseusTests/ReplacementGroupsTest.cs.
    /// </summary>
    public static class ReplacementGroups
    {
        public const string SamePlace = "identical place and size";
        public const string Resized = "same place, resized";
        public const string Shifted = "shifted up to 300 mm";
        public const string Moved = "moved over 300 mm";
        private const double Tol = 25 / 304.8;
        private const double ShiftLimit = 300 / 304.8;

        /// <summary>"category | old type | new type" (type = "Family: Type").</summary>
        public static string PatternKey(ElementChange deleted, ElementChange added) =>
            PatternKey(deleted?.Category, deleted?.Family, deleted?.Type, added?.Family, added?.Type);

        public static string PatternKey(string category, string oldFamily, string oldType, string newFamily, string newType) =>
            $"{category ?? ""}|{ChangeDescriber.TypeName(oldFamily, oldType)}|{ChangeDescriber.TypeName(newFamily, newType)}";

        /// <summary>How the new element sits relative to the old one (link coordinates).</summary>
        public static string Relation(ElementChange deleted, ElementChange added)
        {
            var a = deleted?.OldMin; var b = deleted?.OldMax; var c = added?.NewMin; var d = added?.NewMax;
            if (a == null || b == null || c == null || d == null) return Moved;
            double dist = 0, size = 0;
            for (int i = 0; i < 3; i++)
            {
                double dc = (a[i] + b[i]) / 2 - (c[i] + d[i]) / 2;
                dist += dc * dc;
                size = Math.Max(size, Math.Abs(Math.Abs(b[i] - a[i]) - Math.Abs(d[i] - c[i])));
            }
            dist = Math.Sqrt(dist);
            if (dist <= Tol) return size <= Tol ? SamePlace : Resized;
            return dist <= ShiftLimit ? Shifted : Moved;
        }

        private static int RelationOrder(string r) => r == SamePlace ? 0 : r == Resized ? 1 : r == Shifted ? 2 : 3;

        public static List<ReplacementGroup> Build(IEnumerable<ReplacementPair> pairs, ReplacementLearning learnt)
        {
            learnt = learnt ?? ReplacementLearning.Empty;
            var groups = new List<ReplacementGroup>();
            foreach (var p in pairs ?? Enumerable.Empty<ReplacementPair>())
            {
                p.Pattern = p.Pattern ?? PatternKey(p.Deleted, p.Added);
                p.Relation = p.Relation ?? Relation(p.Deleted, p.Added);
            }
            foreach (var g in (pairs ?? Enumerable.Empty<ReplacementPair>()).GroupBy(p => p.Pattern + "|" + p.Relation))
            {
                var list = g.OrderByDescending(p => p.Score).ThenBy(p => p.Deleted?.Id ?? 0).ToList();
                var first = list[0];
                groups.Add(new ReplacementGroup
                {
                    Key = g.Key,
                    Pattern = first.Pattern,
                    Relation = first.Relation,
                    Category = string.IsNullOrEmpty(first.Deleted?.Category) ? "(no category)" : first.Deleted.Category,
                    OldType = Name(first.Deleted),
                    NewType = Name(first.Added),
                    Pairs = list,
                    MeanScore = list.Average(p => p.Score),
                    MinScore = list.Min(p => p.Score),
                    LearntBias = learnt.Bias(first.Pattern),
                    Learnt = learnt.Describe(first.Pattern),
                });
            }
            return groups
                .OrderBy(g => g.LearntBias > 0.2 ? 0 : g.LearntBias < -0.2 ? 3 : g.MeanScore >= 0.8 ? 1 : 2)
                .ThenBy(g => RelationOrder(g.Relation))
                .ThenByDescending(g => g.Pairs.Count)
                .ThenByDescending(g => g.MeanScore)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
        }

        private static string Name(ElementChange c)
        {
            string n = ChangeDescriber.TypeName(c?.Family, c?.Type);
            return string.IsNullOrEmpty(n) ? "(no type)" : n;
        }
    }
}
