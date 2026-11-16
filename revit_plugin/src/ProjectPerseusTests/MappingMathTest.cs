using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.mapping;

namespace ProjectPerseusTests
{
    [TestClass]
    public class MappingMathTest
    {
        private const double Ft = 1 / 304.8;

        [TestMethod]
        public void OffsetIsInTheSourceAxes()
        {
            // Source turned 90° in plan: its X points to host +Y.
            var src = new Frame { O = new[] { 10.0, 20, 3 }, X = new[] { 0.0, 1, 0 }, Y = new[] { -1.0, 0, 0 }, Z = new[] { 0.0, 0, 1 } };
            var f = MappingMath.Place(src, new[] { 1000.0, 0, 500 }, null);
            Assert.AreEqual(10, f.O[0], 1e-9);
            Assert.AreEqual(20 + 1000 * Ft, f.O[1], 1e-9);
            Assert.AreEqual(3 + 500 * Ft, f.O[2], 1e-9);
            Assert.AreEqual(1, f.X[1], 1e-9);
        }

        [TestMethod]
        public void OffsetZIsAlwaysUp()
        {
            // Ceiling device: its own Z points down.
            var src = new Frame { O = new[] { 0.0, 0, 10 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, -1, 0 }, Z = new[] { 0.0, 0, -1 } };
            var f = MappingMath.Place(src, new[] { 0.0, 100, 200 }, null);
            Assert.AreEqual(10 + 200 * Ft, f.O[2], 1e-9);
            Assert.AreEqual(100 * Ft, f.O[1], 1e-9);   // plan Y = 90° left of the element's X, whatever its own Y
        }

        [TestMethod]
        public void RotationZTurnsInPlanXTilts()
        {
            var src = new Frame();
            var z = MappingMath.Place(src, null, new[] { 0.0, 0, 90 });
            Assert.AreEqual(0, z.X[0], 1e-9);
            Assert.AreEqual(1, z.X[1], 1e-9);
            Assert.IsFalse(MappingMath.IsTilted(z));
            var x = MappingMath.Place(src, null, new[] { 90.0, 0, 0 });
            Assert.AreEqual(-1, x.Z[1], 1e-9);   // Z tipped towards -Y
            Assert.IsTrue(MappingMath.IsTilted(x));
            var dir = MappingMath.PlanDirection(x);
            Assert.AreEqual(1, dir[0], 1e-9);
        }

        [TestMethod]
        public void TriageVerdicts()
        {
            Assert.AreEqual(TypeTriage.Suggested, new TypeTriage { Changes = 10, Cared = 1, Ignored = 9 }.Verdict);
            Assert.AreEqual(TypeTriage.NotWanted, new TypeTriage { Changes = 10, Ignored = 6, Noise = 4 }.Verdict);
            Assert.AreEqual(TypeTriage.Suggested, new TypeTriage { Changes = 10, Ignored = 3 }.Verdict);
            Assert.AreEqual(TypeTriage.Unchanged, new TypeTriage().Verdict);
            Assert.AreEqual(TypeTriage.NotWanted, new TypeTriage { Changes = 5, Cared = 5, NoiseCategory = true }.Verdict);
        }
        // ── Capturing a hand move of the test family (Solve / Corrected) ──

        private static void Same(Frame a, Frame b)
        {
            for (int k = 0; k < 3; k++)
            {
                Assert.AreEqual(a.O[k], b.O[k], 1e-6, "O");
                Assert.AreEqual(a.X[k], b.X[k], 1e-6, "X");
                Assert.AreEqual(a.Y[k], b.Y[k], 1e-6, "Y");
                Assert.AreEqual(a.Z[k], b.Z[k], 1e-6, "Z");
            }
        }

        [TestMethod]
        public void SolveIsTheInverseOfPlace()
        {
            var turned = new Frame { O = new[] { 10.0, 20, 3 }, X = new[] { 0.0, 1, 0 }, Y = new[] { -1.0, 0, 0 }, Z = new[] { 0.0, 0, 1 } };
            var ceiling = new Frame { O = new[] { 0.0, 0, 10 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, -1, 0 }, Z = new[] { 0.0, 0, -1 } };
            foreach (var src in new[] { turned, ceiling })
            foreach (var rot in new[] { new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 37.5 }, new[] { 180.0, 0, 90 }, new[] { 12.0, -30, 45 } })
            {
                var off = new[] { 150.0, -75.5, 2400 };
                var f = MappingMath.Place(src, off, rot);
                Assert.IsTrue(MappingMath.Solve(src, f, rot, out var o, out var r));
                CollectionAssert.AreEqual(off, o);
                for (int k = 0; k < 3; k++) Assert.AreEqual(rot[k], r[k], 1e-6);
                Same(f, MappingMath.Place(src, o, r));
            }
        }

