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

        /// <summary>Stroke a polyline: one quad (2 triangles) per segment plus a disc at sharp joints (> ~45°) so corners don't notch.</summary>
        public static void Stroke(List<double> tris, IList<Pt> line, bool closed, double half)
        {
            int n = closed ? line.Count : line.Count - 1;
            for (int i = 0; i < n; i++)
            {
                Pt a = line[i], b = line[(i + 1) % line.Count];
                Pt d = b - a;
                double len = d.Length;
                if (len < 1e-12) continue;
                Pt p = new Pt(-d.V / len * half, d.U / len * half);
                Tri(tris, a + p, a - p, b + p);
                Tri(tris, a - p, b - p, b + p);
            }
            // Round joins (cheap: 6-sided discs) where the direction turns sharply.
            int first = closed ? 0 : 1, last = closed ? line.Count : line.Count - 1;
            for (int i = first; i < last; i++)
            {
                Pt prev = line[(i - 1 + line.Count) % line.Count], cur = line[i], next = line[(i + 1) % line.Count];
                Pt d1 = cur - prev, d2 = next - cur;
                double l1 = d1.Length, l2 = d2.Length;
                if (l1 < 1e-12 || l2 < 1e-12) continue;
                double cos = (d1.U * d2.U + d1.V * d2.V) / (l1 * l2);
                if (cos < 0.7) Disc(tris, cur, half, 6); // > ~45°: arcs (small steps) need no join discs
            }
        }

        public static void Disc(List<double> tris, Pt c, double r, int segments = 16)
        {
            for (int i = 0; i < segments; i++)
            {
                double a0 = 2 * Math.PI * i / segments, a1 = 2 * Math.PI * (i + 1) / segments;
                Tri(tris, c, new Pt(c.U + r * Math.Cos(a0), c.V + r * Math.Sin(a0)), new Pt(c.U + r * Math.Cos(a1), c.V + r * Math.Sin(a1)));
            }
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

        /// <summary>Badge glyph strokes centred on c: '+' added, '−' deleted, '~' modified.</summary>
        public static void Glyph(List<double> tris, char glyph, Pt c, double size, double half)
        {
            switch (glyph)
            {
                case '+':
                    Stroke(tris, new[] { new Pt(c.U - size, c.V), new Pt(c.U + size, c.V) }, false, half);
                    Stroke(tris, new[] { new Pt(c.U, c.V - size), new Pt(c.U, c.V + size) }, false, half);
                    break;
                case '-':
                    Stroke(tris, new[] { new Pt(c.U - size, c.V), new Pt(c.U + size, c.V) }, false, half);
                    break;
                default:
                    var wave = new List<Pt>();
                    for (int i = 0; i <= 10; i++)
                    {
                        double t = -1 + 2.0 * i / 10;
                        wave.Add(new Pt(c.U + t * size, c.V + Math.Sin(t * Math.PI) * size * 0.45));
                    }
                    Stroke(tris, wave, false, half);
                    break;
            }
        }

        private static void Tri(List<double> t, Pt a, Pt b, Pt c)
        {
            t.Add(a.U); t.Add(a.V); t.Add(b.U); t.Add(b.V); t.Add(c.U); t.Add(c.V);
        }
    }
}
