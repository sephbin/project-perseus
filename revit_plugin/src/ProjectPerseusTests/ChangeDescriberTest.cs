using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using ProjectPerseus.models;

namespace ProjectPerseusTests
{
    [TestClass]
    public class ChangeDescriberTest
    {
        private const double Mm = 1.0 / 304.8;

        [TestMethod]
        public void LengthsAreMillimetresThenMetres()
        {
            Assert.AreEqual("120 mm", ChangeDescriber.Length(120 * Mm));
            Assert.AreEqual("4.2 m", ChangeDescriber.Length(4200 * Mm));
        }

        [TestMethod]
        public void MovementIsSplitIntoHorizontalAndVertical()
        {
            var c = new ElementChange
            {
                Kind = ChangeKind.Modified, Aspects = ChangeAspect.Moved,
                MoveDistance = 130 * Mm, MoveVector = new[] { 120 * Mm, 0, 50 * Mm },
            };
            Assert.AreEqual("Moved 130 mm (120 mm horizontally, 50 mm up).", ChangeDescriber.Lines(c)[0]);
            c.MoveVector = new[] { 0, 0, -50 * Mm };
            c.MoveDistance = 50 * Mm;
            Assert.AreEqual("Moved vertically 50 mm down.", ChangeDescriber.Lines(c)[0]);
            Assert.AreEqual("Moved vertically 50 mm", ChangeDescriber.Summary(c));
            Assert.IsTrue(ChangeDescriber.IsVerticalMove(c));
            c.MoveVector = new[] { 120 * Mm, 0, 50 * Mm };
            Assert.IsFalse(ChangeDescriber.IsVerticalMove(c), "3D move is a plain move");
        }

        [TestMethod]
        public void GeometryReportsLengthAndVolume()
        {
            var c = new ElementChange
            {
                Kind = ChangeKind.Modified, Aspects = ChangeAspect.Geometry,
                OldLength = 4200 * Mm, NewLength = 4800 * Mm, OldVolume = 1.0, NewVolume = 1.12,
            };
            var lines = ChangeDescriber.Lines(c);
            Assert.AreEqual("Length 4.2 m → 4.8 m (+600 mm).", lines[0]);
            Assert.AreEqual("Volume +12 %.", lines[1]);
            Assert.AreEqual("Length +600 mm", ChangeDescriber.Summary(c));
        }

        [TestMethod]
        public void SummaryListsAspectsAndParameters()
        {
            var c = new ElementChange
            {
                Kind = ChangeKind.Modified,
                Aspects = ChangeAspect.Moved | ChangeAspect.Type | ChangeAspect.Parameters,
                MoveDistance = 120 * Mm, OldType = "UC : 310UC118", Family = "UC", Type = "310UC97",
                ParameterChanges = new List<ParameterChange>
                {
                    new ParameterChange { Name = "Mark" }, new ParameterChange { Name = "Comments" },
                    new ParameterChange { Name = "Fire Rating" }, new ParameterChange { Name = "Phase" },
                },
            };
            Assert.AreEqual("Moved 120 mm · Type · 4 params", ChangeDescriber.Summary(c));
            var lines = ChangeDescriber.Lines(c);
            CollectionAssert.Contains(lines, "Type changed: UC : 310UC118 → UC : 310UC97.");
            CollectionAssert.Contains(lines, "4 parameters changed: Mark, Comments, Fire Rating and 1 more.");
        }

        [TestMethod]
        public void DeletedMentionsLikelyReplacement()
        {
            var c = new ElementChange { Kind = ChangeKind.Deleted, Family = "UB", Type = "410UB54", Level = "L3" };
            Assert.AreEqual("Deleted", ChangeDescriber.Summary(c));
            c.Replacements = new List<ReplacementCandidate> { new ReplacementCandidate { UniqueId = "x" } };
            Assert.AreEqual("Deleted · replaced?", ChangeDescriber.Summary(c));
            Assert.AreEqual("Deleted: was UB : 410UB54 on L3.", ChangeDescriber.Lines(c)[0]);
        }
    }
}
