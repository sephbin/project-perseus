using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ReplacementMatcherTest
    {
        private const double M = 1000 / 304.8;

        private static ElementChange Del(string uid, double x, double len = 6, string type = "410UB54", string cat = "Structural Framing") =>
            new ElementChange
            {
                Kind = ChangeKind.Deleted, UniqueId = uid, Category = cat, Family = "UB", Type = type, Level = "L3",
                OldMin = new[] { x * M, 0, 3 * M }, OldMax = new[] { (x + len) * M, 0.2 * M, 3.4 * M },
            };

        private static ElementChange Add(string uid, double x, double len = 6, string type = "410UB54", string family = "UB",
                                         string cat = "Structural Framing", string level = "L3") =>
            new ElementChange
            {
                Kind = ChangeKind.Added, UniqueId = uid, Id = uid.GetHashCode(), Category = cat, Family = family, Type = type, Level = level,
                NewMin = new[] { x * M, 0, 3 * M }, NewMax = new[] { (x + len) * M, 0.2 * M, 3.4 * M },
            };

        [TestMethod]
        public void RemodelledInPlaceIsTheTopCandidate()
        {
            var d = Del("old", 0);
            var changes = new List<ElementChange> { d, Add("same", 0), Add("nearbyOtherType", 0.5, type: "360UB45"), Add("far", 50) };
            ReplacementMatcher.Suggest(changes);
            Assert.IsNotNull(d.Replacements);
            Assert.AreEqual("same", d.Replacements[0].UniqueId);
            StringAssert.Contains(d.Replacements[0].Reason, "same type");
            Assert.IsFalse(d.Replacements.Any(r => r.UniqueId == "far"));
        }

        [TestMethod]
        public void SplitBeamSuggestsBothHalves()
        {
            var d = Del("old", 0, 6);
            var changes = new List<ElementChange> { d, Add("left", 0, 3), Add("right", 3, 3) };
            ReplacementMatcher.Suggest(changes);
            CollectionAssert.AreEquivalent(new[] { "left", "right" }, d.Replacements.Select(r => r.UniqueId).ToArray());
        }

        [TestMethod]
        public void OtherCategoriesAndUnrelatedElementsAreIgnored()
        {
            var d = Del("old", 0);
            var changes = new List<ElementChange>
            {
                d,
                Add("column", 0, cat: "Structural Columns"),
                Add("differentEverything", 20, type: "X", family: "Y", level: "L9"),
            };
            ReplacementMatcher.Suggest(changes);
            Assert.IsNull(d.Replacements);
        }

        [TestMethod]
        public void WeakMatchesBelowThresholdAreDropped()
        {
            // Different family and level, just inside the "near" radius: spatially weak, nothing else in common.
            var d = Del("old", 0, 1);
            var a = Add("weak", 1.8, 1, type: "Other", family: "Other", level: "L9");
            Assert.IsTrue(ReplacementMatcher.Score(d, a) == null || ReplacementMatcher.Score(d, a).Score < ReplacementMatcher.MinScore);
        }
    }
}
