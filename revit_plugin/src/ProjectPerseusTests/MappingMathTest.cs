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
    }
}
