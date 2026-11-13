using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.mapping;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class LinkSyncPlannerTest
    {
        private const double Ft = 1 / 304.8;
        private const string TypeA = "Sprinklers|Head|Pendent";
        private const string TypeB = "Sprinklers|Head|Upright";

        private static Frame At(double xMm, double yMm, double zMm) => new Frame { O = new[] { xMm * Ft, yMm * Ft, zMm * Ft } };

        private static FamilyMapRule Rule(string key, string target, string id = null)
        {
            var p = key.Split('|');
            return new FamilyMapRule { Id = id ?? key, SourceCategory = p[0], SourceFamily = p[1], SourceType = p[2], TargetSymbolUid = target, TargetName = target };
        }

        private static SyncInput Input(params FamilyMapRule[] rules) => new SyncInput { LinkUid = "L", Rules = rules.ToList() };

        private static SyncSource Src(string uid, string type, Frame f) => new SyncSource { Uid = uid, Id = uid.GetHashCode() & 0xffff, TypeKey = type, Category = "Sprinklers", Frame = f };

        /// <summary>One of ours placed at the consultant element in the past (baseline = then).</summary>
        private static SyncOurs Ours(long id, string type, Frame now, string srcUid, string srcType, Frame srcThen, Frame oursThen, string ruleId = TypeA) =>
            new SyncOurs
            {
                Id = id, TypeUid = type, TypeName = type, Frame = now,
                Record = new MappedFamilyData
                {
                    LinkInstanceUid = "L", SourceUid = srcUid, SourceId = "1", RuleId = ruleId, SourceTypeKey = srcType,
                    SourceFrame = MappingMath.Pack(srcThen), OursFrame = MappingMath.Pack(oursThen),
                },
            };

        private static List<SyncKind> Kinds(SyncInput i) => LinkSyncPlanner.Plan(i).Select(x => x.Kind).ToList();

        [TestMethod]
        public void InSyncWithinTolerance()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            i.Ours.Add(Ours(1, "ours", At(10, 0, 3010), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));   // nudged 10 mm
            CollectionAssert.AreEqual(new[] { SyncKind.InSync }, Kinds(i));
        }

        [TestMethod]
        public void ConsultantMovedIsFlagged()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(500, 0, 3000)));
            i.Ours.Add(Ours(1, "ours", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));
            var items = LinkSyncPlanner.Plan(i);
            Assert.AreEqual(SyncKind.Moved, items.Single().Kind);
            StringAssert.Contains(items[0].Detail, "consultant element moved");
        }

        [TestMethod]
        public void HandEditKeepsOwnPosition()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            i.Ours.Add(Ours(1, "ours", At(400, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));   // we moved it
            CollectionAssert.AreEqual(new[] { SyncKind.KeptByHand }, Kinds(i));
        }

        [TestMethod]
        public void RejectedMoveStaysRejectedUntilItMovesAgain()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(500, 0, 3000)));
            var o = Ours(1, "ours", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000));
            o.Record.RejectedExpected = MappingMath.Pack(At(500, 0, 3000));
            i.Ours.Add(o);
            CollectionAssert.AreEqual(new[] { SyncKind.KeptRejected }, Kinds(i));
            i.Sources[0].Frame = At(900, 0, 3000);
            CollectionAssert.AreEqual(new[] { SyncKind.Moved }, Kinds(i));
        }

        [TestMethod]
        public void TypeChangeAndMoveAreSeparateItems()
        {
            var i = Input(Rule(TypeA, "oursA"), Rule(TypeB, "oursB"));
            i.Sources.Add(Src("s1", TypeB, At(500, 0, 3000)));
            i.Ours.Add(Ours(1, "oursA", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));
            var items = LinkSyncPlanner.Plan(i);
            CollectionAssert.AreEquivalent(new[] { SyncKind.ConsultantType, SyncKind.Moved }, items.Select(x => x.Kind).ToList());
            Assert.AreEqual("oursB", items.First(x => x.Kind == SyncKind.ConsultantType).TargetUid);
        }

        [TestMethod]
        public void MappingTypeChange()
        {
            var i = Input(Rule(TypeA, "oursNew"));
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            i.Ours.Add(Ours(1, "oursOld", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));
            CollectionAssert.AreEqual(new[] { SyncKind.MappingType }, Kinds(i));
        }

        [TestMethod]
        public void ReplacedFollowsTheReplacement()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s2", TypeA, At(0, 0, 3000)));   // s1 deleted, s2 replaced it at the same spot
            i.Ours.Add(Ours(1, "ours", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));
            i.ReplacementMap = new Dictionary<string, string> { { "s1", "s2" } };
            var items = LinkSyncPlanner.Plan(i);
            Assert.AreEqual(SyncKind.Replaced, items.Single().Kind);
            Assert.AreEqual("s2", items[0].NewSourceUid);
        }

        [TestMethod]
        public void OrphanedWhenGoneWithoutReplacement()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Ours.Add(Ours(1, "ours", At(0, 0, 3000), "s1", TypeA, At(0, 0, 3000), At(0, 0, 3000)));
            CollectionAssert.AreEqual(new[] { SyncKind.Orphaned }, Kinds(i));
            i.Ours[0].Record.RejectedGone = true;
            CollectionAssert.AreEqual(new[] { SyncKind.KeptRejected }, Kinds(i));
        }

        [TestMethod]
        public void NewAndMatch()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            i.Sources.Add(Src("s2", TypeA, At(5000, 0, 3000)));
            i.Unrecorded.Add(new SyncOurs { Id = 7, TypeUid = "ours", TypeName = "ours", Frame = At(15, 0, 3000) });
            var items = LinkSyncPlanner.Plan(i);
            Assert.AreEqual(7, items.Single(x => x.Kind == SyncKind.Match).OursId);
            Assert.AreEqual("s2", items.Single(x => x.Kind == SyncKind.New).SourceUid);
            i.RejectedNew.Add("s2");
            Assert.IsFalse(LinkSyncPlanner.Plan(i).Any(x => x.Kind == SyncKind.New));
        }

        [TestMethod]
        public void HiddenCategoryIsNotMapped()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.HiddenCategories.Add("Sprinklers");
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            Assert.AreEqual(0, LinkSyncPlanner.Plan(i).Count);
        }

        [TestMethod]
        public void OldRecordWithoutBaselineIsFlaggedNotKept()
        {
            var i = Input(Rule(TypeA, "ours"));
            i.Sources.Add(Src("s1", TypeA, At(0, 0, 3000)));
            i.Ours.Add(Ours(1, "ours", At(400, 0, 3000), "s1", null, null, null));
            CollectionAssert.AreEqual(new[] { SyncKind.Moved }, Kinds(i));
        }

        [TestMethod]
        public void DeltaDescribes()
        {
            var d = MappingMath.Delta(At(300, 0, 110), At(0, 0, 0));
            Assert.AreEqual(300, d.XyMm, 1e-6);
            Assert.AreEqual(110, d.ZMm, 1e-6);
            Assert.IsTrue(MappingMath.Within(MappingMath.Delta(At(20, 0, 20), At(0, 0, 0)), 25, 25));
            StringAssert.Contains(MappingMath.Describe(d), "horizontally");
        }
    }
}
