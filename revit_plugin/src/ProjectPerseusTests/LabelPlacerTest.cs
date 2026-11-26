using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectPerseus.util;
using Box = ProjectPerseus.util.LabelPlacer.Box;

namespace ProjectPerseusTests
{
    [TestClass]
    public class LabelPlacerTest
    {
        private static readonly Box View = new Box(0, 0, 1000, 800);

        private static LabelPlacer.Item Label(double x, double y, double w = 80, double h = 16) =>
            new LabelPlacer.Item { Source = new Box(x, y, 20, 20), W = w, H = h };

        [TestMethod]
        public void LabelGoesRightOfItsElement()
        {
            var a = Label(100, 100);
            LabelPlacer.Place(new List<LabelPlacer.Item> { a }, View, 4);
            Assert.AreEqual(124, a.Result.X, 1e-9);
            Assert.AreEqual(100, a.Result.Y, 1e-9);
            Assert.IsTrue(a.Displaced); // labels always get a leader
        }

        [TestMethod]
        public void NeighbouringLabelsDontOverlap()
        {
            // Five elements in a tight row: every label would sit on its neighbour's.
            var items = new List<LabelPlacer.Item>();
            for (int i = 0; i < 5; i++) items.Add(Label(100 + i * 30, 300));
            LabelPlacer.Place(items, View, 4);
            for (int i = 0; i < items.Count; i++)
            {
                Assert.IsTrue(items[i].Result.Inside(View), $"item {i} left the view: {items[i].Result}");
                for (int j = i + 1; j < items.Count; j++)
                    Assert.IsFalse(items[i].Result.Overlaps(items[j].Result, 0), $"{i} {items[i].Result} overlaps {j} {items[j].Result}");
            }
        }

        [TestMethod]
        public void StackedElementsKeepTheirLabelsOnTheirOwnSide()
        {
            // Narrow view: labels fit only above or below. First fit put the bottom element's label ABOVE it, i.e. right
            // under the top element, reading as the top one's label.
            var view = new Box(0, 0, 200, 400);
            var top = new LabelPlacer.Item { Source = new Box(90, 100, 20, 20), W = 150, H = 40 };
            var bottom = new LabelPlacer.Item { Source = new Box(90, 170, 20, 20), W = 150, H = 40 };
            LabelPlacer.Place(new List<LabelPlacer.Item> { top, bottom }, view, 4);
            Assert.IsTrue(top.Result.Bottom <= 100, $"top label should be above the top element: {top.Result}");
            Assert.IsTrue(bottom.Result.Y >= 190, $"bottom label should be below the bottom element: {bottom.Result}");
        }

        [TestMethod]
        public void LabelAtTheRightEdgeFlipsLeft()
        {
            var a = Label(960, 100);
            LabelPlacer.Place(new List<LabelPlacer.Item> { a }, View, 4);
            Assert.AreEqual(960 - 4 - 80, a.Result.X, 1e-9);
        }

        [TestMethod]
        public void IconRowsSitOnTheirElementUntilCrowded()
        {
            var icon = new LabelPlacer.Item { Source = new Box(400, 400, 40, 40), W = 18, H = 18, Centred = true };
            var other = new LabelPlacer.Item { Source = new Box(405, 405, 30, 30), W = 18, H = 18, Centred = true };
            LabelPlacer.Place(new List<LabelPlacer.Item> { icon, other }, View, 4);
            Assert.AreEqual(411, icon.Result.X, 1e-9);   // centred on its element
            Assert.IsFalse(icon.Displaced);              // no leader
            Assert.IsTrue(other.Displaced);              // pushed off: leader back to it
            Assert.IsFalse(icon.Result.Overlaps(other.Result, 0));
        }

        [TestMethod]
        public void HugeElementsAreClampedToTheView()
        {
            // A wall running far past both sides of the view still gets its label on screen.
            var a = new LabelPlacer.Item { Source = new Box(-5000, 200, 20000, 10), W = 80, H = 16 };
            LabelPlacer.Place(new List<LabelPlacer.Item> { a }, View, 4);
            Assert.IsTrue(a.Result.Inside(View), a.Result.ToString());
        }

        [TestMethod]
        public void LeaderRunsFromTheLabelEdgeToTheElement()
        {
            Assert.IsTrue(LabelPlacer.Leader(new Box(200, 100, 80, 16), new Box(100, 100, 20, 20), out double x1, out double y1, out double x2, out double y2));
            Assert.AreEqual(200, x1, 1e-9);  // label's left edge
            Assert.AreEqual(110, x2, 1e-9);  // small element: its centre
            Assert.AreEqual(110, y2, 1e-9);
            // Large element: the nearest point of its box.
            Assert.IsTrue(LabelPlacer.Leader(new Box(500, 120, 80, 16), new Box(100, 100, 200, 100), out x1, out y1, out x2, out y2));
            Assert.AreEqual(300, x2, 1e-9);  // nearest point on the element's right edge
            Assert.AreEqual(128, y2, 1e-9);
            Assert.IsFalse(LabelPlacer.Leader(new Box(150, 150, 20, 10), new Box(100, 100, 200, 100), out _, out _, out _, out _)); // inside: none
        }
    }
}
