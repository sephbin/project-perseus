using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ReplacementLinksTest
    {
        private const string Link = "link-instance";
        private const double M = 1000 / 304.8;
        private static DateTime _t = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

        private static ReviewEvent Ev(string old, string op, string newUid = null) => new ReviewEvent
        {
            Id = Guid.NewGuid().ToString("N"), LinkInstanceUid = Link, LinkedElementUid = old, LinkedElementId = old.GetHashCode(),
            ReplacementOp = op, ReplacedByUid = newUid, ReplacedById = newUid?.GetHashCode() ?? 0, CreatedUtc = _t = _t.AddMinutes(1), Author = "me",
        };

        private static ElementChange Del(string uid, double x) => new ElementChange
        {
            Kind = ChangeKind.Deleted, UniqueId = uid, Id = 1, Category = "Walls", Family = "Basic Wall", Type = "200", Level = "L1", Signature = "d",
            OldMin = new[] { x * M, 0, 0 }, OldMax = new[] { (x + 5) * M, 0.2 * M, 3 * M },
        };

        private static ElementChange Add(string uid, double x, string type = "200") => new ElementChange
        {
            Kind = ChangeKind.Added, UniqueId = uid, Id = 2, Category = "Walls", Family = "Basic Wall", Type = type, Level = "L1", Signature = "a",
            NewMin = new[] { x * M, 0, 0 }, NewMax = new[] { (x + 5) * M, 0.2 * M, 3 * M },
        };

        [TestMethod]
        public void LinkedPairBecomesOneReplacedChange()
        {
            var changes = new List<ElementChange> { Del("old", 0), Add("new", 0.1, "250"), Add("other", 20) };
            var state = ReplacementLinks.Fold(new[] { Ev("old", ReplacementLinks.OpLink, "new") }, Link);
            var shown = ReplacementLinks.Apply(changes, state);
            Assert.AreEqual(2, shown.Count);
            var r = shown[0];
            Assert.AreEqual(ChangeKind.Replaced, r.Kind);
            Assert.AreEqual("new", r.UniqueId);
            Assert.AreEqual("old", r.OldUniqueId);
            Assert.IsTrue(r.Aspects.HasFlag(ChangeAspect.Moved));
            Assert.IsTrue(r.Aspects.HasFlag(ChangeAspect.Type));
            Assert.AreEqual(0.1 * M, r.MoveDistance, 1e-9);
            Assert.AreEqual("other", shown[1].UniqueId);
        }

        [TestMethod]
        public void RejectBreaksTheLinkAndIsRemembered()
        {
            var state = ReplacementLinks.Fold(new[] { Ev("old", ReplacementLinks.OpLink, "new"), Ev("old", ReplacementLinks.OpReject) }, Link);
            Assert.AreEqual(0, state.ByOld.Count);
            Assert.IsTrue(state.Rejected.Contains("old"));
            var shown = ReplacementLinks.Apply(new List<ElementChange> { Del("old", 0), Add("new", 0) }, state);
            Assert.IsFalse(shown.Any(c => c.Kind == ChangeKind.Replaced));
        }

        [TestMethod]
        public void LinksAreOneToOneNewestWins()
        {
            var state = ReplacementLinks.Fold(new[] { Ev("a", ReplacementLinks.OpLink, "new"), Ev("b", ReplacementLinks.OpLink, "new") }, Link);
            Assert.IsFalse(state.ByOld.ContainsKey("a"));
            Assert.AreEqual("b", state.ByNew["new"].OldUid);
        }

        [TestMethod]
        public void MapFollowsChains()
        {
            var state = ReplacementLinks.Fold(new[] { Ev("a", ReplacementLinks.OpLink, "b"), Ev("b", ReplacementLinks.OpLink, "c") }, Link);
            var map = ReplacementLinks.Map(state);
            Assert.AreEqual("c", map["a"]);
            Assert.AreEqual("c", map["b"]);
        }

        [TestMethod]
        public void LinkToElementNotInComparisonStillMarksReplaced()
        {
            var state = ReplacementLinks.Fold(new[] { Ev("old", ReplacementLinks.OpLink, "elsewhere") }, Link);
            var shown = ReplacementLinks.Apply(new List<ElementChange> { Del("old", 0) }, state);
            Assert.AreEqual(ChangeKind.Replaced, shown[0].Kind);
            Assert.AreEqual("elsewhere", shown[0].UniqueId);
            Assert.IsNull(shown[0].NewMin);
        }

        [TestMethod]
        public void OtherLinkInstancesAreIgnored()
        {
            var e = Ev("old", ReplacementLinks.OpLink, "new");
            e.LinkInstanceUid = "another";
            Assert.AreEqual(0, ReplacementLinks.Fold(new[] { e }, Link).ByOld.Count);
        }
    }
}
