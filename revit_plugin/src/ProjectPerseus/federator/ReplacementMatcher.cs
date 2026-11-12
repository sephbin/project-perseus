using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// For each deleted element, suggests the added elements that probably replaced it (a beam re-modelled as a new
    /// element, a wall split in two...). Candidates must share the category and overlap or sit near the old box;
    /// the score mixes how much they overlap with type / family / level similarity. Pure (no Revit API): unit-tested
    /// in ProjectPerseusTests/ReplacementMatcherTest.cs.
    /// </summary>
    public static class ReplacementMatcher
    {
        public const double MinScore = 0.35;
        public const int MaxCandidates = 3;
        private const double Near = 1000 / 304.8;      // centres within 1 m count as "near" (feet)
        private const double Grow = 300 / 304.8;       // boxes grown by 300 mm before testing overlap
        private const double MinThickness = 10 / 304.8;
        private const double Cell = 10.0;              // spatial hash cell size (feet)
        private const int MaxCellsPerBox = 512;

        /// <param name="bonus">Optional score adjustment per pair, learnt from earlier decisions (ReplacementLearning.Bonus).</param>
        public static void Suggest(IList<ElementChange> changes, Func<ElementChange, ElementChange, double> bonus = null)
        {
            if (changes == null) return;
            var byCategory = changes
                .Where(c => c.Kind == ChangeKind.Added && c.NewMin != null && c.NewMax != null)
                .GroupBy(c => c.Category ?? "")
                .ToDictionary(g => g.Key, g => new Index(g));

            foreach (var d in changes.Where(c => c.Kind == ChangeKind.Deleted && c.OldMin != null && c.OldMax != null))
            {
                d.Replacements = null;
                if (!byCategory.TryGetValue(d.Category ?? "", out var index)) continue;
                var found = new List<ReplacementCandidate>();
                foreach (var a in index.Query(Grown(d.OldMin, -1), Grown(d.OldMax, +1)))
                {
                    var cand = Score(d, a);
                    if (cand != null && bonus != null)
                    {
                        double b = bonus(d, a);
                        if (Math.Abs(b) >= 0.01)
                        {
                            cand.Score = Math.Round(Math.Max(0, Math.Min(1, cand.Score + b)), 3);
                            cand.Reason += b > 0 ? ", linked like this before" : ", rejected like this before";
                        }
                    }
                    if (cand != null && cand.Score >= MinScore) found.Add(cand);
                }
                if (found.Count > 0)
                    d.Replacements = found.OrderByDescending(x => x.Score).ThenBy(x => x.Id).Take(MaxCandidates).ToList();
            }
        }

        /// <summary>How well an added element matches a deleted one, or null if they're not related in space.</summary>
        public static ReplacementCandidate Score(ElementChange deleted, ElementChange added)
        {
            double[] dMin = Thick(deleted.OldMin, deleted.OldMax, true), dMax = Thick(deleted.OldMin, deleted.OldMax, false);
            double[] aMin = Thick(added.NewMin, added.NewMax, true), aMax = Thick(added.NewMin, added.NewMax, false);

            double inter = 1, dVol = 1, aVol = 1;
            for (int i = 0; i < 3; i++)
            {
                double overlap = Math.Min(dMax[i], aMax[i]) - Math.Max(dMin[i], aMin[i]);
                inter *= Math.Max(0, overlap);
                dVol *= dMax[i] - dMin[i];
                aVol *= aMax[i] - aMin[i];
            }
            double iou = inter / (dVol + aVol - inter);
            double coversOld = inter / dVol;

            double dist = 0;
            for (int i = 0; i < 3; i++)
            {
                double dc = (dMin[i] + dMax[i]) / 2, ac = (aMin[i] + aMax[i]) / 2;
                dist += (dc - ac) * (dc - ac);
            }
            dist = Math.Sqrt(dist);
            double proximity = dist < Near ? 0.6 * (1 - dist / Near) : 0;

            double spatial = Math.Max(iou, Math.Max(0.8 * coversOld, proximity));
            if (spatial <= 0) return null;

            bool sameType = Same(deleted.Type, added.Type) && Same(deleted.Family, added.Family);
            bool sameFamily = Same(deleted.Family, added.Family) && !string.IsNullOrEmpty(added.Family);
            bool sameLevel = Same(deleted.Level, added.Level) && !string.IsNullOrEmpty(added.Level);
            double score = 0.5 * spatial + (sameType ? 0.25 : 0) + (sameFamily ? 0.15 : 0) + (sameLevel ? 0.10 : 0);

            var reasons = new List<string>();
            if (sameType) reasons.Add("same type");
            else if (sameFamily) reasons.Add("same family");
            if (sameLevel) reasons.Add("same level");
            if (coversOld >= 0.05) reasons.Add($"covers {Math.Round(coversOld * 100):0} % of the old one");
            else reasons.Add(ChangeDescriber.Length(dist) + " away");

            return new ReplacementCandidate
            {
                UniqueId = added.UniqueId,
                Id = added.Id,
                Family = added.Family,
                Type = added.Type,
                Score = Math.Round(Math.Min(1, score), 3),
                Reason = string.Join(", ", reasons),
            };
        }

        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);

        private static double[] Grown(double[] v, int sign) => new[] { v[0] + sign * Grow, v[1] + sign * Grow, v[2] + sign * Grow };

        // Flat boxes (a floor's top face, a line-based element) get a minimum thickness so overlap volumes aren't 0.
        private static double[] Thick(double[] min, double[] max, bool lower)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                double lo = Math.Min(min[i], max[i]), hi = Math.Max(min[i], max[i]);
                if (hi - lo < MinThickness) { double c = (lo + hi) / 2; lo = c - MinThickness / 2; hi = c + MinThickness / 2; }
                r[i] = lower ? lo : hi;
            }
            return r;
        }

        /// <summary>Spatial hash of the added elements of one category, by their (grown) boxes.</summary>
        private class Index
        {
            private readonly Dictionary<long, List<ElementChange>> _cells = new Dictionary<long, List<ElementChange>>();
            private readonly List<ElementChange> _huge = new List<ElementChange>();

            internal Index(IEnumerable<ElementChange> added)
            {
                foreach (var a in added)
                {
                    var lo = Grown(a.NewMin, -1); var hi = Grown(a.NewMax, +1);
                    if (CellCount(lo, hi) > MaxCellsPerBox) { _huge.Add(a); continue; }
                    foreach (var key in Cells(lo, hi))
                    {
                        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<ElementChange>();
                        list.Add(a);
                    }
                }
            }

            internal IEnumerable<ElementChange> Query(double[] lo, double[] hi)
            {
                var seen = new HashSet<ElementChange>(_huge);
                foreach (var a in _huge) yield return a;
                IEnumerable<long> keys = CellCount(lo, hi) > MaxCellsPerBox ? _cells.Keys.ToList() : Cells(lo, hi);
                foreach (var key in keys)
                    if (_cells.TryGetValue(key, out var list))
                        foreach (var a in list)
                            if (seen.Add(a)) yield return a;
            }

            private static long CellCount(double[] lo, double[] hi)
            {
                long n = 1;
                for (int i = 0; i < 3; i++) n *= (long)(Math.Floor(hi[i] / Cell) - Math.Floor(lo[i] / Cell) + 1);
                return n;
            }

            private static IEnumerable<long> Cells(double[] lo, double[] hi)
            {
                long x0 = (long)Math.Floor(lo[0] / Cell), x1 = (long)Math.Floor(hi[0] / Cell);
                long y0 = (long)Math.Floor(lo[1] / Cell), y1 = (long)Math.Floor(hi[1] / Cell);
                long z0 = (long)Math.Floor(lo[2] / Cell), z1 = (long)Math.Floor(hi[2] / Cell);
                for (long x = x0; x <= x1; x++)
                    for (long y = y0; y <= y1; y++)
                        for (long z = z0; z <= z1; z++)
                            yield return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
            }
        }
    }
}
