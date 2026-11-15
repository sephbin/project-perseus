using System;

namespace ProjectPerseus.mapping
{
    /// <summary>A placement frame: origin + axes (host feet). Consultant elements give one; mapped families are placed by one.</summary>
    public class Frame
    {
        public double[] O = { 0, 0, 0 };
        public double[] X = { 1, 0, 0 };
        public double[] Y = { 0, 1, 0 };
        public double[] Z = { 0, 0, 1 };

        public double[] OfPoint(double x, double y, double z) => new[]
        {
            O[0] + x * X[0] + y * Y[0] + z * Z[0],
            O[1] + x * X[1] + y * Y[1] + z * Z[1],
            O[2] + x * X[2] + y * Y[2] + z * Z[2],
        };

        public double[] OfVector(double x, double y, double z) => new[]
        {
            x * X[0] + y * Y[0] + z * Z[0],
            x * X[1] + y * Y[1] + z * Z[1],
            x * X[2] + y * Y[2] + z * Z[2],
        };

        /// <summary>Plan rotation of the X axis (radians, from host +X, counter-clockwise).</summary>
        public double PlanAngle => Math.Atan2(X[1], X[0]);
    }

    /// <summary>
    /// Where a mapped family goes relative to the consultant element it stands for (mapping/FamilyPlacer). The offset's
    /// X / Y run along the consultant element's direction in plan (so it turns with the element) and Z is always up
    /// (a ceiling device's own Z points down, which made heights confusing); the rotation turns our family about the
    /// consultant element's X, then Y, then Z axes. Pure: unit-tested in ProjectPerseusTests/MappingMathTest.cs.
    /// </summary>
    public static class MappingMath
    {
        public const double MmPerFoot = 304.8;

        public static Frame Place(Frame source, double[] offsetMm, double[] rotationDeg)
        {
            offsetMm = offsetMm ?? new double[3];
            rotationDeg = rotationDeg ?? new double[3];
            var px = PlanDirection(source);
            double ox = offsetMm[0] / MmPerFoot, oy = offsetMm[1] / MmPerFoot, oz = offsetMm[2] / MmPerFoot;
            var o = new[] { source.O[0] + px[0] * ox - px[1] * oy, source.O[1] + px[1] * ox + px[0] * oy, source.O[2] + oz };
            var r = Rotation(rotationDeg[0], rotationDeg[1], rotationDeg[2]);
            // Columns of r = our axes in the source's local coordinates.
            return new Frame
            {
                O = o,
                X = Unit(source.OfVector(r[0, 0], r[1, 0], r[2, 0])),
                Y = Unit(source.OfVector(r[0, 1], r[1, 1], r[2, 1])),
                Z = Unit(source.OfVector(r[0, 2], r[1, 2], r[2, 2])),
            };
        }

