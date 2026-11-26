using System;
using System.Collections.Generic;

namespace ProjectPerseus.util
{
    /// <summary>
    /// Pure screen layout for parameter marker labels and icon rows (ui/ViolationOverlay): each item goes to the first
    /// candidate spot around its element that overlaps nothing placed before it and stays in the view. Labels try right,
    /// left, above, below, then rings further out; icon rows try centred on the element first. Items that find no free
    /// spot keep their first candidate. Leader() gives the line from a placed item back to its element. Tested in
    /// LabelPlacerTest.cs.
    /// </summary>
    public static class LabelPlacer
    {
        public struct Box
        {
            public double X, Y, W, H;
            public Box(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
            public double Right => X + W;
            public double Bottom => Y + H;
            public double CX => X + W / 2;
            public double CY => Y + H / 2;
            public bool Overlaps(Box o, double pad) =>
                X < o.Right + pad && o.X < Right + pad && Y < o.Bottom + pad && o.Y < Bottom + pad;
            public bool Inside(Box o) => X >= o.X && Y >= o.Y && Right <= o.Right && Bottom <= o.Bottom;
            public override string ToString() => $"({X:0},{Y:0} {W:0}x{H:0})";
        }

        public sealed class Item
        {
            public Box Source;        // the element on screen
            public double W, H;       // size of the label / icon row
            public bool Centred;      // icon row: first choice is on the element itself
            public Box Result;        // where it went
            public bool Displaced;    // not at its first choice (or not next to its element): draw a leader
        }

        private const double Pad = 2;       // gap kept between placed items
        private const int Rings = 8;        // how far out to search
        private const int CellSize = 64;    // grid for overlap checks

        public static void Place(IList<Item> items, Box view, double gap)
        {
            var grid = new Dictionary<long, List<Box>>();
            // Every element's box, so a spot nearer another element than its own (or covering one) can be avoided.
            var sources = new Dictionary<long, List<Box>>();
            var clamped = new Box[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                clamped[i] = Clamp(items[i].Source, view);
                Add(sources, clamped[i]);
            }
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var src = clamped[i];
                Box? chosen = null;
                double best = double.MaxValue;
                Box first = default(Box);
                int index = 0;
                // Score every free candidate (not first fit): a label belongs next to ITS element, so a spot that is
                // closer to a neighbour (the top element's label sitting on the bottom one) costs far more than walking
                // a little further round its own element.
                foreach (var c in Candidates(src, it.W, it.H, gap, it.Centred))
                {
                    int order = index++;
                    if (order == 0) first = c;
                    if (!c.Inside(view)) continue;
                    if (Collides(grid, c)) continue;
                    // An icon row on its own element is where it belongs, even when neighbouring elements overlap it.
                    double cost = it.Centred && order == 0 ? 0 : Cost(c, src, sources, order, gap);
                    if (cost < best) { best = cost; chosen = c; }
                    if (order == 0 && cost <= gap + 1) break; // first choice free and nowhere near a neighbour: take it
                }
                it.Result = chosen ?? first;
                // Labels beside the element on the right are "at home" (index 1 = first candidate) but still get a short
                // leader so the eye can follow it; icon rows only when moved off the element.
                it.Displaced = !it.Centred || !chosen.HasValue || !SameBox(it.Result, first);
                Add(grid, it.Result);
            }
        }

