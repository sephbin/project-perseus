using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.markers
{
    /// <summary>
    /// The user's library of parameter marker sets, kept at %AppData%\ProjectPerseus\marker_sets.json (not per model:
    /// door hardware sets are reused across projects). Import/Export move sets between people as JSON files.
    /// All access is on the Revit main thread (panel + engine), so no locking.
    /// </summary>
    internal static class MarkerSetStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "marker_sets.json");

        private class FileModel
        {
            public int Version { get; set; } = 1;
            /// <summary>Master switch for all marker rings (the panel's "Show markers").</summary>
            public bool ShowMarkers { get; set; } = true;
            public bool ShowLegend { get; set; } = true;
            public List<MarkerSet> Sets { get; set; } = new List<MarkerSet>();
        }

        private static FileModel _data;

        /// <summary>Raised after any change (sets edited, added, removed, toggled).</summary>
        internal static event Action Changed;

        internal static List<MarkerSet> Sets { get { EnsureLoaded(); return _data.Sets; } }

        internal static bool ShowMarkers
        {
            get { EnsureLoaded(); return _data.ShowMarkers; }
            set { EnsureLoaded(); if (_data.ShowMarkers == value) return; _data.ShowMarkers = value; NotifyChanged(); }
        }

        internal static bool ShowLegend
        {
            get { EnsureLoaded(); return _data.ShowLegend; }
            set { EnsureLoaded(); if (_data.ShowLegend == value) return; _data.ShowLegend = value; NotifyChanged(); }
        }

        internal static bool AnyActive => ShowMarkers && Sets.Any(s => s.Enabled && s.Categories.Count > 0 && s.Entries.Any(e => e.Enabled));

        /// <summary>Call after editing a set in place: saves and tells the engine to re-evaluate.</summary>
        internal static void NotifyChanged()
        {
            Save();
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Warn($"[MarkerSetStore] change handler failed: {ex.Message}"); }
        }

        internal static void Add(MarkerSet set)
        {
            Sets.Add(set);
            NotifyChanged();
        }

        internal static void Remove(MarkerSet set)
        {
            Sets.Remove(set);
            NotifyChanged();
        }

        internal static void Export(string path, IEnumerable<MarkerSet> sets)
        {
            var model = new FileModel { Sets = sets.ToList() };
            File.WriteAllText(path, JsonConvert.SerializeObject(model, Formatting.Indented));
        }

        /// <summary>Adds the sets in a file (as new sets, so importing twice never overwrites). Returns how many.</summary>
        internal static int Import(string path)
        {
            var model = JsonConvert.DeserializeObject<FileModel>(File.ReadAllText(path));
            var sets = model?.Sets ?? new List<MarkerSet>();
            foreach (var s in sets)
            {
                Normalise(s);
                s.Id = Guid.NewGuid().ToString("N");
                Sets.Add(s);
            }
            if (sets.Count > 0) NotifyChanged();
            return sets.Count;
        }

        private static void EnsureLoaded()
        {
            if (_data != null) return;
            try
            {
                if (File.Exists(FilePath))
                    _data = JsonConvert.DeserializeObject<FileModel>(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                Log.Warn($"[MarkerSetStore] could not read {FilePath}: {ex.Message}");
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { /* keep going with an empty library */ }
            }
            if (_data == null) _data = new FileModel { Sets = { Example() } };
            if (_data.Sets == null) _data.Sets = new List<MarkerSet>();
            foreach (var s in _data.Sets) Normalise(s);
        }

        private static void Normalise(MarkerSet s)
        {
            if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N");
            if (s.Categories == null) s.Categories = new List<string>();
            if (s.Entries == null) s.Entries = new List<MarkerSet.Entry>();
            if (s.Filter == null) s.Filter = "";
            if (s.Parameter == null) s.Parameter = "";
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(_data, Formatting.Indented));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"[MarkerSetStore] save failed: {ex.Message}");
            }
        }

        // First-run example, disabled: shows the shape of a set. The parameter name is a placeholder.
        private static MarkerSet Example() => new MarkerSet
        {
            Name = "Example: door hardware",
            Enabled = false,
            Categories = { "OST_Doors" },
            Parameter = "Door Hardware",
            Entries =
            {
                new MarkerSet.Entry { Label = "Mag lock",  Match = MarkerSet.MatchKind.Contains, Value = "mag",  Color = "#E6007E", Icon = "lock" },
                new MarkerSet.Entry { Label = "Drop bolt", Match = MarkerSet.MatchKind.Contains, Value = "drop", Color = "#00A0E0", Icon = "dropbolt" },
            },
        };
    }
}
