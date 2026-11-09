using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Per-user cache of model snapshots at %AppData%\ProjectPerseus\federator\snapshots\ (gzip JSON), so a version
    /// that was compared once never has to be opened / downloaded again. Two kinds of key:
    /// FileKey = an archived file's full path + size + last write time; VersionKey = the model's save GUID
    /// (Document.GetDocumentVersion), used for snapshots of the loaded (live) link, whose file can't be stamped.
    /// federator/VersionCatalog lists which versions of a link are cached. Only a cache: comparison results live in
    /// the host model (ComparisonStore).
    /// </summary>
    internal static class SnapshotCache
    {
        private const int MaxFiles = 40;

        internal static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "federator", "snapshots");

        /// <summary>"size|lastWriteUtcTicks" of a local / UNC file; null when it isn't a readable file (e.g. cloud).</summary>
        internal static string StampOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var fi = new FileInfo(path);
                return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { return null; }
        }

        internal static string FileKey(string path, string stamp) => Path.GetFullPath(path).ToLowerInvariant() + "|" + stamp;
        internal static string VersionKey(string versionGuid) => "version|" + (versionGuid ?? "").ToLowerInvariant();

        /// <summary>Is a snapshot stored under this key (cheap: file exists)?</summary>
        internal static bool Has(string key) => !string.IsNullOrEmpty(key) && File.Exists(FileFor(key));

        /// <summary>Snapshot of an archived file, if that exact file (path, size, time) was read before.</summary>
        internal static LinkSnapshot TryLoad(string sourcePath)
        {
            string stamp = StampOf(sourcePath);
            return stamp == null ? null : TryLoadKey(FileKey(sourcePath, stamp), stamp);
        }

        internal static LinkSnapshot TryLoadKey(string key, string expectStamp = null)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string file = FileFor(key);
            if (!File.Exists(file)) return null;
            try
            {
                using (var fs = File.OpenRead(file))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var sr = new StreamReader(gz, Encoding.UTF8))
                using (var jr = new JsonTextReader(sr))
                {
                    var snap = new JsonSerializer().Deserialize<LinkSnapshot>(jr);
                    if (snap == null || snap.Format != LinkSnapshot.CurrentFormat) return null;
                    if (expectStamp != null && snap.FileStamp != expectStamp) return null;
                    fs.Close();
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // most recently used survives pruning
                    return snap;
                }
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] cached snapshot unreadable, ignoring ({file}): {ex.Message}");
                return null;
            }
        }

        /// <summary>Caches a snapshot of an archived file under its FileKey; returns the key (null when not cached).</summary>
        internal static string Save(LinkSnapshot snap)
        {
            if (snap?.FileStamp == null || string.IsNullOrEmpty(snap.SourcePath)) return null;
            string key = FileKey(snap.SourcePath, snap.FileStamp);
            return SaveKey(snap, key) ? key : null;
        }

        internal static bool SaveKey(LinkSnapshot snap, string key)
        {
            if (snap == null || string.IsNullOrEmpty(key)) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                string file = FileFor(key);
                string tmp = file + ".tmp";
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    new JsonSerializer { NullValueHandling = NullValueHandling.Ignore }.Serialize(sw, snap);
                }
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
                Prune();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Federator] could not cache snapshot of {snap.SourcePath}: {ex.Message}");
                return false;
            }
        }

        private static void Prune()
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.json.gz")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFiles);
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* in use: next time */ }
            }
        }

        private static string FileFor(string key)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(Folder, string.Concat(hash.Select(b => b.ToString("x2"))) + ".json.gz");
            }
        }
    }
}
