using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ViewTargetsTest
    {
        private static ViewCandidate V(string uid, string name, string sheet = "") => new ViewCandidate { Uid = uid, Name = name, SheetNumber = sheet };

        [TestMethod]
        public void RegexPicksViewsCaseInsensitively()
        {
            var s = new BakeSettings { ViewRegex = "^l\\d+ - coord" };
            var rx = ViewTargets.Build(s.ViewRegex, out var err);
            Assert.IsNull(err);
            var views = new[] { V("1", "L1 - Coordination"), V("2", "L2 - Coordination"), V("3", "L1 - GA") };
            CollectionAssert.AreEqual(new[] { "1", "2" }, ViewTargets.Targets(views, s, rx).Select(v => v.Uid).ToArray());
        }

        [TestMethod]
        public void TickingAndUntickingOverrideTheRegex()
        {
            var s = new BakeSettings { ViewRegex = "Coordination" };
            var rx = ViewTargets.Build(s.ViewRegex, out _);
            var match = V("1", "L1 - Coordination");
            var other = V("3", "L1 - GA");

            ViewTargets.Toggle(match, s, rx, false);
            Assert.IsFalse(ViewTargets.IsTarget(match, s, rx));
            Assert.AreEqual(ViewTargets.Reason.Excluded, ViewTargets.ReasonFor(match, s, rx));

            ViewTargets.Toggle(other, s, rx, true);
            Assert.IsTrue(ViewTargets.IsTarget(other, s, rx));
            Assert.AreEqual(ViewTargets.Reason.Manual, ViewTargets.ReasonFor(other, s, rx));

            // Re-ticking a matching view just removes the exclusion (no redundant manual entry).
            ViewTargets.Toggle(match, s, rx, true);
            Assert.AreEqual(ViewTargets.Reason.Regex, ViewTargets.ReasonFor(match, s, rx));
            Assert.IsFalse(s.IncludedViewUids.Contains("1"));
        }

        [TestMethod]
        public void SheetNumberTargetAndInvalidPattern()
        {
            var s = new BakeSettings { ViewRegex = "^A-1", RegexTarget = BakeSettings.MatchSheetNumber };
            var rx = ViewTargets.Build(s.ViewRegex, out _);
            Assert.IsTrue(ViewTargets.IsTarget(V("1", "Anything", "A-101"), s, rx));
            Assert.IsFalse(ViewTargets.IsTarget(V("2", "A-101 named view", ""), s, rx));

            Assert.IsNull(ViewTargets.Build("([", out var err));
            Assert.IsNotNull(err);
            Assert.IsNull(ViewTargets.Build("  ", out err));
            Assert.IsNull(err);
        }

        [TestMethod]
        public void PruneDropsViewsNoLongerInTheModel()
        {
            var s = new BakeSettings();
            s.IncludedViewUids.Add("gone");
            s.IncludedViewUids.Add("1");
            s.ExcludedViewUids.Add("gone2");
            ViewTargets.Prune(s, new[] { V("1", "x") });
            CollectionAssert.AreEqual(new[] { "1" }, s.IncludedViewUids);
            Assert.AreEqual(0, s.ExcludedViewUids.Count);
        }
    }
}
