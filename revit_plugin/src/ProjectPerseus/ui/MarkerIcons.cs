using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Icons for parameter markers: stroked glyphs on a 16x16 grid (round caps/joins), drawn white on a coloured
    /// disc on the ring (ViolationOverlay) and in the marker panel/legend. Keys are stored in marker sets, so never
    /// rename one; add new ones instead.
    /// </summary>
    internal static class MarkerIcons
    {
        internal class Icon
        {
            internal string Key, Name;
            internal Geometry Geometry;
        }

        private static readonly List<Icon> _all = new List<Icon>();
        private static readonly Dictionary<string, Icon> _byKey = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        static MarkerIcons()
        {
            Add("lock",        "Lock / mag lock",  "M4 7.5h8v6H4z M5.6 7.5V5.6a2.4 2.4 0 0 1 4.8 0V7.5 M8 10v1.5");
            Add("unlock",      "Unlocked",         "M4 7.5h8v6H4z M5.6 7.5V5.6a2.4 2.4 0 0 1 4.7-.8");
            Add("dropbolt",    "Drop bolt",        "M8 2v8.5 M4.8 7.3L8 10.5l3.2-3.2 M3.5 13.5h9");
            Add("bolt",        "Electric / power", "M9.2 1.8L4.2 9h3.6l-1 5.2 5-7.2H8.2z");
            Add("key",         "Key",              "M5.5 5a3 3 0 1 1 0 6a3 3 0 1 1 0-6z M8.5 8h5.5 M12 8v2.5 M14 8v2");
            Add("card",        "Card reader",      "M2.5 4.5h11v7h-11z M2.5 7h11 M4.5 9.5h3");
            Add("bell",        "Alarm",            "M4 11V7.5a4 4 0 0 1 8 0V11l1.2 1.5H2.8z M6.5 14.2h3");
            Add("flame",       "Fire",             "M8 1.8c.6 2.6 4 4 4 8a4 4 0 0 1-8 0c0-1.9.9-3 1.8-3.8c0 1.6.7 2.6 1.7 2.8c.2-2.3-.6-4.4.5-7z");
            Add("shield",      "Security",         "M8 2l5 2v4c0 3-2 5-5 6c-3-1-5-3-5-6V4z");
            Add("person",      "Person / access",  "M8 2a1.9 1.9 0 1 1 0 3.8a1.9 1.9 0 1 1 0-3.8z M4 14c0-3 1.8-5.6 4-5.6s4 2.6 4 5.6");
            Add("door",        "Door",             "M4 14V2.5h8V14 M2.5 14h11 M9.6 8.2v.6");
            Add("eye",         "Visible / check",  "M1.5 8S4 3.6 8 3.6 14.5 8 14.5 8 12 12.4 8 12.4 1.5 8 1.5 8z M8 6a2 2 0 1 1 0 4a2 2 0 1 1 0-4z");
            Add("star",        "Star",             "M8 2l1.8 3.8 4.2.5-3.1 2.9.8 4.1L8 11.3l-3.7 2 .8-4.1L2 6.3l4.2-.5z");
            Add("check",       "Tick",             "M3.5 8.5l3 3 6-7");
            Add("cross",       "Cross",            "M4 4l8 8 M12 4l-8 8");
            Add("exclamation", "Attention",        "M8 3v6.5 M8 12.6v.2");
            Add("question",    "Unknown",          "M5.8 5.6a2.3 2.3 0 1 1 3.4 2c-.8.5-1.2 1-1.2 2 M8 12.6v.2");
            Add("power",       "Power",            "M8 2.2v5.5 M4.9 4.6a4.8 4.8 0 1 0 6.2 0");
        }

        private static void Add(string key, string name, string path)
        {
            var g = Geometry.Parse(path);
            g.Freeze();
            var icon = new Icon { Key = key, Name = name, Geometry = g };
            _all.Add(icon);
            _byKey[key] = icon;
        }

        internal static IReadOnlyList<Icon> All => _all;

        internal static Geometry Get(string key) =>
            !string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var i) ? i.Geometry : null;

        /// <summary>A white glyph on a disc of the given colour, <paramref name="size"/> px across.</summary>
        internal static FrameworkElement Badge(string key, Color fill, double size)
        {
            var geometry = Get(key);
            var grid = new System.Windows.Controls.Grid { Width = size, Height = size, IsHitTestVisible = false };
            grid.Children.Add(new Ellipse
            {
                Fill = Frozen(fill),
                Stroke = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            });
            if (geometry != null)
            {
                grid.Children.Add(new Path
                {
                    Data = geometry,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    // 16-unit glyph scaled into the inner ~70% of the disc
                    RenderTransform = new MatrixTransform(size * 0.7 / 16, 0, 0, size * 0.7 / 16, size * 0.15, size * 0.15),
                });
            }
            return grid;
        }

        /// <summary>The glyph alone in the given colour (for lists), or an empty box for "no icon".</summary>
        internal static FrameworkElement Glyph(string key, Color stroke, double size)
        {
            var geometry = Get(key);
            if (geometry == null) return new System.Windows.Controls.Border { Width = size, Height = size };
            return new Path
            {
                Data = geometry,
                Stroke = Frozen(stroke),
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = size,
                Height = size,
                Stretch = Stretch.None,
                RenderTransform = new ScaleTransform(size / 16, size / 16),
                IsHitTestVisible = false,
            };
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
