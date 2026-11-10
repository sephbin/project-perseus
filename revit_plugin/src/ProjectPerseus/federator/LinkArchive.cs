using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Automatic version history of linked models, without Autodesk's web APIs. Whenever a link shows a save GUID
    /// (DocumentVersion) not archived yet, Perseus copies the .rvt Revit loaded it from into the archive: for a cloud
    /// link Revit's own local copy (CollaborationCache\&lt;account&gt;\&lt;project GUID&gt;\LinkedModels\&lt;model GUID&gt;.rvt,
    /// overwritten on the next Reload Latest), for a file link its file. The copy is checked to be that exact save
    /// (BasicFileInfo) and runs on a background thread: seconds, no snapshot. Archived versions appear in the panel's
    /// From / To pickers; comparing one opens the copy (no download), after which its snapshot is cached (✓).
    /// Layout: &lt;root&gt;\&lt;project&gt;_&lt;hash&gt;\&lt;link title&gt;_&lt;hash of model key&gt;\&lt;saves&gt;_&lt;guid&gt;.rvt + .json. One Perseus root
    /// for every project (a shared network folder gives the team one history; no shared index, anyone's Revit adds what it
    /// loads); the project subfolder is made automatically from the host model (Project Number + Name, hashed with its
    /// cloud project GUID / central folder, so renaming the project doesn't split it). Keeps the newest KeepPerLink per link.
    /// Root: this user's setting, else the machine-wide one IT can deploy (%ProgramData%\ProjectPerseus\federator\
    /// archive.json), else %LOCALAPPDATA%. Replaceable by / complementary to APS cloud history (aps/ApsClient).
    /// </summary>
    internal static class LinkArchive
    {
        internal class Settings
        {
            public bool Enabled { get; set; } = true;
            /// <summary>Perseus archive root for all projects; empty = machine-wide setting, else %LOCALAPPDATA%.</summary>
            public string Folder { get; set; }
            public int KeepPerLink { get; set; } = 10;

            /// <summary>Root actually used (after the machine-wide default).</summary>
            [JsonIgnore] internal string Root => !string.IsNullOrWhiteSpace(Folder) ? Folder : !string.IsNullOrWhiteSpace(MachineFolder) ? MachineFolder : DefaultFolder;
            [JsonIgnore] internal string MachineFolder { get; set; }
        }

        // Project subfolder per host document (cheap to recompute, but Idling asks often).
        private static readonly ConcurrentDictionary<string, string> ProjectFolders = new ConcurrentDictionary<string, string>();

        /// <summary>This host model's archive folder: &lt;root&gt;\&lt;project&gt;_&lt;hash&gt;. API context.</summary>
        internal static string RootFor(Document host, Settings settings = null)
        {
            string root = (settings ?? LoadSettings()).Root;
            if (host == null) return root;
            // Cached for 5 minutes: listing a slow / unreachable share on every idle check would stall Revit.
            string cacheKey = root + "|" + DocKey(host);
            if (Resolved.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.Key < TimeSpan.FromMinutes(5)) return hit.Value;
            string resolved = Resolve(host, root);
            Resolved[cacheKey] = new KeyValuePair<DateTime, string>(DateTime.UtcNow, resolved);
            return resolved;
        }

        private static readonly ConcurrentDictionary<string, KeyValuePair<DateTime, string>> Resolved =
            new ConcurrentDictionary<string, KeyValuePair<DateTime, string>>();

        private static string Resolve(Document host, string root)
        {
            string name = ProjectFolders.GetOrAdd(DocKey(host), _ => ProjectFolderName(host));
            // Reuse an existing folder with the same identity (the project may have been renamed since).
            string suffix = name.Substring(name.LastIndexOf('_'));
            try
            {
                if (Directory.Exists(root))
                {
                    var existing = Directory.GetDirectories(root).FirstOrDefault(d => d.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) return existing;
                }
            }
            catch { /* unreachable share: use the computed path */ }
            return Path.Combine(root, name);
        }

        // "<Project Number> <Project Name>_<hash of project identity>".
        private static string ProjectFolderName(Document host)
        {
            string label = null, identity;
            try
            {
                var pi = host.ProjectInformation;
                label = string.Join(" ", new[] { pi?.Number, pi?.Name }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "Project Number" && x != "Project Name"));
            }
            catch { /* no project info */ }
            if (string.IsNullOrWhiteSpace(label)) label = host.Title;
            if (VersionCatalog.TryCloudGuids(host, out Guid project, out _) && project != Guid.Empty) identity = "cloud|" + project.ToString("N");
            else
            {
                string path = DocKey(host);
                try { identity = "folder|" + (Path.GetDirectoryName(path) ?? path).ToLowerInvariant(); }
                catch { identity = "title|" + host.Title.ToLowerInvariant(); }
            }
            return Safe(label) + "_" + Hash(identity);
        }

        private static string DocKey(Document doc)
        {
            try { return doc.IsWorkshared && doc.GetWorksharingCentralModelPath() != null
                    ? ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()) : doc.PathName + "|" + doc.Title; }
            catch { return doc.Title; }
        }

        internal class Entry
        {
            public string VersionGuid { get; set; }
            public int Saves { get; set; }
            public string Title { get; set; }
            public string ModelKey { get; set; }
            public string SourcePath { get; set; }     // the cache / file it was copied from
            public bool FromCloud { get; set; }
            public DateTime SourceTimeUtc { get; set; } // last write time of the source = when Revit got that version
            public DateTime ArchivedUtc { get; set; }
            public string ArchivedBy { get; set; }
            public long Bytes { get; set; }
            [JsonIgnore] public string RvtPath { get; set; }
        }

        internal static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectPerseus", "federator", "archive");
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "federator", "archive.json");
        /// <summary>Machine-wide defaults IT can deploy (same format; only Folder / KeepPerLink / Enabled are read).</summary>
        internal static string MachineSettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProjectPerseus", "federator", "archive.json");

        // Save GUIDs already archived or being copied (this session), so Idling checks stay cheap.
        private static readonly ConcurrentDictionary<string, bool> Known = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised (thread pool) after a version was archived; the panel refreshes its pickers.</summary>
        internal static event Action<string> Archived;

        internal static Settings LoadSettings()
        {
            Settings machine = Read(MachineSettingsPath), user = Read(SettingsPath);
            var s = user ?? machine ?? new Settings();
            s.MachineFolder = machine?.Folder;
            if (user == null && machine != null) s.Folder = null;   // machine root shows as the default, not the user's own
            return s;
        }

        private static Settings Read(string path)
        {
            try { return File.Exists(path) ? JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path)) : null; }
            catch (Exception ex) { Log.Info($"[Federator] {path} unreadable: {ex.Message}"); return null; }
        }

        internal static void SaveSettings(Settings s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(s, Formatting.Indented), new UTF8Encoding(false));
            Known.Clear();
            Resolved.Clear();
        }

        /// <summary>Archived versions of a model in an archive folder (RootFor the host), newest first.</summary>
        internal static List<Entry> List(string root, string modelKey)
        {
            var list = new List<Entry>();
            try
            {
                string dir = FindModelFolder(root, modelKey);
                if (dir == null) return list;
                foreach (var json in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var e = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(json));
                        e.RvtPath = Path.ChangeExtension(json, ".rvt");
                        if (File.Exists(e.RvtPath)) list.Add(e);
                    }
                    catch { /* half-written by someone else: skip */ }
                }
            }
            catch (Exception ex) { Log.Info($"[Federator] archive list: {ex.Message}"); }
            return list.OrderByDescending(e => e.Saves).ThenByDescending(e => e.SourceTimeUtc).ToList();
        }

        /// <summary>
        /// Archives the loaded version of a link when it isn't archived yet. Cheap when it is (in-memory set); the copy
        /// runs on a background thread. API context (reads the link document and Revit's cache path).
        /// </summary>
        internal static void Capture(Autodesk.Revit.ApplicationServices.Application app, Document host, RevitLinkInstance instance)
        {
            var s = LoadSettings();
            if (!s.Enabled) return;
            Document linkDoc;
            try { linkDoc = instance.GetLinkDocument(); } catch { return; }
            if (linkDoc == null) return;
            DocumentVersion dv;
            try { dv = Document.GetDocumentVersion(linkDoc); } catch { return; }
            if (dv == null) return;
            string guid = dv.VersionGUID.ToString();
            string knownKey = DocKey(host) + "|" + guid;   // per host model: each project has its own subfolder
            if (Known.ContainsKey(knownKey)) return;

            string typeName = host.GetElement(instance.GetTypeId())?.Name ?? linkDoc.Title;
            string modelKey = VersionCatalog.ModelKey(host, instance, linkDoc, typeName);
            string root = RootFor(host, s);
            string modelDir = ModelFolder(root, modelKey, linkDoc.Title);
            string stem = $"{dv.NumberOfSaves:D6}_{dv.VersionGUID:N}";
            if (File.Exists(Path.Combine(modelDir, stem + ".json"))) { Known[knownKey] = true; return; }

            bool cloud = linkDoc.IsModelInCloud;
            string source = cloud ? CloudCacheFile(app, linkDoc) : linkDoc.PathName;
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                Known[knownKey] = true;   // nothing to copy from; don't retry every idle tick
                Log.Info($"[Federator] archive: no local file for {linkDoc.Title} (save {dv.NumberOfSaves})");
                return;
            }
            Known[knownKey] = true;
            string user = app.Username, title = linkDoc.Title;
            int saves = dv.NumberOfSaves, keep = Math.Max(1, s.KeepPerLink);

            // The cache can already hold a newer version than the one loaded: only archive that exact save.
            // (Revit API: checked here in the API context, from the file header; only the copy runs in the background.)
            try
            {
                var fileVersion = BasicFileInfo.Extract(source)?.GetDocumentVersion();
                if (fileVersion == null || !string.Equals(fileVersion.VersionGUID.ToString(), guid, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"[Federator] archive: {Path.GetFileName(source)} isn't save {saves} of {title}; skipped");
                    return;
                }
            }
            catch (Exception ex)
            {
                Known.TryRemove(knownKey, out _);
                Log.Info($"[Federator] archive: couldn't read {source}: {ex.Message}");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(modelDir);
                    string rvt = Path.Combine(modelDir, stem + ".rvt"), tmp = rvt + ".part";
                    var started = DateTime.UtcNow;
                    using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                        src.CopyTo(dst, 1 << 20);
                    if (File.Exists(rvt)) File.Delete(rvt);
                    File.Move(tmp, rvt);
                    var entry = new Entry
                    {
                        VersionGuid = guid, Saves = saves, Title = title, ModelKey = modelKey, SourcePath = source, FromCloud = cloud,
                        SourceTimeUtc = File.GetLastWriteTimeUtc(source), ArchivedUtc = DateTime.UtcNow, ArchivedBy = user,
                        Bytes = new FileInfo(rvt).Length,
                    };
                    File.WriteAllText(Path.Combine(modelDir, stem + ".json"), JsonConvert.SerializeObject(entry, Formatting.Indented), new UTF8Encoding(false));
                    Log.Info($"[Federator] archived {title} save {saves} ({entry.Bytes / (1024 * 1024):N0} MB) in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
                    Prune(modelDir, keep);
                    Archived?.Invoke(modelKey);
                }
                catch (Exception ex)
                {
                    Known.TryRemove(knownKey, out _);   // try again later
                    Log.Warn($"[Federator] archiving {title} failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Revit's local copy of a loaded cloud link: %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit &lt;ver&gt;\CollaborationCache\
        /// &lt;account&gt;\&lt;project GUID&gt;\LinkedModels\&lt;model GUID&gt;.rvt (any account folder). Null when not found.
        /// </summary>
        internal static string CloudCacheFile(Autodesk.Revit.ApplicationServices.Application app, Document linkDoc)
        {
            if (!VersionCatalog.TryCloudGuids(linkDoc, out Guid project, out Guid model)) return null;
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "Autodesk", "Revit", "Autodesk Revit " + app.VersionNumber, "CollaborationCache");
                if (!Directory.Exists(root)) return null;
                string name = model.ToString("D") + ".rvt";
                var candidates = new List<string>();
                foreach (var account in Directory.GetDirectories(root))
                {
                    string projectDir = Path.Combine(account, project.ToString("D"));
                    if (!Directory.Exists(projectDir)) continue;
                    // Linked into this project, or opened / linked as its own model.
                    candidates.Add(Path.Combine(projectDir, "LinkedModels", name));
                    candidates.Add(Path.Combine(projectDir, name));
                    candidates.Add(Path.Combine(projectDir, "CentralCache", name));
                    candidates.Add(Path.Combine(projectDir, "LinkedModels", "CentralCache", name));
                }
                // A link from another project sits under the HOST's project folder: search LinkedModels everywhere.
                foreach (var linked in Directory.GetDirectories(root, "LinkedModels", SearchOption.AllDirectories))
                    candidates.Add(Path.Combine(linked, name));
                return candidates.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] cloud cache lookup for {linkDoc.Title}: {ex.Message}");
                return null;
            }
        }

        private static void Prune(string modelDir, int keep)
        {
            var old = Directory.GetFiles(modelDir, "*.json").Select(j => new { Json = j, Name = Path.GetFileNameWithoutExtension(j) })
                .OrderByDescending(x => x.Name, StringComparer.Ordinal)   // "<saves:D6>_…": newest first
                .Skip(keep);
            foreach (var x in old)
            {
                try
                {
                    File.Delete(Path.ChangeExtension(x.Json, ".rvt"));
                    File.Delete(x.Json);
                    Log.Info($"[Federator] archive: removed old version {x.Name}");
                }
                catch { /* in use: next time */ }
            }
        }

        private static string ModelFolder(string root, string modelKey, string title) =>
            FindModelFolder(root, modelKey) ?? Path.Combine(root, Safe(title) + "_" + Hash(modelKey));

        private static string FindModelFolder(string root, string modelKey)
        {
            if (!Directory.Exists(root)) return null;
            string suffix = "_" + Hash(modelKey);
            return Directory.GetDirectories(root).FirstOrDefault(d => d.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")).Take(5).Select(b => b.ToString("x2")));
        }

        private static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            string t = string.Concat((s ?? "model").Select(c => bad.Contains(c) ? '_' : c));
            return t.Length > 60 ? t.Substring(0, 60) : t;
        }
    }
}
