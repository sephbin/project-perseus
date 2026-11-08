using System;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// A link instance's total transform as plain numbers (origin + basis), captured once in an API context so the
    /// panel can move change boxes into host coordinates without calling the Revit API. Pure.
    /// </summary>
    public class LinkTransform
    {
        public double[] Origin = { 0, 0, 0 };
        public double[] BasisX = { 1, 0, 0 };
        public double[] BasisY = { 0, 1, 0 };
        public double[] BasisZ = { 0, 0, 1 };

        public static readonly LinkTransform Identity = new LinkTransform();

        public double[] OfPoint(double x, double y, double z) => new[]
        {
            Origin[0] + x * BasisX[0] + y * BasisY[0] + z * BasisZ[0],
            Origin[1] + x * BasisX[1] + y * BasisY[1] + z * BasisZ[1],
            Origin[2] + x * BasisX[2] + y * BasisY[2] + z * BasisZ[2],
        };

        /// <summary>Axis-aligned host box around a link-coordinate box.</summary>
        public void Box(double[] min, double[] max, out double[] hmin, out double[] hmax)
        {
            hmin = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            hmax = new[] { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < 8; i++)
            {
                var p = OfPoint((i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2]);
                for (int k = 0; k < 3; k++)
                {
                    hmin[k] = Math.Min(hmin[k], p[k]);
                    hmax[k] = Math.Max(hmax[k], p[k]);
                }
            }
        }
    }
}