        [TestMethod]
        public void SolveWithoutAPreferenceStillRoundTrips()
        {
            var src = new Frame { O = new[] { 1.0, 2, 3 }, X = new[] { 0.6, 0.8, 0 }, Y = new[] { -0.8, 0.6, 0 }, Z = new[] { 0.0, 0, 1 } };
            var f = MappingMath.Place(src, new[] { 0.0, 0, 0 }, new[] { 180.0, 0, 0 });
            Assert.IsTrue(MappingMath.Solve(src, f, null, out _, out var r));
            Same(f, MappingMath.Place(src, new double[3], r));
            var mirrored = new Frame { O = src.O, X = new[] { -0.6, -0.8, 0 }, Y = src.Y, Z = src.Z };
            Assert.IsFalse(MappingMath.Solve(src, mirrored, null, out _, out _));
        }

        [TestMethod]
        public void CorrectedKeepsHowRevitRealisedThePlacement()
        {
            // Ceiling device (Z down), level-based family: Revit placed it upright (flipped about X) and 0.5 ft lower.
            var src = new Frame { O = new[] { 0.0, 0, 10 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, -1, 0 }, Z = new[] { 0.0, 0, -1 } };
            var expected = MappingMath.Place(src, new double[3], new double[3]);
            var placed = new Frame { O = new[] { 0.0, 0, 9.5 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, 1, 0 }, Z = new[] { 0.0, 0, 1 } };
            Same(expected, MappingMath.Corrected(expected, placed, placed));   // not moved: same rule
            // The user turned it 90° in plan and moved it 1 ft along +X.
            var now = new Frame { O = new[] { 1.0, 0, 9.5 }, X = new[] { 0.0, 1, 0 }, Y = new[] { -1.0, 0, 0 }, Z = new[] { 0.0, 0, 1 } };
            var rule = MappingMath.Corrected(expected, placed, now);
            Assert.IsTrue(MappingMath.Solve(src, rule, new double[3], out var off, out var rot));
            Assert.AreEqual(304.8, off[0], 1e-6);
            Assert.AreEqual(0, off[2], 1e-6);
            // Placing with the solved rule points the plan direction where the user turned it.
            var again = MappingMath.Place(src, off, rot);
            var dir = MappingMath.PlanDirection(again);
            Assert.AreEqual(0, dir[0], 1e-6);
            Assert.AreEqual(1, dir[1], 1e-6);
        }
            [TestMethod]
        public void RealisationCarriesHowRevitPlacedIt()
        {
            // A hosted family placed 100 mm off the rule point (wall face) and turned 90° (the wall's direction).
            var rule = new Frame { O = new[] { 5.0, 5, 1 }, X = new[] { 1.0, 0, 0 }, Y = new[] { 0.0, 1, 0 }, Z = new[] { 0.0, 0, 1 } };
            var placed = new Frame { O = new[] { 5.0, 5 + 100 * Ft, 1 }, X = new[] { 0.0, 1, 0 }, Y = new[] { -1.0, 0, 0 }, Z = new[] { 0.0, 0, 1 } };
            var c = MappingMath.Realisation(rule, placed, true);
            Same(placed, MappingMath.Realise(rule, c));
            // The consultant element moves 1 ft along +X and turns 90°: ours follows with the same relation.
            var rule2 = new Frame { O = new[] { 6.0, 5, 1 }, X = new[] { 0.0, 1, 0 }, Y = new[] { -1.0, 0, 0 }, Z = new[] { 0.0, 0, 1 } };
            var now = MappingMath.Realise(rule2, c);
            Assert.AreEqual(6 - 100 * Ft, now.O[0], 1e-9);
            Assert.AreEqual(-1, now.X[0], 1e-9);
            // Axes only: the origin stays on the rule point.
            Assert.AreEqual(5, MappingMath.Realise(rule, MappingMath.Realisation(rule, placed, false)).O[1], 1e-9);
            Assert.AreSame(rule, MappingMath.Realise(rule, null));
        }
        }
}
