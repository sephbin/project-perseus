using System;
using System.Collections.Generic;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// A captured image of a review view (federator/ViewImageCapture) and how host model points land on it: the view's
    /// crop frame (origin + right / up axes, host feet), the model rectangle the image covers in that frame, and the
    /// image size in pixels. Stored as frame.json next to the image tiles (ui/ViewImageStore). Pure: unit-tested in
    /// ProjectPerseusTests/ReplacementGroupsTest.cs.
    /// </summary>
    public class ViewImageFrame
    {
        public string ViewUid { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public DateTime CapturedUtc { get; set; }
        /// <summary>Link instance UniqueId -> save GUID loaded when the image was made (stale when it changed).</summary>
        public Dictionary<string, string> LinkVersions { get; set; } = new Dictionary<string, string>();
        /// <summary>The source view's crop (when cropped) as text, to notice a changed crop region.</summary>
        public string CropSignature { get; set; }

        public double[] Origin { get; set; } = { 0, 0, 0 };
        public double[] Right { get; set; } = { 1, 0, 0 };
        public double[] Up { get; set; } = { 0, 1, 0 };
        /// <summary>Model rectangle covered by the image, in frame coordinates (feet).</summary>
        public double U0 { get; set; }
        public double V0 { get; set; }
        public double U1 { get; set; }
        public double V1 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int TileSize { get; set; } = 512;
        /// <summary>Folder holding the tiles (t_col_row.png); set when loaded, not stored.</summary>
        [Newtonsoft.Json.JsonIgnore] public string Folder { get; set; }

        public double PixelsPerFoot => U1 > U0 ? Width / (U1 - U0) : 0;

        public void ToFrame(double x, double y, double z, out double u, out double v)
        {
            double dx = x - Origin[0], dy = y - Origin[1], dz = z - Origin[2];
            u = dx * Right[0] + dy * Right[1] + dz * Right[2];
            v = dx * Up[0] + dy * Up[1] + dz * Up[2];
        }

        public void ToPixel(double x, double y, double z, out double px, out double py)
        {
            ToFrame(x, y, z, out double u, out double v);
            px = (u - U0) / (U1 - U0) * Width;
            py = (V1 - v) / (V1 - V0) * Height;
        }

        /// <summary>Pixel rectangle around a host axis-aligned box (all 8 corners projected).</summary>
        public void PixelRect(double[] min, double[] max, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = y0 = double.MaxValue;
            x1 = y1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                ToPixel((i & 1) == 0 ? min[0] : max[0], (i & 2) == 0 ? min[1] : max[1], (i & 4) == 0 ? min[2] : max[2], out double px, out double py);
                x0 = Math.Min(x0, px); y0 = Math.Min(y0, py);
                x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
            }
        }

        /// <summary>Does the image cover (most of) this host box?</summary>
        public bool Covers(double[] min, double[] max)
        {
            PixelRect(min, max, out double x0, out double y0, out double x1, out double y1);
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            return cx >= 0 && cy >= 0 && cx <= Width && cy <= Height;
        }

        /// <summary>
        /// The pixel window for a thumbnail of aspect w:h around a pixel rectangle: padded (at least minModelFeet
        /// across), widened to the aspect, kept inside the image when it fits.
        /// </summary>
        public void Window(double x0, double y0, double x1, double y1, double aspect, double minModelFeet,
                           out double wx0, out double wy0, out double wx1, out double wy1)
        {
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            double w = Math.Max(x1 - x0, 1) * 1.8, h = Math.Max(y1 - y0, 1) * 1.8;
            double min = minModelFeet * PixelsPerFoot;
            w = Math.Max(w, min);
            h = Math.Max(h, min / Math.Max(aspect, 0.01));
            if (w / h < aspect) w = h * aspect; else h = w / aspect;
            wx0 = cx - w / 2; wx1 = cx + w / 2; wy0 = cy - h / 2; wy1 = cy + h / 2;
            Shift(ref wx0, ref wx1, Width);
            Shift(ref wy0, ref wy1, Height);
        }

        private static void Shift(ref double a, ref double b, double size)
        {
            double len = b - a;
            if (len >= size) return;   // larger than the image: keep centred (white margins)
            if (a < 0) { a = 0; b = len; }
            else if (b > size) { b = size; a = size - len; }
        }
    }
}
