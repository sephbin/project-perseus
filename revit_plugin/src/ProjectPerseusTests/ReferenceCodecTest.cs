using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.collections;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ReferenceCodecTest
    {
        // ── slot text ────────────────────────────────────────────────────────

        [TestMethod]
        public void SlotRoundTripsIdAndName()
        {
            var r = new MemberRef(12345, "ST07 Exit");
            Assert.AreEqual("12345|ST07 Exit", ReferenceCodec.EncodeSlot(r));
            Assert.AreEqual(r, ReferenceCodec.DecodeSlot("12345|ST07 Exit"));
        }

        [TestMethod]
        public void SlotEscapesBarsAndBackslashesInNames()
        {
            var r = new MemberRef(7, @"A|B\C");
            string text = ReferenceCodec.EncodeSlot(r);
            Assert.AreEqual(@"7|A\|B\\C", text);
            Assert.AreEqual(r, ReferenceCodec.DecodeSlot(text));
        }

        [TestMethod]
        public void EmptyAndNameOnlySlots()
        {
            Assert.IsTrue(ReferenceCodec.DecodeSlot("").IsEmpty);
            Assert.IsTrue(ReferenceCodec.DecodeSlot("   ").IsEmpty);
            Assert.AreEqual("", ReferenceCodec.EncodeSlot(MemberRef.Empty));
            // A hand-typed wayfinding id from before collections: resolved by name.
            Assert.AreEqual(new MemberRef(0, "ST07"), ReferenceCodec.DecodeSlot("ST07"));
            Assert.AreEqual(new MemberRef(0, "ST07"), ReferenceCodec.DecodeSlot("|ST07"));
            Assert.AreEqual("|ST07", ReferenceCodec.EncodeSlot(new MemberRef(0, "ST07")));
            // Not an id before the bar: the whole text is a name.
            Assert.AreEqual(new MemberRef(0, "Exit|Left"), ReferenceCodec.DecodeSlot(@"Exit\|Left"));
            Assert.AreEqual(new MemberRef(0, "ab|cd"), ReferenceCodec.DecodeSlot("ab|cd"));
        }

        // ── list text ────────────────────────────────────────────────────────

        [TestMethod]
        public void ListRoundTripsWithHolesAndDropsTrailingEmpties()
        {
            var members = new List<MemberRef> { new MemberRef(1, "ST01"), MemberRef.Empty, new MemberRef(3, "ST03 \"Lift\""), MemberRef.Empty };
            string json = ReferenceCodec.EncodeList(members);
            Assert.AreEqual("[{\"id\":1,\"name\":\"ST01\"},null,{\"id\":3,\"name\":\"ST03 \\\"Lift\\\"\"}]", json);
            var back = ReferenceCodec.DecodeList(json, out string err);
            Assert.IsNull(err);
            CollectionAssert.AreEqual(members.Take(3).ToList(), back);
            Assert.AreEqual("", ReferenceCodec.EncodeList(new[] { MemberRef.Empty }));
        }

        [TestMethod]
        public void ListAcceptsStringsAndReportsBadJson()
        {
            var back = ReferenceCodec.DecodeList("[\"5|ST05\", \"ST06\", {\"id\": \"9\", \"name\": \"ST09\"}]", out string err);
            Assert.IsNull(err);
            CollectionAssert.AreEqual(new[] { new MemberRef(5, "ST05"), new MemberRef(0, "ST06"), new MemberRef(9, "ST09") }, back);

            Assert.AreEqual(0, ReferenceCodec.DecodeList("{oops", out err).Count);
            Assert.IsNotNull(err);
            ReferenceCodec.DecodeList("{\"a\":1}", out err);
            StringAssert.Contains(err, "list");
            Assert.AreEqual(0, ReferenceCodec.DecodeList("", out err).Count);
            Assert.IsNull(err);
        }

        // ── slot names ───────────────────────────────────────────────────────

        [TestMethod]
        public void SlotNamesFromPattern()
        {
            Assert.AreEqual("COX_Sign_3", ReferenceCodec.SlotName("COX_Sign_{n}", 3));
            Assert.AreEqual(3, ReferenceCodec.SlotNumber("COX_Sign_{n}", "COX_Sign_3"));
            Assert.AreEqual(12, ReferenceCodec.SlotNumber("COX_Sign_{n}", "cox_sign_12"));
            Assert.AreEqual(2, ReferenceCodec.SlotNumber("Sign {n} (ref)", "Sign 2 (ref)"));
            Assert.AreEqual(0, ReferenceCodec.SlotNumber("COX_Sign_{n}", "COX_Sign_01"));
            Assert.AreEqual(0, ReferenceCodec.SlotNumber("COX_Sign_{n}", "COX_Sign_"));
            Assert.AreEqual(0, ReferenceCodec.SlotNumber("COX_Sign_{n}", "COX_Sign_3x"));
            Assert.AreEqual(0, ReferenceCodec.SlotNumber("Sign Members", "Sign Members"));
        }

        // ── overrides ────────────────────────────────────────────────────────

        [TestMethod]
        public void OverridesRoundTripKeyedBySlot()
        {
            var o = OverrideCodec.Parse("{\"3\": {\"Arrow\": \"Left\"}, \"1\": {\"Message\": \"Plant Room 3\", \"Size\": 2, \"Lit\": true}}", out string err);
            Assert.IsNull(err);
            Assert.IsTrue(o.TryGet(1, "message", out var msg));
            Assert.AreEqual("Plant Room 3", msg);
            Assert.AreEqual("2", o.For(1)["Size"]);
            Assert.AreEqual("Yes", o.For(1)["Lit"]);
            Assert.AreEqual("{\"1\":{\"Lit\":\"Yes\",\"Message\":\"Plant Room 3\",\"Size\":\"2\"},\"3\":{\"Arrow\":\"Left\"}}", OverrideCodec.Serialize(o));
        }

        [TestMethod]
        public void OverridesSetClearAndEmpty()
        {
            var o = new Overrides();
            o.Set(2, "Message", "");          // blanking a field is a real override
            Assert.AreEqual("{\"2\":{\"Message\":\"\"}}", OverrideCodec.Serialize(o));
            Assert.IsTrue(o.Clear(2, "MESSAGE"));
            Assert.IsTrue(o.IsEmpty);
            Assert.AreEqual("", OverrideCodec.Serialize(o));
            o.Set(0, "Message", "x");         // slots are 1-based
            Assert.IsTrue(o.IsEmpty);
        }

        [TestMethod]
        public void OverridesFollowSlotsWhenMembersMove()
        {
            var o = OverrideCodec.Parse("{\"1\":{\"M\":\"a\"},\"2\":{\"M\":\"b\"},\"3\":{\"M\":\"c\"}}", out _);
            // Slot 2 removed, 3 moves up to 2, 1 stays.
            int dropped = o.Remap(new Dictionary<int, int> { [1] = 1, [3] = 2 });
            Assert.AreEqual(1, dropped);
            Assert.AreEqual("{\"1\":{\"M\":\"a\"},\"2\":{\"M\":\"c\"}}", OverrideCodec.Serialize(o));
            Assert.AreEqual(1, o.PruneAbove(1));
            Assert.AreEqual("{\"1\":{\"M\":\"a\"}}", OverrideCodec.Serialize(o));
        }

        [TestMethod]
        public void OverridesMergeOnTopOfMemberValues()
        {
            var o = new Overrides();
            o.Set(1, "message", "Plant Room 3");
            var merged = o.Merge(1, new Dictionary<string, string> { ["Message"] = "Exit", ["Arrow"] = "Up" });
            Assert.AreEqual("Plant Room 3", merged["Message"]);
            Assert.AreEqual("Up", merged["Arrow"]);
            Assert.AreEqual("Exit", o.Merge(2, new Dictionary<string, string> { ["Message"] = "Exit" })["Message"]);
        }

        [TestMethod]
        public void BadOverridesKeepReadableSlots()
        {
            var o = OverrideCodec.Parse("{\"x\": {\"M\": \"a\"}, \"2\": {\"M\": \"b\"}, \"3\": \"oops\"}", out string err);
            Assert.IsNotNull(err);
            CollectionAssert.AreEqual(new[] { 2 }, o.Slots.ToArray());
            OverrideCodec.Parse("not json", out err);
            Assert.IsNotNull(err);
            OverrideCodec.Parse("[1]", out err);
            Assert.IsNotNull(err);
        }

        // ── resolver ─────────────────────────────────────────────────────────

        private static Resolution Resolve(MemberRef r, Dictionary<long, TargetInfo> model)
        {
            return ReferenceResolver.Resolve(r,
                id => model.TryGetValue(id, out var i) ? i : new TargetInfo(false, false, ""),
                name => model.Where(kv => kv.Value.IsTarget && kv.Value.Name == name).Select(kv => kv.Key).ToList());
        }

        [TestMethod]
        public void ResolverOkWhenIdAndNameAgree()
        {
            var model = new Dictionary<long, TargetInfo> { [10] = new TargetInfo(true, true, "ST07 Exit") };
            var res = Resolve(new MemberRef(10, "ST07 Exit"), model);
            Assert.AreEqual(RefStatus.Ok, res.Status);
            Assert.AreEqual(10, res.ElementId);
            Assert.AreEqual(RefStatus.Empty, Resolve(MemberRef.Empty, model).Status);
        }

        [TestMethod]
        public void ResolverRenamedElementIsStaleAcceptRename()
        {
            var model = new Dictionary<long, TargetInfo> { [10] = new TargetInfo(true, true, "ST07 Exit Stair") };
            var res = Resolve(new MemberRef(10, "ST07 Exit"), model);
            Assert.AreEqual(RefStatus.Stale, res.Status);
            Assert.AreEqual(10, res.ElementId);
            Assert.AreEqual(RefFix.AcceptRename, res.Fix);
            Assert.AreEqual("ST07 Exit Stair", res.FixName);
        }

        [TestMethod]
        public void ResolverRenamedButAnotherHasTheNameOffersRepoint()
        {
            var model = new Dictionary<long, TargetInfo>
            {
                [10] = new TargetInfo(true, true, "ST07 old"),
                [22] = new TargetInfo(true, true, "ST07 Exit"),
            };
            var res = Resolve(new MemberRef(10, "ST07 Exit"), model);
            Assert.AreEqual(RefStatus.Stale, res.Status);
            Assert.AreEqual(10, res.ElementId, "the id's element is still used until someone re-points");
            Assert.AreEqual(RefFix.Repoint, res.Fix);
            Assert.AreEqual(22, res.FixId);
        }

        [TestMethod]
        public void ResolverDeletedElementFoundByName()
        {
            var model = new Dictionary<long, TargetInfo> { [22] = new TargetInfo(true, true, "ST07 Exit") };
            var res = Resolve(new MemberRef(10, "ST07 Exit"), model);
            Assert.AreEqual(RefStatus.Stale, res.Status);
            Assert.AreEqual(22, res.ElementId);
            Assert.AreEqual(RefFix.Repoint, res.Fix);
            // Name-only references (hand-typed ids) resolve the same way.
            var nameOnly = Resolve(new MemberRef(0, "ST07 Exit"), model);
            Assert.AreEqual(RefStatus.Stale, nameOnly.Status);
            Assert.AreEqual(22, nameOnly.FixId);
        }

        [TestMethod]
        public void ResolverBrokenWhenNoOrSeveralNameMatches()
        {
            var none = Resolve(new MemberRef(10, "ST07 Exit"), new Dictionary<long, TargetInfo>());
            Assert.AreEqual(RefStatus.Broken, none.Status);
            Assert.AreEqual(0, none.ElementId);

            var two = new Dictionary<long, TargetInfo>
            {
                [21] = new TargetInfo(true, true, "ST07 Exit"),
                [22] = new TargetInfo(true, true, "ST07 Exit"),
            };
            var res = Resolve(new MemberRef(10, "ST07 Exit"), two);
            Assert.AreEqual(RefStatus.Broken, res.Status);
            StringAssert.Contains(res.Reason, "2 allowed elements");

            // The id exists but isn't an allowed member any more (category changed): still handed to templates, marked broken.
            var wrongCat = new Dictionary<long, TargetInfo> { [10] = new TargetInfo(true, false, "ST07 Exit") };
            var w = Resolve(new MemberRef(10, "ST07 Exit"), wrongCat);
            Assert.AreEqual(RefStatus.Broken, w.Status);
            Assert.AreEqual(10, w.ElementId);
        }

        [TestMethod]
        public void ListRollup_JoinsMemberValues()
        {
            var values = new[] { "ST07", "", " ST02 ", "ST07", null };
            Assert.AreEqual("ST07, ST02, ST07", ListRollup.Join(values, ", ", skipEmpty: true, unique: false));
            Assert.AreEqual("ST07, ST02", ListRollup.Join(values, ", ", skipEmpty: true, unique: true));
            Assert.AreEqual("ST07;;ST02;ST07;", ListRollup.Join(values, ";", skipEmpty: false, unique: false));
            Assert.AreEqual("ST07\nST02", ListRollup.Join(values, "\\n", skipEmpty: true, unique: true));   // escaped new line
            Assert.AreEqual("ST07\nST02", ListRollup.Join(values, "\n", skipEmpty: true, unique: true));    // real new line
            Assert.AreEqual("ST07 / ST02", ListRollup.Join(values, " / ", true, true));
            Assert.AreEqual("ST07ST02", ListRollup.Join(values, "", true, true));
            Assert.AreEqual("ST07, ST02", ListRollup.Join(values, null, true, true));                        // default ", "
            Assert.AreEqual("a\\b", ListRollup.Delimiter("a\\\\b"));
            Assert.AreEqual("", ListRollup.Join(new string[0], ", ", true, false));

            var l = new ProjectPerseus.models.CollectionListDto { MemberParameter = " Sign Type " };
            Assert.AreEqual("Sign Type", l.Target);   // owner parameter defaults to the member parameter's name
            l.OwnerParameter = "Pack Sign Types";
            Assert.AreEqual("Pack Sign Types", l.Target);
        }
    }
}
