using System;
using System.Collections.Generic;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// 2D vector shapes for DirectContext3D, SVG-style: paths (rounded rectangles, dashes), strokes turned into
    /// triangles, filled discs and arrows, small glyphs. Coordinates are (u, v) in the view plane; triangles are
    /// returned flat as u,v pairs (6 numbers per triangle). Pure: unit-tested in ProjectPerseusTests/OverlayShapesTest.cs.
    /// ChangeHighlightServer lifts them into 3D on the view plane at the element's depth.
    /// </summary>
    public static class OverlayShapes
    {
        public struct Pt
        {
            public double U, V;
            public Pt(double u, double v) { U = u; V = v; }
            public static Pt operator +(Pt a, Pt b) => new Pt(a.U + b.U, a.V + b.V);
            public static Pt operator -(Pt a, Pt b) => new Pt(a.U - b.U, a.V - b.V);
            public static Pt operator *(Pt a, double k) => new Pt(a.U * k, a.V * k);
            public double Length => Math.Sqrt(U * U + V * V);
        }

        /// <summary>Closed rounded rectangle path (no repeated end point). Radius is clamped to half the shorter side.</summary>
        public static List<Pt> RoundedRect(double u0, double v0, double u1, double v1, double radius, int segmentsPerCorner = 6)
        {
            double r = Math.Max(0, Math.Min(radius, Math.Min(u1 - u0, v1 - v0) / 2));
            var path = new List<Pt>();
            if (r <= 1e-12)
            {
                path.Add(new Pt(u0, v0)); path.Add(new Pt(u1, v0)); path.Add(new Pt(u1, v1)); path.Add(new Pt(u0, v1));
                return path;
            }
            // Corners counter-clockwise starting bottom-right: centre, start angle.
            var corners = new[]
            {
                new { C = new Pt(u1 - r, v0 + r), A = -Math.PI / 2 },
                new { C = new Pt(u1 - r, v1 - r), A = 0.0 },
                new { C = new Pt(u0 + r, v1 - r), A = Math.PI / 2 },
                new { C = new Pt(u0 + r, v0 + r), A = Math.PI },
            };
            foreach (var c in corners)
                for (int i = 0; i <= segmentsPerCorner; i++)
                {
                    double a = c.A + Math.PI / 2 * i / segmentsPerCorner;
                    path.Add(new Pt(c.C.U + r * Math.Cos(a), c.C.V + r * Math.Sin(a)));
                }
            return path;
        }

        /// <summary>Convex hull, counter-clockwise, no repeated point (monotone chain). 1-2 points for degenerate input.</summary>
        public static List<Pt> ConvexHull(IEnumerable<Pt> points)
        {
            var p = new List<Pt>(points);
            p.Sort((a, b) => a.U != b.U ? a.U.CompareTo(b.U) : a.V.CompareTo(b.V));
            if (p.Count < 3)
            {
                if (p.Count == 2 && (p[1] - p[0]).Length < 1e-12) p.RemoveAt(1);
                return p;
            }
            var h = new Pt[2 * p.Count];
            int k = 0;
            for (int i = 0; i < p.Count; i++)
            {
                while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 1e-18) k--;
                h[k++] = p[i];
            }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 1e-18) k--;
                h[k++] = p[i];
            }
            var hull = new List<Pt>();
            for (int i = 0; i < Math.Max(1, k - 1); i++) hull.Add(h[i]);
            if (hull.Count == 2 && (hull[1] - hull[0]).Length < 1e-12) hull.RemoveAt(1);
            return hull;
        }

        private static double Cross(Pt o, Pt a, Pt b) => (a.U - o.U) * (b.V - o.V) - (a.V - o.V) * (b.U - o.U);

        /// <summary>
        /// Closed path at distance r around a convex counter-clockwise polygon (its Minkowski sum with a disc): every
        /// edge moved out by r, corners rounded with radius r. A segment gives a capsule, a point a circle.
        /// </summary>
        public static List<Pt> Inflate(IList<Pt> hull, double r, int segmentsPerQuarter = 4)
        {
            var path = new List<Pt>();
            int n = hull.Count;
            if (n == 0) return path;
            if (n == 1)
            {
                for (int i = 0; i < 4 * segmentsPerQuarter; i++)
                {
                    double a = 2 * Math.PI * i / (4 * segmentsPerQuarter);
                    path.Add(new Pt(hull[0].U + r * Math.Cos(a), hull[0].V + r * Math.Sin(a)));
                }
                return path;
            }
            for (int i = 0; i < n; i++)
            {
                Pt prev = hull[(i + n - 1) % n], cur = hull[i], next = hull[(i + 1) % n];
                Pt d0 = cur - prev, d1 = next - cur;
                double a0 = Math.Atan2(-d0.U, d0.V);   // outward normal of prev -> cur (CCW polygon): (dv, -du)
                double a1 = Math.Atan2(-d1.U, d1.V);
                while (a1 < a0 - 1e-12) a1 += 2 * Math.PI;
                int steps = Math.Max(1, (int)Math.Ceiling((a1 - a0) / (Math.PI / 2) * segmentsPerQuarter));
                for (int s = 0; s <= steps; s++)
                {
                    double a = a0 + (a1 - a0) * s / steps;
                    path.Add(new Pt(cur.U + r * Math.Cos(a), cur.V + r * Math.Sin(a)));
                }
            }
            return path;
        }

        public static double PathLength(IList<Pt> path, bool closed)
        {
            double len = 0;
            int n = closed ? path.Count : path.Count - 1;
            for (int i = 0; i < n; i++) len += (path[(i + 1) % path.Count] - path[i]).Length;
            return len;
        }

        /// <summary>Splits a path into dashes (open polylines) of the given lengths, following its corners.</summary>
        public static List<List<Pt>> Dash(IList<Pt> path, bool closed, double dash, double gap)
        {
            var result = new List<List<Pt>>();
            if (path.Count < 2 || dash <= 0) return result;
            int n = closed ? path.Count : path.Count - 1;
            bool on = true;
            double left = dash;
            var current = new List<Pt> { path[0] };
            for (int i = 0; i < n; i++)
            {
                Pt a = path[i], b = path[(i + 1) % path.Count];
                double segLen = (b - a).Length, pos = 0;
                while (segLen - pos > left)
                {
                    pos += left;
                    Pt p = a + (b - a) * (pos / segLen);
                    if (on) { current.Add(p); result.Add(current); }
                    else current = new List<Pt> { p };
                    on = !on;
                    left = on ? dash : gap;
                }
                left -= segLen - pos;
                if (on) current.Add(b);
            }
            if (on && current.Count > 1) result.Add(current);
            return result;
        }

        /// <summary>
        /// Strokes a polyline SVG-style: one continuous ribbon with mitred joins (each vertex offset along the bisector
        /// of its two segments, miter clamped to 4× the half-width), so arcs and corners have no notches. Open lines
        /// can get round caps.
        /// </summary>
        public static void Stroke(List<double> tris, IList<Pt> line, bool closed, double half, bool roundCaps = false)
        {
            var pts = new List<Pt>(line.Count);
            foreach (var p in line)
                if (pts.Count == 0 || (p - pts[pts.Count - 1]).Length > 1e-12) pts.Add(p);
            if (closed && pts.Count > 2 && (pts[0] - pts[pts.Count - 1]).Length <= 1e-12) pts.RemoveAt(pts.Count - 1);
            int n = pts.Count;
            if (n < 2) return;

            var left = new Pt[n];
            var right = new Pt[n];
            for (int i = 0; i < n; i++)
            {
                bool hasPrev = closed || i > 0, hasNext = closed || i < n - 1;
                Pt dPrev = hasPrev ? Unit(pts[i] - pts[(i - 1 + n) % n]) : default(Pt);
                Pt dNext = hasNext ? Unit(pts[(i + 1) % n] - pts[i]) : default(Pt);
                if (!hasPrev) dPrev = dNext;
                if (!hasNext) dNext = dPrev;
                Pt nPrev = new Pt(-dPrev.V, dPrev.U), nNext = new Pt(-dNext.V, dNext.U);
                Pt sum = nPrev + nNext;
                Pt m = sum.Length < 1e-9 ? nNext : sum * (1 / sum.Length);
                double cos = m.U * nNext.U + m.V * nNext.V;
                double miter = half / Math.Max(cos, 0.25);
                left[i] = pts[i] + m * miter;
                right[i] = pts[i] - m * miter;
            }
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n;
                Tri(tris, left[i], right[i], left[j]);
                Tri(tris, right[i], right[j], left[j]);
            }
            if (roundCaps && !closed)
            {
                Disc(tris, pts[0], half, 16);
                Disc(tris, pts[n - 1], half, 16);
            }
        }

        private static Pt Unit(Pt v)
        {
            double l = v.Length;
            return l < 1e-12 ? new Pt(1, 0) : v * (1 / l);
        }

        public static void Disc(List<double> tris, Pt c, double r, int segments = 16)
        {
            for (int i = 0; i < segments; i++)
            {
                double a0 = 2 * Math.PI * i / segments, a1 = 2 * Math.PI * (i + 1) / segments;
                Tri(tris, c, new Pt(c.U + r * Math.Cos(a0), c.V + r * Math.Sin(a0)), new Pt(c.U + r * Math.Cos(a1), c.V + r * Math.Sin(a1)));
            }
        }

        /// <summary>Closed circle path (no repeated end point), counter-clockwise from angle 0.</summary>
        public static List<Pt> Circle(Pt c, double r, int segments = 48)
        {
            var path = new List<Pt>(segments);
            for (int i = 0; i < segments; i++)
            {
                double a = 2 * Math.PI * i / segments;
                path.Add(new Pt(c.U + r * Math.Cos(a), c.V + r * Math.Sin(a)));
            }
            return path;
        }

        /// <summary>Arrow from a to b: stroked shaft ending at the base of a filled head whose tip is at b.</summary>
        public static void Arrow(List<double> tris, Pt a, Pt b, double shaftHalf, double headLength, double headHalf)
        {
            Pt d = b - a;
            double len = d.Length;
            if (len < 1e-12) return;
            Pt dir = d * (1 / len);
            double head = Math.Min(headLength, len * 0.6);
            Pt baseC = b - dir * head;
            Stroke(tris, new[] { a, baseC + dir * (shaftHalf * 0.5) }, false, shaftHalf);
            Pt n = new Pt(-dir.V, dir.U);
            double hw = headHalf * head / headLength;
            Tri(tris, b, baseC + n * hw, baseC - n * hw);
        }

        /// <summary>
        /// Shortens segment a→b so it starts where it leaves rectangle A and ends where it enters rectangle B (gap
        /// added). Returns false when the rectangles overlap along the way (caller draws centre to centre).
        /// </summary>
        public static bool ClipBetween(Pt a, Pt b, double[] rectA, double[] rectB, double gap, out Pt from, out Pt to)
        {
            from = a; to = b;
            double tA = ExitT(a, b, rectA), tB = 1 - ExitT(b, a, rectB);
            if (double.IsNaN(tA) || double.IsNaN(tB) || tB <= tA) return false;
            Pt d = b - a;
            double len = d.Length;
            if (len < 1e-12) return false;
            double g = gap / len;
            if (tB - tA <= 2 * g) return false;
            from = a + d * (tA + g);
            to = a + d * (tB - g);
            return true;
        }

        // Parameter (0..1 along p→q) where the ray from p (inside rect [u0,v0,u1,v1]) leaves the rect.
        private static double ExitT(Pt p, Pt q, double[] r)
        {
            double t = 1;
            double du = q.U - p.U, dv = q.V - p.V;
            if (du > 1e-12) t = Math.Min(t, (r[2] - p.U) / du); else if (du < -1e-12) t = Math.Min(t, (r[0] - p.U) / du);
            if (dv > 1e-12) t = Math.Min(t, (r[3] - p.V) / dv); else if (dv < -1e-12) t = Math.Min(t, (r[1] - p.V) / dv);
            return t < 0 ? double.NaN : t;
        }

        /// <summary>Badge glyph strokes centred on c: '+' added, '−' deleted, 'r' replaced (⇌), '~' modified.</summary>
        public static void Glyph(List<double> tris, char glyph, Pt c, double size, double half)
        {
            switch (glyph)
            {
                case '+':
                    Stroke(tris, new[] { new Pt(c.U - size, c.V), new Pt(c.U + size, c.V) }, false, half, true);
                    Stroke(tris, new[] { new Pt(c.U, c.V - size), new Pt(c.U, c.V + size) }, false, half, true);
                    break;
                case '-':
                    Stroke(tris, new[] { new Pt(c.U - size, c.V), new Pt(c.U + size, c.V) }, false, half, true);
                    break;
                case 'r':
                    // ⇌: upper arrow to the right (barb up), lower arrow to the left (barb down).
                    double dy = size * 0.36, barb = size * 0.5;
                    Stroke(tris, new[] { new Pt(c.U - size, c.V + dy), new Pt(c.U + size, c.V + dy), new Pt(c.U + size - barb, c.V + dy + barb) }, false, half, true);
                    Stroke(tris, new[] { new Pt(c.U + size, c.V - dy), new Pt(c.U - size, c.V - dy), new Pt(c.U - size + barb, c.V - dy - barb) }, false, half, true);
                    break;
                default:
                    // One smooth period of a sine, finely sampled so the mitred ribbon reads as a curve.
                    var wave = new List<Pt>();
                    const int samples = 28;
                    for (int i = 0; i <= samples; i++)
                    {
                        double t = -1 + 2.0 * i / samples;
                        wave.Add(new Pt(c.U + t * size, c.V + Math.Sin(t * Math.PI) * size * 0.42));
                    }
                    Stroke(tris, wave, false, half, true);
                    break;
            }
        }

        private static void Tri(List<double> t, Pt a, Pt b, Pt c)
        {
            t.Add(a.U); t.Add(a.V); t.Add(b.U); t.Add(b.V); t.Add(c.U); t.Add(c.V);
        }
    }
}