        // Gap between two boxes (0 when they touch or overlap).
        private static double Distance(Box a, Box b)
        {
            double dx = Math.Max(0, Math.Max(a.X - b.Right, b.X - a.Right));
            double dy = Math.Max(0, Math.Max(a.Y - b.Bottom, b.Y - a.Bottom));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Same(Box a, Box b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01 &&
                                                  Math.Abs(a.W - b.W) < 0.01 && Math.Abs(a.H - b.H) < 0.01;

        // Lower is better: near its own element, in the preferred order, never nearer a neighbour or on top of one.
        private static double Cost(Box c, Box own, Dictionary<long, List<Box>> sources, int order, double gap)
        {
            double dOwn = Distance(c, own);
            double cost = dOwn + order * 0.5;
            var seen = new HashSet<long>();
            foreach (var cell in Cells(new Box(c.X - dOwn - gap, c.Y - dOwn - gap, c.W + 2 * (dOwn + gap), c.H + 2 * (dOwn + gap))))
            {
                if (!sources.TryGetValue(cell, out var list)) continue;
                foreach (var s in list)
                {
                    if (Same(s, own)) continue;
                    long key = ((long)Math.Round(s.X * 8) << 32) ^ (long)Math.Round(s.Y * 8);
                    if (!seen.Add(key)) continue;
                    double d = Distance(c, s);
                    if (d <= 0) cost += 400;                         // covers another element
                    if (d < dOwn + gap) cost += 1000 + (dOwn + gap - d) * 10; // reads as that element's label
                }
            }
            return cost;
        }

        private static bool SameBox(Box a, Box b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

        // Huge elements (a wall across the plan) are reduced to their part in the view, so labels stay on screen.
        private static Box Clamp(Box s, Box view)
        {
            double x0 = Math.Max(s.X, view.X), y0 = Math.Max(s.Y, view.Y);
            double x1 = Math.Min(s.Right, view.Right), y1 = Math.Min(s.Bottom, view.Bottom);
            if (x1 < x0 || y1 < y0) return s; // off screen: leave it, the candidates won't fit and the item keeps its first spot
            return new Box(x0, y0, x1 - x0, y1 - y0);
        }

        private static IEnumerable<Box> Candidates(Box s, double w, double h, double gap, bool centred)
        {
            if (centred) yield return new Box(s.CX - w / 2, s.CY - h / 2, w, h);
            yield return new Box(s.Right + gap, s.Y, w, h);                       // right, top aligned
            yield return new Box(s.X - gap - w, s.Y, w, h);                       // left
            yield return new Box(s.CX - w / 2, s.Y - gap - h, w, h);              // above
            yield return new Box(s.CX - w / 2, s.Bottom + gap, w, h);             // below
            yield return new Box(s.Right + gap, s.Bottom - h, w, h);              // right, bottom aligned
            yield return new Box(s.X - gap - w, s.Bottom - h, w, h);              // left, bottom aligned
            // Rings further out: 16 directions, each step one item height (and width sideways) further.
            for (int r = 1; r <= Rings; r++)
            {
                double dx = s.W / 2 + gap + w / 2 + r * (w * 0.5 + gap);
                double dy = s.H / 2 + gap + h / 2 + r * (h + gap);
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8;
                    double cx = s.CX + Math.Cos(a) * dx, cy = s.CY - Math.Sin(a) * dy;
                    yield return new Box(cx - w / 2, cy - h / 2, w, h);
                }
            }
        }

        private static IEnumerable<long> Cells(Box b)
        {
            int x0 = (int)Math.Floor((b.X - Pad) / CellSize), x1 = (int)Math.Floor((b.Right + Pad) / CellSize);
            int y0 = (int)Math.Floor((b.Y - Pad) / CellSize), y1 = (int)Math.Floor((b.Bottom + Pad) / CellSize);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    yield return ((long)x << 32) ^ (uint)y;
        }

        private static bool Collides(Dictionary<long, List<Box>> grid, Box b)
        {
            foreach (var cell in Cells(b))
                if (grid.TryGetValue(cell, out var list))
                    foreach (var o in list)
                        if (b.Overlaps(o, Pad)) return true;
            return false;
        }

        private static void Add(Dictionary<long, List<Box>> grid, Box b)
        {
            foreach (var cell in Cells(b))
            {
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<Box>();
                list.Add(b);
            }
        }

        /// <summary>
        /// The leader from a placed item back to its element: from the item's edge facing the element to the nearest
        /// point of the element (its centre when it is small). False when they touch (no line needed).
        /// </summary>
        public static bool Leader(Box item, Box source, out double x1, out double y1, out double x2, out double y2)
        {
            bool small = source.W < 24 && source.H < 24;
            // Element end: nearest point of the element to the item's centre (centre for small elements).
            x2 = small ? source.CX : Math.Max(source.X, Math.Min(item.CX, source.Right));
            y2 = small ? source.CY : Math.Max(source.Y, Math.Min(item.CY, source.Bottom));
            // Item end: nearest point of the item to that.
            x1 = Math.Max(item.X, Math.Min(x2, item.Right));
            y1 = Math.Max(item.Y, Math.Min(y2, item.Bottom));
            double dx = x2 - x1, dy = y2 - y1;
            return dx * dx + dy * dy > 16; // more than 4 px apart
        }
    }
}
