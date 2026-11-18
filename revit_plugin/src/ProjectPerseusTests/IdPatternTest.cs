using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.util;

namespace ProjectPerseusTests
{
    [TestClass]
    public class IdPatternTest
    {
        [TestMethod]
        public void Parse_RejectsInvalidPatterns()
        {
            Assert.IsNull(IdPattern.Parse(""));
            Assert.IsNull(IdPattern.ForRule("{CategoryPrefix}", isValue: false)); // ID rule without Increment
            Assert.IsNull(IdPattern.Parse("{Increment|4}{Increment|4}"));    // two Increments
            Assert.IsNull(IdPattern.Parse("{Increment|0}"));                 // width out of range
            Assert.IsNull(IdPattern.Parse("{Bogus}{Increment|4}"));          // unknown token
            Assert.IsNull(IdPattern.Parse("{Increment|4"));                  // unmatched brace
        }

        [TestMethod]
        public void ForRule_ValuePatternsHaveNoCounter()
        {
            var v = IdPattern.ForRule("{Project.Project Number}-{Level.Name}", isValue: true);
            Assert.IsNotNull(v);
            Assert.IsFalse(v.HasCounter);
            CollectionAssert.AreEqual(new[] { "Project.Project Number", "Level.Name" }, new System.Collections.Generic.List<string>(v.Tokens));
            Assert.IsNull(IdPattern.ForRule("{CategoryPrefix}{Increment|4}", isValue: true));   // a counter makes it an ID rule
            Assert.IsTrue(IdPattern.ForRule("{CategoryPrefix}{Increment|4}", isValue: false).HasCounter);
            Assert.IsFalse(v.TryExtractNumber("P1-Level 01", t => "x", out _));
        }

        private static System.Func<string, string> Tokens(params string[] kv)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return t => d.TryGetValue(t, out var v) ? v : null;
        }

