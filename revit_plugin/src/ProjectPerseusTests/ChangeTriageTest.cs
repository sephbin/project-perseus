using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ChangeTriageTest
    {
        private const double Mm = 1 / 304.8;

        private static ElementChange Params(string uid, string type, params string[] nameOldNew)
        {
            var c = new ElementChange
            {
                Kind = ChangeKind.Modified, Aspects = ChangeAspect.Parameters, UniqueId = uid,
                Category = "Sprinklers", Family = "Pendent", Type = type,
            };
            for (int i = 0; i + 2 < nameOldNew.Length + 1; i += 3)
                c.ParameterChanges.Add(new ParameterChange { Name = nameOldNew[i], Old = nameOldNew[i + 1], New = nameOldNew[i + 2] });
            return c;
        }

        private static ElementChange Moved(string uid, double mm, ChangeAspect extra = ChangeAspect.None) => new ElementChange
        {
            Kind = ChangeKind.Modified, Aspects = ChangeAspect.Moved | extra, UniqueId = uid, MoveDistance = mm * Mm,
            Category = "Sprinklers", Family = "Pendent", Type = "K80",
        };

        [TestMethod]
        public void SameParameterSetsGroupTogetherLargestFirst()
        {
            var changes = new List<ElementChange>
            {
                Params("a", "K80", "Mark", "1", "2", "Comments", "", "x"),
                Params("b", "K80", "Comments", "", "y", "Mark", "3", "4"),   // same set, different order and values
                Params("c", "K80", "Mark", "5", "6"),
                Params("d", "K115", "Mark", "1", "2", "Comments", "", "x"), // other type
            };
            var groups = ChangeTriage.Group(changes);
            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual(2, groups[0].Changes.Count);
            Assert.AreEqual("Parameters: Comments, Mark", groups[0].Description);
            Assert.AreEqual("Pendent : K80", groups[0].Type);

            // Category-only grouping merges the two types.
            var byCategory = ChangeTriage.Group(changes, new TriageOptions { ByType = false });
            Assert.AreEqual(3, byCategory.Max(g => g.Changes.Count));
        }

        [TestMethod]
        public void SplitByValuesSeparatesDifferentTransitions()
        {
            var changes = new List<ElementChange>
            {
                Params("a", "K80", "System Classification", "Wet", "Wet Sprinkler"),
                Params("b", "K80", "System Classification", "Wet", "Wet Sprinkler"),
                Params("c", "K80", "System Classification", "Dry", "Dry Sprinkler"),
            };
            Assert.AreEqual(1, ChangeTriage.Group(changes).Count);
            var split = ChangeTriage.Group(changes, new TriageOptions { SplitByValues = true });
            CollectionAssert.AreEqual(new[] { 2, 1 }, split.Select(g => g.Changes.Count).ToArray());
        }

        [TestMethod]
        public void TransitionsListTheMostCommonValuesFirst()
        {
            var changes = new List<ElementChange>
            {
                Params("a", "K80", "System Classification", "Wet", "Wet Sprinkler"),
                Params("b", "K80", "System Classification", "Wet", "Wet Sprinkler"),
                Params("c", "K80", "System Classification", "Dry", "Dry Sprinkler"),
            };
            var t = ChangeTriage.Group(changes)[0].Transitions;
            Assert.AreEqual(2, t.Count);
            Assert.AreEqual("Wet", t[0].Old);
            Assert.AreEqual("Wet Sprinkler", t[0].New);
            Assert.AreEqual(2, t[0].Count);
            Assert.AreEqual(2, t[0].Distinct);
        }

        [TestMethod]
        public void MovesAreBucketedByDistance()
        {
            var groups = ChangeTriage.Group(new[] { Moved("a", 2), Moved("b", 4), Moved("c", 30), Moved("d", 800) });
            CollectionAssert.AreEquivalent(new[] { "Moved ≤ 5 mm", "Moved ≤ 50 mm", "Moved > 500 mm" }, groups.Select(g => g.Description).ToArray());
            Assert.AreEqual(2, groups.Single(g => g.Description == "Moved ≤ 5 mm").Changes.Count);
        }

        [TestMethod]
        public void VerticalMovesGroupApartFromOtherMoves()
        {
            var up = Moved("a", 30); up.MoveVector = new[] { 0, 0, 30 * Mm };
            var side = Moved("b", 30); side.MoveVector = new[] { 30 * Mm, 0, 0 };
            var both = Moved("c", 30); both.MoveVector = new[] { 20 * Mm, 0, 22 * Mm };
            var groups = ChangeTriage.Group(new[] { up, side, both });
            CollectionAssert.AreEquivalent(new[] { "Moved vertically ≤ 50 mm", "Moved ≤ 50 mm" }, groups.Select(g => g.Description).ToArray());
            Assert.AreEqual(2, groups.Single(g => g.Description == "Moved ≤ 50 mm").Changes.Count);
        }

        [TestMethod]
        public void NoiseIsOnlyNoiseWhenNothingElseChanged()
        {
            var rules = new NoiseRules { Parameters = new List<string> { "Mark", "Comments" }, MinMoveMm = 5 };
            var set = NoiseFilter.ParameterSet(rules);
            Assert.IsTrue(NoiseFilter.IsNoise(Params("a", "K80", "Mark", "1", "2"), rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(Params("b", "K80", "Mark", "1", "2", "Flow", "60", "80"), rules, set));
            Assert.IsTrue(NoiseFilter.IsNoise(Moved("c", 2), rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(Moved("d", 20), rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(Moved("e", 2, ChangeAspect.Type), rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(new ElementChange { Kind = ChangeKind.Added }, rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(Params("f", "K80", "Mark", "1", "2"), null, set));
        }

        [TestMethod]
        public void BatchEventsApplyToEachElementWithTheirOwnSignature()
        {
            var batch = new ReviewEvent
            {
                Id = "1", ComparisonId = "c1", LinkInstanceUid = "link", Author = "alice",
                CreatedUtc = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), Status = ReviewStatus.Accepted,
                Elements = new List<string> { "e1", "e2", "e3" },
                Signatures = new Dictionary<string, string> { { "e1", "s1" }, { "e2", "s2" }, { "e3", "s3" } },
            };
            var states = ReviewLog.Fold(new[] { batch }, "c1");
            Assert.AreEqual(3, states.Count);
            Assert.IsTrue(states.Values.All(s => s.Status == ReviewStatus.Accepted));

            // In a later comparison only the element whose signature is unchanged keeps the status.
            var later = ReviewLog.Fold(new[] { batch }, "c2", new Dictionary<string, string>
            {
                { ReviewLog.Key("link", "e1"), "s1" }, { ReviewLog.Key("link", "e2"), "changed" },
            });
            Assert.AreEqual(ReviewStatus.Accepted, later[ReviewLog.Key("link", "e1")].Status);
            Assert.AreEqual(ReviewStatus.Unreviewed, later[ReviewLog.Key("link", "e2")].Status);
        }
        [TestMethod]
        public void NoiseCategoriesHideEveryKindOfChange()
        {
            var rules = new NoiseRules { Categories = new List<string> { "sprinklers" } };
            var set = NoiseFilter.ParameterSet(rules);
            var added = new ElementChange { Kind = ChangeKind.Added, Category = "Sprinklers" };
            Assert.IsTrue(NoiseFilter.IsNoise(added, rules, set));
            Assert.IsNull(NoiseFilter.Effective(added, rules, set));
            Assert.IsNull(NoiseFilter.Effective(Params("a", "K80", "Flow", "60", "80"), rules, set));
            var wall = new ElementChange { Kind = ChangeKind.Deleted, Category = "Walls" };
            Assert.AreSame(wall, NoiseFilter.Effective(wall, rules, set));
            Assert.IsFalse(NoiseFilter.IsNoise(wall, rules, set));
        }

        [TestMethod]
        public void EffectiveChangeDropsNoiseButKeepsTheRest()
        {
            var rules = new NoiseRules { Parameters = new List<string> { "Mark" }, MinMoveMm = 5 };
            var set = NoiseFilter.ParameterSet(rules);

            var mixed = Params("a", "K80", "Mark", "1", "2", "Flow", "60", "80");
            var e = NoiseFilter.Effective(mixed, rules, set);
            Assert.AreNotSame(mixed, e);
            CollectionAssert.AreEqual(new[] { "Flow" }, e.ParameterChanges.Select(p => p.Name).ToArray());
            Assert.AreEqual(2, mixed.ParameterChanges.Count, "the stored change is untouched");

            Assert.IsNull(NoiseFilter.Effective(Params("b", "K80", "Mark", "1", "2"), rules, set), "only noise");
            var clean = Params("c", "K80", "Flow", "60", "80");
            Assert.AreSame(clean, NoiseFilter.Effective(clean, rules, set), "no noise: same instance");

            var smallMoveAndFlow = Moved("d", 2, ChangeAspect.Parameters);
            smallMoveAndFlow.ParameterChanges.Add(new ParameterChange { Name = "Flow", Old = "60", New = "80" });
            var eff = NoiseFilter.Effective(smallMoveAndFlow, rules, set);
            Assert.AreEqual(ChangeAspect.Parameters, eff.Aspects);

            // Triage on effective changes: "Mark" no longer splits the groups.
            var groups = ChangeTriage.Group(new[] { e, NoiseFilter.Effective(clean, rules, set) });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("Parameters: Flow", groups[0].Description);
        }
    }
}
