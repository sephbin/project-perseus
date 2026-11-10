using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Per-user list of the versions of each consultant model whose snapshot is cached (models/LinkVersion), at
    /// %AppData%\ProjectPerseus\federator\versions\&lt;sha1(model key)&gt;.json. Feeds the panel's From / To version
    /// pickers (✓ = cached, no need to open / download it again). Entries whose snapshot was pruned from
    /// SnapshotCache drop out. Model key = cloud model GUID, else the link's file path, else its name.
    /// Stage 2 (FEDERATOR_PLAN.md): cloud version history from APS adds versions that aren't cached yet.
    /// </summary>
    internal static class VersionCatalog
    {
        private static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "federator", "versions");

        /// <summary>Identity of the consultant model behind a link instance, the same across its versions. API context.</summary>
        /// <param name="linkDoc">The loaded link document (null when unloaded).</param>
        internal static string ModelKey(Document host, RevitLinkInstance instance, Document linkDoc, string fallbackName)
        {
            // Loaded: from the document itself (cloud links are external resources, not external file references).
            if (TryCloudGuids(linkDoc, out _, out Guid model)) return "cloud|" + model.ToString("N");
            try
            {
                if (linkDoc != null && !linkDoc.IsModelInCloud && !string.IsNullOrEmpty(linkDoc.PathName))
                    return "file|" + Path.GetFullPath(linkDoc.PathName).ToLowerInvariant();
            }
            catch { /* odd path */ }
            // Unloaded file link: its saved path.
            try
            {
                var typeId = instance.GetTypeId();
                if (ExternalFileUtils.IsExternalFileReference(host, typeId))
                {
                    var mp = ExternalFileUtils.GetExternalFileReference(host, typeId)?.GetAbsolutePath();
                    if (mp != null && mp.CloudPath) return "cloud|" + mp.GetModelGUID().ToString("N");
                    string p = mp == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                    if (!string.IsNullOrEmpty(p)) return "file|" + Path.GetFullPath(p).ToLowerInvariant();
                }
            }
            catch (Exception ex) { Log.Info($"[Federator] link model key: {ex.Message}"); }
            return "name|" + (fallbackName ?? "").ToLowerInvariant();
        }

        /// <summary>Cloud project + model GUIDs of a loaded cloud model, read locally (no cloud lookup).</summary>
        internal static bool TryCloudGuids(Document linkDoc, out Guid project, out Guid model)
        {
            project = model = Guid.Empty;
            try
            {
                if (linkDoc == null || !linkDoc.IsModelInCloud) return false;
                var mp = linkDoc.GetCloudModelPath();
                if (mp == null || !mp.CloudPath) return false;
                project = mp.GetProjectGUID();
                model = mp.GetModelGUID();
                return model != Guid.Empty;
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] cloud path of {linkDoc?.Title}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Cached versions, newest (most saves, then latest) first.</summary>
        internal static List<LinkVersion> List(string modelKey)
        {
            return Read(modelKey).Where(v => SnapshotCache.Has(v.CacheKey))
                .OrderByDescending(v => v.Saves).ThenByDescending(v => v.SavedUtc).ToList();
        }

        internal static LinkVersion FindByGuid(string modelKey, string versionGuid)
        {
            if (string.IsNullOrEmpty(versionGuid)) return null;
            return List(modelKey).FirstOrDefault(v => string.Equals(v.VersionGuid, versionGuid, StringComparison.OrdinalIgnoreCase));
        }

        internal static LinkVersion FindByCloudId(string modelKey, string cloudVersionId)
        {
            if (string.IsNullOrEmpty(cloudVersionId)) return null;
            return List(modelKey).FirstOrDefault(v => v.CloudVersionId == cloudVersionId);
        }

        /// <summary>Adds or replaces (same save GUID, else same cache key) a cached version.</summary>
        internal static void Register(string modelKey, LinkVersion v)
        {
            if (string.IsNullOrEmpty(modelKey) || v?.CacheKey == null) return;
            try
            {
                var list = Read(modelKey).Where(x => SnapshotCache.Has(x.CacheKey)).ToList();
                var same = list.Where(x => x.CacheKey == v.CacheKey ||
                                           (!string.IsNullOrEmpty(v.VersionGuid) && string.Equals(x.VersionGuid, v.VersionGuid, StringComparison.OrdinalIgnoreCase))).ToList();
                // Keep what was known about the cloud version when the same save is met again (e.g. as the live link).
                var cloud = same.FirstOrDefault(x => x.CloudVersionId != null);
                if (v.CloudVersionId == null && cloud != null) { v.CloudVersionId = cloud.CloudVersionId; v.CloudVersionNumber = cloud.CloudVersionNumber; }
                list.RemoveAll(x => same.Contains(x));
                list.Add(v);
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FileFor(modelKey), JsonConvert.SerializeObject(list, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn($"[Federator] could not record cached version: {ex.Message}"); }
        }

        private static List<LinkVersion> Read(string modelKey)
        {
            try
            {
                string file = FileFor(modelKey);
                if (!File.Exists(file)) return new List<LinkVersion>();
                return JsonConvert.DeserializeObject<List<LinkVersion>>(File.ReadAllText(file)) ?? new List<LinkVersion>();
            }
            catch (Exception ex)
            {
                Log.Info($"[Federator] version list unreadable, starting over: {ex.Message}");
                return new List<LinkVersion>();
            }
        }

        private static string FileFor(string modelKey)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(modelKey ?? ""));
                return Path.Combine(Folder, string.Concat(hash.Select(b => b.ToString("x2"))) + ".json");
            }
        }
    }
}
