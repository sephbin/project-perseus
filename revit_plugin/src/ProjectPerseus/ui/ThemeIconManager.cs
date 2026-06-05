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
                var icon = LoadIcon(entry.BaseName, isDark);
                if (icon != null)
                {
                    entry.Button.LargeImage = icon;
                }
            }
        }

        private static BitmapImage LoadIcon(string baseName, bool isDark)
        {
            if (isDark)
            {
                var darkImg = TryLoad(BuildUri(baseName + "_dark"));
                if (darkImg != null) return darkImg;
                Log.Info($"ThemeIconManager: dark variant for '{baseName}' not found, falling back to base icon.");
            }
            return TryLoad(BuildUri(baseName));
        }

        private static Uri BuildUri(string name)
        {
            return new Uri($"pack://application:,,,/ProjectPerseus;component/resources/{name}.png");
        }

        private static BitmapImage TryLoad(Uri uri)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = uri;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                return img;
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
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = uri;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                button.LargeImage = img;
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
