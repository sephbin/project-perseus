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
