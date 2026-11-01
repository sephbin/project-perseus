using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

using ProjectPerseus.logging;
namespace ProjectPerseus.ui
{
    internal static class ThemeIconManager
    {
        // Revit does not scale ribbon images: it shows them at their natural size in device-independent
        // pixels and crops anything larger than 32x32 (large) / 16x16 (small). The source PNGs carry no
        // DPI, so a 64px decode would read as 64 DIP and be cropped. Stamping 192 DPI makes 64px read as
        // 32 DIP (and 32px as 16 DIP) while keeping 2x pixels for crisp high-DPI rendering.
        private const double RibbonImageDpi = 192.0;

        private static BitmapSource DecodeAtDpi(Uri uri, int decodeWidth)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = decodeWidth;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();

            int stride = (img.PixelWidth * img.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * img.PixelHeight];
            img.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(img.PixelWidth, img.PixelHeight, RibbonImageDpi, RibbonImageDpi,
                img.Format, img.Palette, pixels, stride);
            result.Freeze();
            return result;
        }

#if REVIT2024_OR_GREATER
        private class IconEntry
        {
            public PushButton Button;
            public string BaseName;
        }

        private static readonly List<IconEntry> _entries = new List<IconEntry>();
        private static bool _subscribed;

        public static void Register(PushButton button, string baseName)
        {
            if (button == null || string.IsNullOrEmpty(baseName)) return;
            _entries.Add(new IconEntry { Button = button, BaseName = baseName });
        }

        public static void Initialize(UIControlledApplication application)
        {
            ApplyTheme();
            if (!_subscribed && application != null)
            {
                application.ThemeChanged += OnThemeChanged;
                _subscribed = true;
            }
        }

        public static void Shutdown(UIControlledApplication application)
        {
            if (_subscribed && application != null)
            {
                application.ThemeChanged -= OnThemeChanged;
                _subscribed = false;
            }
        }

        private static void OnThemeChanged(object sender, Autodesk.Revit.UI.Events.ThemeChangedEventArgs e)
        {
            ApplyTheme();
        }

        private static void ApplyTheme()
        {
            bool isDark = UIThemeManager.CurrentTheme == UITheme.Dark;
            foreach (var entry in _entries)
            {
                var icon = LoadIcon(entry.BaseName, isDark, LargeDecodeWidth);
                if (icon != null)
                {
                    entry.Button.LargeImage = icon;
                    // Stacked (small) buttons only show Image; decode a separate 32px copy so Revit scales crisply.
                    entry.Button.Image = LoadIcon(entry.BaseName, isDark, SmallDecodeWidth) ?? icon;
                }
            }
        }

        // Source PNGs are 128px. Decoding at 64/32px gives high-quality downsampling instead of
        // WPF's default bilinear scaling, and still leaves headroom for 200% display scaling.
        private const int LargeDecodeWidth = 64;
        private const int SmallDecodeWidth = 32;

        private static BitmapSource LoadIcon(string baseName, bool isDark, int decodeWidth)
        {
            if (isDark)
            {
                var darkImg = TryLoad(BuildUri(baseName + "_dark"), decodeWidth);
                if (darkImg != null) return darkImg;
                Log.Info($"ThemeIconManager: dark variant for '{baseName}' not found, falling back to base icon.");
            }
            return TryLoad(BuildUri(baseName), decodeWidth);
        }

        private static Uri BuildUri(string name)
        {
            return new Uri($"pack://application:,,,/ProjectPerseus;component/resources/{name}.png");
        }

        private static BitmapSource TryLoad(Uri uri, int decodeWidth)
        {
            try
            {
                return DecodeAtDpi(uri, decodeWidth);
            }
            catch (IOException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Info($"ThemeIconManager: failed to load icon {uri}: {ex.Message}");
                return null;
            }
        }
#else
        // Revit 2022/2023 have no UIThemeManager / ThemeChangedEventArgs — keep call sites
        // identical by setting the light icon directly with no theme-change subscription.
        public static void Register(PushButton button, string baseName)
        {
            if (button == null || string.IsNullOrEmpty(baseName)) return;
            try
            {
                var uri = new Uri($"pack://application:,,,/ProjectPerseus;component/resources/{baseName}.png");
                button.LargeImage = DecodeAtDpi(uri, 64);
                button.Image = DecodeAtDpi(uri, 32);
            }
            catch (Exception ex)
            {
                Log.Info($"ThemeIconManager (2023): failed to set icon for '{baseName}': {ex.Message}");
            }
        }

        public static void Initialize(UIControlledApplication application) { }
        public static void Shutdown(UIControlledApplication application) { }
#endif
    }
}