        /// <summary>
        /// The rule frame that makes the family land at <paramref name="now"/>, given that the rule frame
        /// <paramref name="expected"/> made it land at <paramref name="placed"/> (Revit may realise a frame differently, e.g. a
        /// level-based family stays upright). The same expected -> placed relation (in expected's axes) is applied to now.
        /// </summary>
        public static Frame Corrected(Frame expected, Frame placed, Frame now)
        {
            var E = new[] { expected.X, expected.Y, expected.Z };
            var P = new[] { placed.X, placed.Y, placed.Z };
            var N = new[] { now.X, now.Y, now.Z };
            // C[i,j] = E_i · P_j (placed axes in expected's coordinates); ct = placed origin in expected's coordinates.
            var c = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) c[i, j] = Dot(E[i], P[j]);
            var d = Sub(placed.O, expected.O);
            var ct = new[] { Dot(E[0], d), Dot(E[1], d), Dot(E[2], d) };
            // now = E' · C  =>  E'_i = sum_j C[i,j] N_j
            var axes = new double[3][];
            for (int i = 0; i < 3; i++)
            {
                axes[i] = new double[3];
                for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) axes[i][k] += c[i, j] * N[j][k];
                axes[i] = Unit(axes[i]);
            }
            var o = (double[])now.O.Clone();
            for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) o[k] -= ct[i] * axes[i][k];
            return new Frame { O = o, X = axes[0], Y = axes[1], Z = axes[2] };
        }

        /// <summary>
        /// Inverse of Place: the offset (mm, plan axes of the source + up) and rotation (° about the source's X, Y, Z) that put a
        /// family at <paramref name="target"/>. Of the two equivalent rotations the one nearer <paramref name="prefer"/> is
        /// returned. False when the target is mirrored relative to the source (no rotation gives it).
        /// </summary>
        public static bool Solve(Frame source, Frame target, double[] prefer, out double[] offsetMm, out double[] rotationDeg)
        {
            var px = PlanDirection(source);
            var d = Sub(target.O, source.O);
            offsetMm = new[]
            {
                Tidy((d[0] * px[0] + d[1] * px[1]) * MmPerFoot, 0.1),
                Tidy((-d[0] * px[1] + d[1] * px[0]) * MmPerFoot, 0.1),
                Tidy(d[2] * MmPerFoot, 0.1),
            };
            var S = new[] { source.X, source.Y, source.Z };
            var T = new[] { target.X, target.Y, target.Z };
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = Dot(S[i], T[j]);
            double det = r[0, 0] * (r[1, 1] * r[2, 2] - r[1, 2] * r[2, 1]) - r[0, 1] * (r[1, 0] * r[2, 2] - r[1, 2] * r[2, 0])
                       + r[0, 2] * (r[1, 0] * r[2, 1] - r[1, 1] * r[2, 0]);
            rotationDeg = (double[])(prefer ?? new double[3]).Clone();
            if (det < 0) return false;
            const double toDeg = 180 / Math.PI;
            double sb = Math.Max(-1, Math.Min(1, -r[2, 0]));
            double[] a1, a2;
            if (Math.Abs(sb) > 1 - 1e-9)
            {
                // Gimbal lock (Y = ±90°): only X - Z (or X + Z) is defined; keep the preferred X.
                double b = Math.Sign(sb) * 90;
                double x = prefer?[0] ?? 0;
                double zMinusX = Math.Atan2(-r[0, 1], r[1, 1]) * toDeg;   // with X = 0
                double z = sb > 0 ? zMinusX + x : zMinusX - x;
                a1 = a2 = new[] { x, b, z };
            }
            else
            {
                double b = Math.Asin(sb) * toDeg;
                double x = Math.Atan2(r[2, 1], r[2, 2]) * toDeg, z = Math.Atan2(r[1, 0], r[0, 0]) * toDeg;
                a1 = new[] { x, b, z };
                a2 = new[] { x + 180, 180 - b, z + 180 };
            }
            double[] Norm(double[] a) => new[] { Tidy(Wrap(a[0]), 0.01), Tidy(Wrap(a[1]), 0.01), Tidy(Wrap(a[2]), 0.01) };
            a1 = Norm(a1); a2 = Norm(a2);
            double Dist(double[] a)
            {
                if (prefer == null) return Math.Abs(a[0]) + Math.Abs(a[1]) + Math.Abs(a[2]);
                double s = 0;
                for (int i = 0; i < 3; i++) s += Math.Abs(Wrap(a[i] - prefer[i]));
                return s;
            }
            rotationDeg = Dist(a2) < Dist(a1) - 1e-6 ? a2 : a1;
            return true;
        }

        /// <summary>Angle in (-180, 180].</summary>
        private static double Wrap(double deg)
        {
            deg %= 360;
            if (deg <= -180) deg += 360;
            if (deg > 180) deg -= 360;
            return deg;
        }

        /// <summary>Rounded to a step, without -0.</summary>
        private static double Tidy(double v, double step)
        {
            double r = Math.Round(v / step) * step;
            r = Math.Round(r, 6);
            return r == 0 ? 0 : r;
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        /// <summary>R = Rz · Ry · Rx (degrees): rotate about X first, then Y, then Z (all local).</summary>
        public static double[,] Rotation(double xDeg, double yDeg, double zDeg)
        {
            double a = xDeg * Math.PI / 180, b = yDeg * Math.PI / 180, c = zDeg * Math.PI / 180;
            double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cc = Math.Cos(c), sc = Math.Sin(c);
            return new[,]
            {
                { cc * cb, cc * sb * sa - sc * ca, cc * sb * ca + sc * sa },
                { sc * cb, sc * sb * sa + cc * ca, sc * sb * ca - cc * sa },
                { -sb,     cb * sa,                cb * ca },
            };
        }

        /// <summary>The frame's X axis flattened to plan (for families that always stand upright); Y when X is vertical.</summary>
        public static double[] PlanDirection(Frame f)
        {
            double len = Math.Sqrt(f.X[0] * f.X[0] + f.X[1] * f.X[1]);
            if (len > 1e-6) return new[] { f.X[0] / len, f.X[1] / len, 0 };
            len = Math.Sqrt(f.Y[0] * f.Y[0] + f.Y[1] * f.Y[1]);
            return len > 1e-6 ? new[] { f.Y[0] / len, f.Y[1] / len, 0 } : new[] { 1.0, 0, 0 };
        }

        /// <summary>Does the frame tilt away from upright (Z not vertical)? Upright-only families then can't follow it.</summary>
        public static bool IsTilted(Frame f, double toleranceDeg = 0.5) =>
            Math.Abs(f.Z[2]) < Math.Cos(toleranceDeg * Math.PI / 180);

        public const double AngleToleranceDeg = 1.0;

        /// <summary>Signed plan rotation (radians, counter-clockwise) taking a's plan direction to b's.</summary>
        public static double PlanAngleBetween(Frame a, Frame b)
        {
            var pa = PlanDirection(a);
            var pb = PlanDirection(b);
            return Math.Atan2(pa[0] * pb[1] - pa[1] * pb[0], pa[0] * pb[0] + pa[1] * pb[1]);
        }

        /// <summary>Frame as 12 numbers (origin, X, Y, Z) for storage.</summary>
        public static double[] Pack(Frame f) => f == null ? null : new[]
        {
            f.O[0], f.O[1], f.O[2], f.X[0], f.X[1], f.X[2], f.Y[0], f.Y[1], f.Y[2], f.Z[0], f.Z[1], f.Z[2],
        };

        public static Frame Unpack(double[] a) => a == null || a.Length < 12 ? null : new Frame
        {
            O = new[] { a[0], a[1], a[2] }, X = new[] { a[3], a[4], a[5] }, Y = new[] { a[6], a[7], a[8] }, Z = new[] { a[9], a[10], a[11] },
        };

        /// <summary>How far apart two frames are: horizontal and vertical distance of the origins (mm) and rotation (°, X or Z axis).</summary>
        public class FrameDelta
        {
            public double XyMm, ZMm, AngleDeg;
            public double TotalMm => Math.Sqrt(XyMm * XyMm + ZMm * ZMm);
        }

        public static FrameDelta Delta(Frame a, Frame b)
        {
            double dx = a.O[0] - b.O[0], dy = a.O[1] - b.O[1], dz = a.O[2] - b.O[2];
            return new FrameDelta
            {
                XyMm = Math.Sqrt(dx * dx + dy * dy) * MmPerFoot,
                ZMm = Math.Abs(dz) * MmPerFoot,
                AngleDeg = Math.Max(AngleDeg(a.X, b.X), AngleDeg(a.Z, b.Z)),
            };
        }

        public static bool Within(FrameDelta d, double xyTolMm, double zTolMm, double angleTolDeg = AngleToleranceDeg) =>
            d.XyMm <= xyTolMm && d.ZMm <= zTolMm && d.AngleDeg <= angleTolDeg;

        /// <summary>"320 mm (300 mm horizontally, 110 mm vertically), turned 15°".</summary>
        public static string Describe(FrameDelta d)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (d.TotalMm >= 0.5)
            {
                string s = $"{d.TotalMm:N0} mm";
                if (d.XyMm >= 0.5 && d.ZMm >= 0.5) s += $" ({d.XyMm:N0} mm horizontally, {d.ZMm:N0} mm vertically)";
                else if (d.ZMm >= 0.5) s += " vertically";
                parts.Add(s);
            }
            if (d.AngleDeg >= 0.05) parts.Add($"turned {d.AngleDeg:0.#}°");
            return parts.Count == 0 ? "no change" : string.Join(", ", parts);
        }

        private static double AngleDeg(double[] u, double[] v)
        {
            double lu = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]), lv = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (lu < 1e-12 || lv < 1e-12) return 0;
            double c = (u[0] * v[0] + u[1] * v[1] + u[2] * v[2]) / (lu * lv);
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
        }

        public static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[] Unit(double[] v)
        {
            double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return l < 1e-12 ? v : new[] { v[0] / l, v[1] / l, v[2] / l };
        }
    }
}
