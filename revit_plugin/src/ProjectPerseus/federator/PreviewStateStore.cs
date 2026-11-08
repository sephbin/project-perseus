using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ProjectPerseus.logging;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Which changes the user has switched off in the panel's Preview column, per comparison, at
    /// %AppData%\ProjectPerseus\federator\preview_hidden.json. Per user on purpose: it's a viewing preference, not
    /// review data. A comparison never opened before starts with DefaultHiddenCategories off.
    /// </summary>
    internal static class PreviewStateStore
    {
        private const int MaxComparisons = 50;

        /// <summary>Non-physical categories that are rarely worth seeing in a coordination review.</summary>
        internal static readonly HashSet<string> DefaultHiddenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Project Base Point", "Survey Point", "Internal Origin", "Sheets", "Views", "Viewports", "Cameras", "Scope Boxes",
        };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "federator", "preview_hidden.json");

        private class FileModel
        {
            public Dictionary<string, Entry> Comparisons { get; set; } = new Dictionary<string, Entry>();
        }

        private class Entry
        {
            public DateTime SavedUtc { get; set; }
            public List<string> Hidden { get; set; } = new List<string>();
        }

        /// <summary>Hidden change UniqueIds for the comparison, or null if it was never saved.</summary>
        internal static HashSet<string> Load(string comparisonId)
        {
            if (string.IsNullOrEmpty(comparisonId)) return null;
            var model = Read();
            return model.Comparisons.TryGetValue(comparisonId, out var e) ? new HashSet<string>(e.Hidden) : null;
        }

        internal static void Save(string comparisonId, IEnumerable<string> hidden)
        {
            if (string.IsNullOrEmpty(comparisonId)) return;
            try
            {
                var model = Read();
                model.Comparisons[comparisonId] = new Entry { SavedUtc = DateTime.UtcNow, Hidden = hidden.ToList() };
                foreach (var old in model.Comparisons.OrderByDescending(kv => kv.Value.SavedUtc).Skip(MaxComparisons).Select(kv => kv.Key).ToList())
                    model.Comparisons.Remove(old);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(model));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Info($"[Federator] preview state not saved: {ex.Message}"); }
        }

        private static FileModel Read()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonConvert.DeserializeObject<FileModel>(File.ReadAllText(FilePath)) ?? new FileModel();
            }
            catch (Exception ex) { Log.Info($"[Federator] preview state unreadable: {ex.Message}"); }
            return new FileModel();
        }
    }
}
