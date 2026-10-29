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

        [TestMethod]
        public void TryExtractNumber_EscapesRegexCharactersInPrefix()
        {
            var p = IdPattern.Parse("{CategoryPrefix}{Increment|2}");
            Assert.IsTrue(p.TryExtractNumber("A.B01", "A.B", out _));
            Assert.IsFalse(p.TryExtractNumber("AxB01", "A.B", out _));
        }
    }
}