        [TestMethod]
        public void AutoValue_RendersAndTransforms()
        {
            var p = IdPattern.ForRule("{Project.Project Number}-{Level.Name}", true);
            var resolve = Tokens("Project.Project Number", "P123", "Level.Name", "Level 02");

            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(p, "", resolve, out string v, out _));
            Assert.AreEqual("P123-Level 02", v);

            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(p, "value.upper()", resolve, out v, out _));
            Assert.AreEqual("P123-LEVEL 02", v);

            var level = IdPattern.ForRule("{Level.Name}", true);
            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(level, "re.sub(r\"\\D\", \"\", value)", resolve, out v, out _));
            Assert.AreEqual("02", v);

            // row["token"] reads any token of the pattern; numbers come out without a trailing .0
            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(p, "row[\"Project.Project Number\"]", resolve, out v, out _));
            Assert.AreEqual("P123", v);
            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(level, "len(value) * 2", resolve, out v, out _));
            Assert.AreEqual("16", v);
        }

        [TestMethod]
        public void AutoValue_UnresolvedClearsAndBadTransformFails()
        {
            var p = IdPattern.ForRule("{Room.Number}", true);
            Assert.AreEqual(AutoValue.Outcome.Unresolved, AutoValue.Compute(p, "", Tokens(), out string v, out string err));
            Assert.AreEqual("", v);
            Assert.AreEqual("Room.Number", err);

            var lit = IdPattern.ForRule("Fixed text", true);   // literal-only value rule
            Assert.AreEqual(AutoValue.Outcome.Ok, AutoValue.Compute(lit, "", Tokens(), out v, out _));
            Assert.AreEqual("Fixed text", v);

            Assert.AreEqual(AutoValue.Outcome.TransformError, AutoValue.Compute(lit, "value.(", Tokens(), out _, out err));
            Assert.IsFalse(string.IsNullOrEmpty(err));
        }

        [TestMethod]
        public void UniqueWith_ScopesCounterWithoutPrintingIt()
        {
            // Doors D01, D02... per room: the room number is part of the counter scope and the unique key only.
            var p = IdPattern.ForRule("D{Increment|2}", false, "{Element.Door Tag Room Number}");
            Assert.IsTrue(p.HasUniqueWith);
            CollectionAssert.AreEqual(new[] { "Element.Door Tag Room Number" }, new System.Collections.Generic.List<string>(p.UniqueWithTokens));
            var r101 = Tokens("Element.Door Tag Room Number", "101");
            var r102 = Tokens("Element.Door Tag Room Number", "102");
            Assert.AreEqual("D01", p.Render(r101, 1));
            Assert.AreNotEqual(p.ScopeKey(r101), p.ScopeKey(r102));
            Assert.IsNull(p.ScopeKey(Tokens()));                         // no room number yet: skipped
            Assert.AreEqual("D01 [101]", p.UniqueKey("D01", r101));
            Assert.AreNotEqual(p.UniqueKey("D01", r101), p.UniqueKey("D01", r102));
            Assert.IsTrue(p.TryExtractNumber("D07", r102, out long n) && n == 7);

            var plain = IdPattern.ForRule("D{Increment|2}", false, "");
            Assert.AreEqual("D01", plain.UniqueKey("D01", r101));        // no unique-with: the value itself
            Assert.IsNull(IdPattern.ForRule("D{Increment|2}", false, "{Bogus}"));
            Assert.IsNull(IdPattern.ForRule("D{Increment|2}", false, "{Increment|2}"));
            Assert.IsFalse(IdPattern.ForRule("{Level.Name}", true, "{Room.Number}").HasUniqueWith); // value rules ignore it
        }

        [TestMethod]
        public void IncrementModes_PickNextOrLowestFree()
        {
            // The only door in the room: both minimising modes give 1, whatever number it arrived with.
            Assert.AreEqual(1, IdNumbering.Pick(IdNumbering.Next, new long[0]));
            Assert.AreEqual(1, IdNumbering.Pick(IdNumbering.Fill, new long[0]));
            // D01 and D03 held (D02 deleted): next continues after the highest, fill takes the gap.
            Assert.AreEqual(4, IdNumbering.Pick(IdNumbering.Next, new long[] { 1, 3 }));
            Assert.AreEqual(2, IdNumbering.Pick(IdNumbering.Fill, new long[] { 1, 3 }));
            // Numbers another user holds (server claims) are skipped.
            Assert.AreEqual(3, IdNumbering.Pick(IdNumbering.Fill, new long[] { 1 }, n => n == 2));
            Assert.AreEqual(5, IdNumbering.Pick(IdNumbering.Next, new long[] { 1, 3 }, n => n == 4));
            Assert.AreEqual(IdNumbering.Keep, IdNumbering.Normalise(null));
            Assert.AreEqual(IdNumbering.Fill, IdNumbering.Normalise(" Fill "));
            Assert.IsFalse(IdNumbering.Minimises(IdNumbering.Keep));
        }

        [TestMethod]
        public void DoorRoomTokensAndFallbacks()
        {
            var p = IdPattern.ForRule("{ToRoom.Number|FromRoom.Number}", true);
            Assert.IsNotNull(p);
            CollectionAssert.AreEqual(new[] { "ToRoom.Number|FromRoom.Number" }, new System.Collections.Generic.List<string>(p.Tokens));
            CollectionAssert.AreEqual(new[] { "ToRoom.Number", "FromRoom.Number" }, IdPattern.Alternatives(p.Tokens[0]));
            Assert.IsNotNull(IdPattern.Parse("{Element.Door Tag Room Number}-{Increment|2}"));
            Assert.IsNull(IdPattern.Parse("{ToRoom.Number|Bogus}"));
        }

        [TestMethod]
        public void Render_PadsAndPrefixes()
        {
            var p = IdPattern.Parse("{CategoryPrefix}{Increment|4}");
            Assert.IsTrue(p.UsesCategoryPrefix);
            Assert.AreEqual("W0001", p.Render("W", 1));
            Assert.AreEqual("D0042", p.Render("D", 42));
            Assert.AreEqual("W12345", p.Render("W", 12345));   // grows past the minimum width
        }

        [TestMethod]
        public void Render_SupportsLiteralText()
        {
            var p = IdPattern.Parse("AJV-{CategoryPrefix}-{Increment|3}");
            Assert.AreEqual("AJV-W-007", p.Render("W", 7));
        }

        [TestMethod]
        public void TryExtractNumber_MatchesOnlyOwnPrefix()
        {
            var p = IdPattern.Parse("{CategoryPrefix}{Increment|4}");
            Assert.IsTrue(p.TryExtractNumber("W0012", "W", out long n));
            Assert.AreEqual(12, n);
            Assert.IsFalse(p.TryExtractNumber("W0012", "D", out _));   // wrong category prefix
            Assert.IsFalse(p.TryExtractNumber("W12", "W", out _));     // below minimum width
            Assert.IsFalse(p.TryExtractNumber("", "W", out _));
            Assert.IsFalse(p.TryExtractNumber("W0012x", "W", out _));
        }

        [TestMethod]
        public void TryExtractNumber_WorksWithoutCategoryPrefix()
        {
            var p = IdPattern.Parse("ID-{Increment|5}");
            Assert.IsFalse(p.UsesCategoryPrefix);
            Assert.IsTrue(p.TryExtractNumber("ID-00009", (string)null, out long n));
            Assert.AreEqual(9, n);
        }

        private static System.Func<string, string> Values(string level, string prefix) =>
            t => t == "Level.Name" ? level : t == "CategoryPrefix" ? prefix : null;

        [TestMethod]
        public void Parse_AcceptsRelatedObjectTokens()
        {
            Assert.IsNotNull(IdPattern.Parse("{Host.Mark}.{Increment|2}"));
            Assert.IsNotNull(IdPattern.Parse("{Level.Level Number}-{Increment|2}"));   // parameter names may contain spaces
            Assert.IsNotNull(IdPattern.Parse("{Workset}-{Increment|2}"));
            Assert.IsNull(IdPattern.Parse("{Foo.Name}{Increment|2}"));                 // unknown related kind
            Assert.IsNull(IdPattern.Parse("{Level.}{Increment|2}"));                   // no parameter name
            Assert.IsNull(IdPattern.Parse("{Level.A|B}{Increment|2}"));
        }

        [TestMethod]
        public void Render_ResolvesRelatedTokens()
        {
            var p = IdPattern.Parse("{Level.Name}-{CategoryPrefix}-{Increment|3}");
            CollectionAssert.AreEqual(new[] { "Level.Name", "CategoryPrefix" }, new System.Collections.Generic.List<string>(p.Tokens));
            Assert.AreEqual("Level 02-W-001", p.Render(Values("Level 02", "W"), 1));
        }

        [TestMethod]
        public void TryExtractNumber_ChecksRelatedValues()
        {
            var p = IdPattern.Parse("{Level.Name}-{CategoryPrefix}-{Increment|3}");
            Assert.IsTrue(p.TryExtractNumber("Level 02-W-007", Values("Level 02", "W"), out long n));
            Assert.AreEqual(7, n);
            Assert.IsFalse(p.TryExtractNumber("Level 02-W-007", Values("Level 03", "W"), out _));  // element moved level
            Assert.IsFalse(p.TryExtractNumber("Level 02-W-007", Values("Level 02", "D"), out _));
        }

        [TestMethod]
        public void ScopeKey_SeparatesCountersAndRejectsUnresolvedValues()
        {
            var p = IdPattern.Parse("{Level.Name}-{CategoryPrefix}-{Increment|3}");
            Assert.AreNotEqual(p.ScopeKey(Values("Level 02", "W")), p.ScopeKey(Values("Level 03", "W")));
            Assert.AreEqual(p.ScopeKey(Values("Level 02", "W")), p.ScopeKey(Values("Level 02", "W")));
            Assert.IsNull(p.ScopeKey(Values(null, "W")));    // no level -> element is skipped
            Assert.IsNull(p.ScopeKey(Values("Level 02", "")));
        }

        [TestMethod]
        public void TryExtractNumber_EscapesRegexCharactersInRelatedValues()
        {
            var p = IdPattern.Parse("{Level.Name}-{Increment|2}");
            Assert.IsTrue(p.TryExtractNumber("L(2)+-05", t => "L(2)+", out _));
            Assert.IsFalse(p.TryExtractNumber("LL2-05", t => "L(2)+", out _));
        }

        [TestMethod]
        public void TryExtractNumber_EscapesRegexCharactersInPrefix()
        {
            var p = IdPattern.Parse("{CategoryPrefix}{Increment|2}");
            Assert.IsTrue(p.TryExtractNumber("A.B01", "A.B", out _));
            Assert.IsFalse(p.TryExtractNumber("AxB01", "A.B", out _));
        }
    }
}
