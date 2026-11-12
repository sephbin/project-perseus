using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ReplacementGroupsTest
    {
        private const double M = 1000 / 304.8;
        private static DateTime _t = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

        private static ElementChange Del(string uid, double x, string type = "200") => new ElementChange
        {
            Kind = ChangeKind.Deleted, UniqueId = uid, Category = "Walls", Family = "Basic Wall", Type = type,
            OldMin = new[] { x * M, 0, 0 }, OldMax = new[] { (x + 5) * M, 0.2 * M, 3 * M },
        };

        private static ElementChange Add(string uid, double x, string type = "200", double len = 5) => new ElementChange
        {
            Kind = ChangeKind.Added, UniqueId = uid, Category = "Walls", Family = "Basic Wall", Type = type,
            NewMin = new[] { x * M, 0, 0 }, NewMax = new[] { (x + len) * M, 0.2 * M, 3 * M },
        };

        private static ReplacementPair Pair(ElementChange d, ElementChange a, double score) => new ReplacementPair { Deleted = d, Added = a, Score = score };

        private static ReviewEvent Ev(string old, string op, string pattern) => new ReviewEvent
        {
            Id = Guid.NewGuid().ToString("N"), LinkInstanceUid = "L", LinkedElementUid = old, ReplacementOp = op,
            ReplacementPattern = pattern, CreatedUtc = _t = _t.AddMinutes(1),
        };

        [TestMethod]
        public void SameTypeDifferentSizesAreSeparateGroups()
        {
            var small = Del("d1", 0); small.OldMax = new[] { 5 * M, 0.1 * M, 0.1 * M };    // 100 x 100 section
            var big = Del("d2", 20); big.OldMin = new[] { 20 * M, 0, 0 }; big.OldMax = new[] { 30 * M, 0.3 * M, 0.3 * M };   // 300 x 300, longer
            var small2 = Del("d3", 40); small2.OldMin = new[] { 40 * M, 0, 0 }; small2.OldMax = new[] { 52 * M, 0.1 * M, 0.1 * M };   // same section, longer
            var a1 = Add("a1", 0); a1.NewMax = (double[])small.OldMax.Clone();
            var a2 = Add("a2", 20); a2.NewMin = (double[])big.OldMin.Clone(); a2.NewMax = (double[])big.OldMax.Clone();
            var a3 = Add("a3", 40); a3.NewMin = (double[])small2.OldMin.Clone(); a3.NewMax = (double[])small2.OldMax.Clone();
            var groups = ReplacementGroups.Build(new[] { Pair(small, a1, 0.9), Pair(big, a2, 0.9), Pair(small2, a3, 0.9) }, null);
            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("100 × 100 mm", groups[0].Size);
            Assert.AreEqual(2, groups[0].Pairs.Count);
        }

        [TestMethod]
        public void RelationBuckets()
        {
            Assert.AreEqual(ReplacementGroups.SamePlace, ReplacementGroups.Relation(Del("d", 0), Add("a", 0)));
            Assert.AreEqual(ReplacementGroups.Resized, ReplacementGroups.Relation(Del("d", 0), Add("a", -0.5, len: 6)));
            Assert.AreEqual(ReplacementGroups.Shifted, ReplacementGroups.Relation(Del("d", 0), Add("a", 0.2)));
            Assert.AreEqual(ReplacementGroups.Moved, ReplacementGroups.Relation(Del("d", 0), Add("a", 2)));
        }

        [TestMethod]
        public void GroupsByPatternAndRelationBiggestConfidentFirst()
        {
            var pairs = new List<ReplacementPair>();
            for (int i = 0; i < 5; i++) pairs.Add(Pair(Del("d" + i, i * 10), Add("a" + i, i * 10), 0.95));
            for (int i = 0; i < 3; i++) pairs.Add(Pair(Del("e" + i, i * 10, "100"), Add("b" + i, i * 10, "150"), 0.7));
            pairs.Add(Pair(Del("f", 100), Add("c", 102), 0.9));
            var groups = ReplacementGroups.Build(pairs, null);
            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual(5, groups[0].Pairs.Count);
            Assert.AreEqual(ReplacementGroups.SamePlace, groups[0].Relation);
            Assert.AreEqual("Walls · Basic Wall : 200 → same type", groups[0].Title);
            Assert.AreEqual(ReplacementGroups.Moved, groups[1].Relation);   // confident (90 %) before the 70 % group
            Assert.AreEqual(3, groups[2].Pairs.Count);
        }

        [TestMethod]
        public void LearntPatternComesFirstAndRejectedLast()
        {
            string linked = ReplacementGroups.PatternKey(Del("x", 0, "100"), Add("y", 0, "150"));
            string rejected = ReplacementGroups.PatternKey(Del("x", 0), Add("y", 0));
            var learnt = ReplacementLearning.From(new[]
            {
                Ev("o1", ReplacementLinks.OpLink, linked), Ev("o2", ReplacementLinks.OpLink, linked),
                Ev("o3", ReplacementLinks.OpReject, rejected), Ev("o4", ReplacementLinks.OpReject, rejected),
            });
            var pairs = new List<ReplacementPair>
            {
                Pair(Del("d", 0), Add("a", 0), 0.95),
                Pair(Del("e", 20, "100"), Add("b", 20, "150"), 0.6),
            };
            var groups = ReplacementGroups.Build(pairs, learnt);
            Assert.AreEqual(linked, groups[0].Pattern);
            Assert.AreEqual("linked 2 like this before", groups[0].Learnt);
            Assert.IsTrue(groups[1].LearntBias < 0);
            Assert.IsTrue(learnt.Bonus(Del("x", 0, "100"), Add("y", 0, "150")) > 0);
        }

        [TestMethod]
        public void LatestDecisionPerElementCountsAndBreakKeepsPattern()
        {
            string p = ReplacementGroups.PatternKey(Del("x", 0), Add("y", 0));
            var learnt = ReplacementLearning.From(new[] { Ev("o1", ReplacementLinks.OpLink, p), Ev("o1", ReplacementLinks.OpReject, null) });
            var s = learnt.For(p);
            Assert.AreEqual(0, s.Linked);
            Assert.AreEqual(1, s.Rejected);
        }

        [TestMethod]
        public void MatcherUsesLearntBonus()
        {
            // Different type, shifted 600 mm: below the threshold alone, suggested once that type change was linked before.
            var d = Del("d", 0, "100");
            var a = Add("a", 0.6, "150");
            a.Family = "Other Wall";
            var plain = new List<ElementChange> { d, a };
            ReplacementMatcher.Suggest(plain);
            double before = d.Replacements?.FirstOrDefault()?.Score ?? 0;
            string pattern = ReplacementGroups.PatternKey(d, a);
            var learnt = ReplacementLearning.From(Enumerable.Range(0, 6).Select(i => Ev("o" + i, ReplacementLinks.OpLink, pattern)));
            ReplacementMatcher.Suggest(plain, learnt.Bonus);
            Assert.IsNotNull(d.Replacements);
            Assert.IsTrue(d.Replacements[0].Score > before);
            StringAssert.Contains(d.Replacements[0].Reason, "linked like this before");
        }

        [TestMethod]
        public void FrameMapsHostPointsToPixels()
        {
            var f = new ViewImageFrame { Origin = new[] { 100.0, 50, 0 }, U0 = 0, U1 = 100, V0 = 0, V1 = 50, Width = 2000, Height = 1000 };
            f.ToPixel(100, 50, 0, out double x, out double y);
            Assert.AreEqual(0, x, 1e-9);
            Assert.AreEqual(1000, y, 1e-9);   // bottom-left of the model rect = bottom-left pixel
            f.ToPixel(150, 75, 10, out x, out y);
            Assert.AreEqual(1000, x, 1e-9);
            Assert.AreEqual(500, y, 1e-9);
            Assert.IsTrue(f.Covers(new[] { 140.0, 70, 0 }, new[] { 160.0, 80, 3 }));
            Assert.IsFalse(f.Covers(new[] { 300.0, 70, 0 }, new[] { 310.0, 80, 3 }));
        }

        [TestMethod]
        public void ThumbnailWindowKeepsAspectAndStaysInside()
        {
            var f = new ViewImageFrame { U0 = 0, U1 = 100, V0 = 0, V1 = 50, Width = 2000, Height = 1000 };
            f.Window(5, 5, 15, 10, 1.6, 4000 / 304.8, out double x0, out double y0, out double x1, out double y1);
            Assert.AreEqual(1.6, (x1 - x0) / (y1 - y0), 1e-6);
            Assert.AreEqual(0, x0, 1e-9);
            Assert.AreEqual(0, y0, 1e-9);
            Assert.IsTrue(x1 - x0 >= 4000 / 304.8 * 20 - 1e-6);   // at least 4 m at 20 px / ft
        }
    }
}
