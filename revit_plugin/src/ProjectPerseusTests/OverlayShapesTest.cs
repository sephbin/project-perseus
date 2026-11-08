using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.federator;
using Pt = ProjectPerseus.federator.OverlayShapes.Pt;

namespace ProjectPerseusTests
{
    [TestClass]
    public class OverlayShapesTest
    {
        [TestMethod]
        public void RoundedRectStaysInsideItsBoundsAndHasTheRightPerimeter()
        {
            var path = OverlayShapes.RoundedRect(0, 0, 10, 4, 1, 8);
            Assert.IsTrue(path.All(p => p.U >= -1e-9 && p.U <= 10 + 1e-9 && p.V >= -1e-9 && p.V <= 4 + 1e-9));
            // Perimeter = straight sides + one full circle of radius 1 (polygon approximation, slightly shorter).
            double expected = 2 * (10 - 2) + 2 * (4 - 2) + 2 * Math.PI;
            Assert.AreEqual(expected, OverlayShapes.PathLength(path, true), 0.05);
        }

        [TestMethod]
        public void RadiusIsClampedToHalfTheShortSide()
        {
            var path = OverlayShapes.RoundedRect(0, 0, 10, 2, 5, 8);
            Assert.IsTrue(path.All(p => p.V >= -1e-9 && p.V <= 2 + 1e-9));
        }

        [TestMethod]
        public void DashesFollowThePathWithTheGivenLengths()
        {
            var line = new List<Pt> { new Pt(0, 0), new Pt(10, 0) };
            var dashes = OverlayShapes.Dash(line, false, 2, 1);
            Assert.AreEqual(4, dashes.Count); // 0-2, 3-5, 6-8, 9-10
            Assert.AreEqual(2, OverlayShapes.PathLength(dashes[0], false), 1e-9);
            Assert.AreEqual(3, dashes[1][0].U, 1e-9);
            Assert.AreEqual(1, OverlayShapes.PathLength(dashes[3], false), 1e-9);

            // Around a corner: total dashed length is ~2/3 of the perimeter.
            var rect = OverlayShapes.RoundedRect(0, 0, 9, 9, 0);
            double on = OverlayShapes.Dash(rect, true, 2, 1).Sum(d => OverlayShapes.PathLength(d, false));
            Assert.AreEqual(36 * 2.0 / 3.0, on, 1.0);
        }

        [TestMethod]
        public void ArrowIsClippedBetweenTheTwoRectangles()
        {
            var oldRect = new[] { 0.0, 0, 2, 2 };
            var newRect = new[] { 10.0, 0, 12, 2 };
            Assert.IsTrue(OverlayShapes.ClipBetween(new Pt(1, 1), new Pt(11, 1), oldRect, newRect, 0.5, out var from, out var to));
            Assert.AreEqual(2.5, from.U, 1e-9);
            Assert.AreEqual(9.5, to.U, 1e-9);

            // Overlapping rectangles: no clean gap, caller falls back to centre-to-centre.
            Assert.IsFalse(OverlayShapes.ClipBetween(new Pt(1, 1), new Pt(2, 1), oldRect, new[] { 1.0, 0, 3, 2 }, 0.1, out _, out _));
        }

        [TestMethod]
        public void StrokeAndShapesProduceWholeTriangles()
        {
            var tris = new List<double>();
            OverlayShapes.Stroke(tris, OverlayShapes.RoundedRect(0, 0, 5, 5, 1), true, 0.1);
            OverlayShapes.Arrow(tris, new Pt(0, 0), new Pt(5, 0), 0.1, 1, 0.5);
            OverlayShapes.Glyph(tris, '+', new Pt(0, 0), 1, 0.1);
            OverlayShapes.Glyph(tris, '~', new Pt(0, 0), 1, 0.1);
            Assert.IsTrue(tris.Count > 0);
            Assert.AreEqual(0, tris.Count % 6);
            Assert.IsFalse(tris.Any(double.IsNaN));
        }
        [TestMethod]
        public void ClosedStrokeIsOneMitredRibbon()
        {
            var tris = new List<double>();
            var square = new List<Pt> { new Pt(0, 0), new Pt(10, 0), new Pt(10, 10), new Pt(0, 10) };
            OverlayShapes.Stroke(tris, square, true, 1);
            Assert.AreEqual(8 * 6, tris.Count); // 4 sides x 2 triangles, no extra join pieces

            // Outer corners sit exactly on the mitre (corner +/- half-width on both axes), so the ribbon has no notch.
            var us = Enumerable.Range(0, tris.Count / 2).Select(i => tris[2 * i]).ToList();
            var vs = Enumerable.Range(0, tris.Count / 2).Select(i => tris[2 * i + 1]).ToList();
            Assert.AreEqual(-1, us.Min(), 1e-9);
            Assert.AreEqual(11, us.Max(), 1e-9);
            Assert.AreEqual(-1, vs.Min(), 1e-9);
            Assert.AreEqual(11, vs.Max(), 1e-9);
        }

        [TestMethod]
        public void StrokeWidthIsConstantAlongACurve()
        {
            // Every ribbon vertex of a stroked circle sits half-width from the centre line (within the miter factor).
            var circle = OverlayShapes.RoundedRect(-5, -5, 5, 5, 5, 16);
            var tris = new List<double>();
            OverlayShapes.Stroke(tris, circle, true, 0.5);
            for (int i = 0; i < tris.Count; i += 2)
            {
                double r = Math.Sqrt(tris[i] * tris[i] + tris[i + 1] * tris[i + 1]);
                Assert.IsTrue(Math.Abs(r - 5) <= 0.5 * 1.01 + 1e-9, $"vertex at radius {r}");
            }
        }

        [TestMethod]
        public void DuplicatePointsAndRoundCapsAreHandled()
        {
            var tris = new List<double>();
            OverlayShapes.Stroke(tris, new List<Pt> { new Pt(0, 0), new Pt(0, 0), new Pt(5, 0) }, false, 0.5, true);
            Assert.AreEqual((2 + 16 + 16) * 6, tris.Count); // one segment + two 16-sided caps
            Assert.IsFalse(tris.Any(double.IsNaN));
        }
    }
}
