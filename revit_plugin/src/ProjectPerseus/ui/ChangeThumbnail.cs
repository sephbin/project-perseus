using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ProjectPerseus.federator;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// A cropped piece of a review view image (ui/ViewImageStore) around some changes, with their boxes drawn on top
    /// (solid = where the element is now, dashed = old position). Loads in the background; click = callback (zoom in Revit).
    /// </summary>
    internal static class ChangeThumbnail
    {
        internal class Mark
        {
            internal double[] Min, Max;   // host coordinates (feet)
            internal Brush Brush;
            internal bool Dashed;
        }

        private const double MinModelFeet = 4000 / 304.8;   // show at least 4 m around the changes
        private static readonly Brush Frame = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE6, 0xEE)));
        private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));

        /// <returns>null when the image doesn't cover the marks.</returns>
        internal static FrameworkElement Build(ViewImageFrame f, IList<Mark> marks, double width, double height, string tip, Action onClick)
        {
            marks = marks?.Where(m => m.Min != null && m.Max != null).ToList();
            if (f == null || marks == null || marks.Count == 0 || f.Width <= 0 || f.Height <= 0) return null;
            if (!marks.Any(m => f.Covers(m.Min, m.Max))) return null;

            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            var rects = new List<Rect>();
            foreach (var m in marks)
            {
                f.PixelRect(m.Min, m.Max, out double a, out double b, out double c, out double d);
                rects.Add(new Rect(new Point(a, b), new Point(c, d)));
                x0 = Math.Min(x0, a); y0 = Math.Min(y0, b); x1 = Math.Max(x1, c); y1 = Math.Max(y1, d);
            }
            f.Window(x0, y0, x1, y1, width / height, MinModelFeet, out double wx0, out double wy0, out double wx1, out double wy1);
            double sx = width / (wx1 - wx0), sy = height / (wy1 - wy0);

            var image = new Image { Width = width, Height = height, Stretch = Stretch.Fill };
            var canvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
            var loading = new TextBlock { Text = "Loading view image…", Foreground = Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                                          VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                double left = (r.X - wx0) * sx, top = (r.Y - wy0) * sy, w = r.Width * sx, h = r.Height * sy;
                // Tiny elements: at least 10 px so they can be seen.
                if (w < 10) { left -= (10 - w) / 2; w = 10; }
                if (h < 10) { top -= (10 - h) / 2; h = 10; }
                foreach (bool halo in new[] { true, false })
                {
                    var rect = new Rectangle
                    {
                        Width = w + 6, Height = h + 6, RadiusX = 3, RadiusY = 3,
                        Stroke = halo ? Brushes.White : marks[i].Brush, StrokeThickness = halo ? 4.5 : 2,
                    };
                    if (marks[i].Dashed && !halo) rect.StrokeDashArray = new DoubleCollection { 3, 2 };
                    Canvas.SetLeft(rect, left - 3);
                    Canvas.SetTop(rect, top - 3);
                    canvas.Children.Add(rect);
                }
            }
            var grid = new Grid { Width = width, Height = height, Background = Brushes.White, ClipToBounds = true };
            grid.Children.Add(loading);
            grid.Children.Add(image);
            grid.Children.Add(canvas);
            var caption = new TextBlock { Text = f.ViewName, FontSize = 10, Foreground = Muted, Background = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                                          Padding = new Thickness(4, 1, 4, 1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                                          IsHitTestVisible = false };
            grid.Children.Add(caption);
            var border = new Border { Child = grid, BorderBrush = Frame, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                                      ToolTip = tip, Cursor = onClick != null ? Cursors.Hand : null };
            if (onClick != null) border.MouseLeftButtonUp += (s, e) => { e.Handled = true; onClick(); };

            var dispatcher = image.Dispatcher;
            ViewImageStore.CropAsync(f, wx0, wy0, wx1, wy1, (int)Math.Round(width), (int)Math.Round(height)).ContinueWith(t =>
            {
                var bmp = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : null;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (bmp != null) { image.Source = bmp; loading.Visibility = Visibility.Collapsed; }
                    else loading.Text = "View image unavailable";
                }));
            });
            return border;
        }

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
