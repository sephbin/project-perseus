using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectPerseus.logging;

namespace ProjectPerseus.config
{
    /// <summary>
    /// Test switch for finding which part of Perseus is involved in a crash: names listed one per line in
    /// %AppData%\ProjectPerseus\disabled_features.txt are not started (read once at Revit start-up; '#' starts a
    /// comment). No file = everything on. Names: violations, ids, highlights, ownership, markers, federator,
    /// keyschedules. Added 2026-11-25 for the door-flip (FlipFromToRoom) crashes.
    /// </summary>
    internal static class FeatureSwitches
    {
        // "parent.child" names switch off one part; listing the parent switches off all its parts.
        internal static readonly string[] Known =
            { "violations", "violations.changed", "violations.failures", "violations.links", "violations.cache",
              "violations.poller", "ids", "highlights", "ownership", "markers", "federator", "keyschedules" };

        private static HashSet<string> _off;

        internal static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "disabled_features.txt");

        internal static bool IsOff(string name)
        {
            if (_off == null) Load();
            if (_off.Contains(name)) return true;
            int dot = name.IndexOf('.');
            return dot > 0 && _off.Contains(name.Substring(0, dot));
        }

        private static void Load()
        {
            _off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Split('#')[0].Trim();
                    if (line.Length > 0) _off.Add(line);
                }
                var unknown = _off.Where(n => !Known.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                Log.Warn($"[FeatureSwitches] TEST MODE - disabled by {FilePath}: {string.Join(", ", _off)}" +
                         (unknown.Count > 0 ? $" (unknown names ignored: {string.Join(", ", unknown)})" : ""));
            }
            catch (Exception ex) { Log.Warn($"[FeatureSwitches] could not read {FilePath}: {ex.Message}"); }
        }
    }
}
