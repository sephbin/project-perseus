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
            Assert.IsNull(IdPattern.Parse("{CategoryPrefix}"));              // no Increment
            Assert.IsNull(IdPattern.Parse("{Increment|4}{Increment|4}"));    // two Increments
            Assert.IsNull(IdPattern.Parse("{Increment|0}"));                 // width out of range
            Assert.IsNull(IdPattern.Parse("{Bogus}{Increment|4}"));          // unknown token
            Assert.IsNull(IdPattern.Parse("{Increment|4"));                  // unmatched brace
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
            Assert.IsTrue(p.TryExtractNumber("ID-00009", null, out long n));
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
