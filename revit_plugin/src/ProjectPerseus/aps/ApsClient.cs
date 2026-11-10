using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.aps
{
    /// <summary>
    /// APS calls for the federator's cloud versions: list a cloud model's versions (Data Management API) and download
    /// one to a local .rvt. Cloud workshared models (C4RModel) download through the Revit Cloud Model "linked-files"
    /// endpoint (host model only, versions published since Feb 2025), falling back to the version's storage object
    /// (older published versions: a ZIP with the host model inside); other files through their storage object (OSS
    /// signed S3 download). Background threads only (awaits HTTP); tokens from ApsAuth.
    /// </summary>
    internal static class ApsClient
    {
        private const string Base = "https://developer.api.autodesk.com";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        internal static string DownloadFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProjectPerseus", "federator", "downloads");

        /// <param name="projectId">Data Management project id ("b.…", Document.GetProjectId()).</param>
        /// <param name="itemUrn">Item (lineage) URN (Document.GetCloudModelUrn()).</param>
        internal static async Task<List<CloudVersion>> ListVersionsAsync(string projectId, string itemUrn, bool interactive, CancellationToken ct)
        {
            string token = await ApsAuth.GetTokenAsync(interactive, ct).ConfigureAwait(false);
            if (token == null) return null;   // not signed in
            if (!projectId.StartsWith("b.", StringComparison.Ordinal)) projectId = "b." + projectId;   // Data Management wants "b.<guid>"
            var list = new List<CloudVersion>();
            string url = $"{Base}/data/v1/projects/{Uri.EscapeDataString(projectId)}/items/{Uri.EscapeDataString(itemUrn)}/versions";
            while (url != null)
            {
                var j = await GetJson(url, token, ct).ConfigureAwait(false);
                foreach (var d in (JArray)j["data"] ?? new JArray())
                {
                    var a = d["attributes"];
                    list.Add(new CloudVersion
                    {
                        Id = (string)d["id"],
                        Number = (int?)a?["versionNumber"] ?? 0,
                        Name = (string)a?["displayName"] ?? (string)a?["name"],
                        CreatedUtc = ((DateTime?)a?["createTime"] ?? DateTime.MinValue).ToUniversalTime(),
                        User = (string)a?["lastModifiedUserName"] ?? (string)a?["createUserName"],
                        ExtensionType = (string)a?["extension"]?["type"],
                        StorageId = (string)d["relationships"]?["storage"]?["data"]?["id"],
                    });
                }
                url = (string)j["links"]?["next"]?["href"];
            }
            return list.OrderByDescending(v => v.Number).ToList();
        }

        /// <summary>
        /// Finds a cloud model's Data Management ids from its cloud project + model GUIDs (ModelPath, read locally), for
        /// when Revit's Document.GetProjectId / GetCloudModelUrn fail: the hub holding project "b.&lt;guid&gt;", its top
        /// folders, then a folder search for the model GUID (else the file name, checked against the GUID).
        /// Returns (projectId, itemUrn); throws with the reason when not found / not accessible.
        /// </summary>
        internal static async Task<Tuple<string, string>> ResolveItemAsync(Guid projectGuid, Guid modelGuid, string fileName, CancellationToken ct)
        {
            string token = await ApsAuth.GetTokenAsync(false, ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("Not signed in to Autodesk.");
            string pid = "b." + projectGuid.ToString("D");
            string model = modelGuid.ToString("D");
            var hubs = await GetJson($"{Base}/project/v1/hubs", token, ct).ConfigureAwait(false);
            string lastError = "this project isn't in any hub your Autodesk account can see";
            foreach (var hub in (JArray)hubs["data"] ?? new JArray())
            {
                JObject top;
                try { top = await GetJson($"{Base}/project/v1/hubs/{Uri.EscapeDataString((string)hub["id"])}/projects/{pid}/topFolders", token, ct).ConfigureAwait(false); }
                catch (Exception ex) { lastError = ex.Message; continue; }   // not this hub
                foreach (var folder in (JArray)top["data"] ?? new JArray())
                {
                    string fid = Uri.EscapeDataString((string)folder["id"]);
                    foreach (string filter in new[]
                             {
                                 $"filter[attributes.extension.data.modelGuid]={Uri.EscapeDataString(model)}",
                                 string.IsNullOrEmpty(fileName) ? null : $"filter[attributes.displayName]={Uri.EscapeDataString(fileName)}",
                             })
                    {
                        if (filter == null) continue;
                        JObject found;
                        try { found = await GetJson($"{Base}/data/v1/projects/{pid}/folders/{fid}/search?{filter}", token, ct).ConfigureAwait(false); }
                        catch (Exception ex) { lastError = ex.Message; continue; }
                        foreach (var v in (JArray)found["data"] ?? new JArray())
                        {
                            string guid = (string)v["attributes"]?["extension"]?["data"]?["modelGuid"];
                            if (guid != null && !string.Equals(guid, model, StringComparison.OrdinalIgnoreCase)) continue;
                            string item = (string)v["relationships"]?["item"]?["data"]?["id"];
                            if (item != null)
                            {
                                Log.Info($"[APS] resolved {fileName} -> {pid} / {item}");
                                return Tuple.Create(pid, item);
                            }
                        }
                    }
                }
                lastError = "the model wasn't found in its project's folders";
            }
            throw new InvalidOperationException($"Couldn't find {fileName} in Autodesk Docs: {lastError}");
        }

        /// <summary>Downloads a version to DownloadFolder and returns the local .rvt path. progress: fraction or -1.</summary>
        internal static async Task<string> DownloadAsync(string projectId, CloudVersion v, IProgress<double> progress, CancellationToken ct)
        {
            string token = await ApsAuth.GetTokenAsync(true, ct).ConfigureAwait(false);
            string name = Path.GetFileNameWithoutExtension(v.Name ?? "model");
            string dir = Path.Combine(DownloadFolder, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, $"{Safe(name)}_v{v.Number}.rvt");

            string signed = null;
            if (v.IsCloudWorkshared)
            {
                try { signed = await RcmHostUrl(projectId, v.Id, token, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Info($"[APS] RCM linked-files unavailable for v{v.Number} ({ex.Message}); trying the storage object"); }
            }
            if (signed == null)
            {
                if (string.IsNullOrEmpty(v.StorageId))
                    throw new InvalidOperationException($"Version {v.Number} has no downloadable file (it may not be published).");
                signed = await StorageUrl(v.StorageId, token, ct).ConfigureAwait(false);
            }

            string raw = target + ".part";
            await DownloadTo(signed, raw, progress, ct).ConfigureAwait(false);
            if (IsZip(raw))
            {
                // Older cloud workshared versions: ZIP with the host model (+ its links); take the host.
                using (var zip = ZipFile.OpenRead(raw))
                {
                    var rvts = zip.Entries.Where(e => e.FullName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)).ToList();
                    var host = rvts.FirstOrDefault(e => string.Equals(Path.GetFileNameWithoutExtension(e.Name), name, StringComparison.OrdinalIgnoreCase))
                               ?? rvts.OrderByDescending(e => e.Length).FirstOrDefault()
                               ?? throw new InvalidOperationException("The downloaded package has no Revit model in it.");
                    host.ExtractToFile(target, true);
                }
                File.Delete(raw);
            }
            else File.Move(raw, target);
            Log.Info($"[APS] downloaded {v.Name} v{v.Number} ({new FileInfo(target).Length / (1024 * 1024):N0} MB)");
            return target;
        }

        /// <summary>Deletes a downloaded version (after its snapshot is cached).</summary>
        internal static void Discard(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !path.StartsWith(DownloadFolder, StringComparison.OrdinalIgnoreCase)) return;
                var dir = Path.GetDirectoryName(path);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex) { Log.Info($"[APS] download left behind ({path}): {ex.Message}"); }
        }

        private static async Task<string> RcmHostUrl(string projectId, string versionId, string token, CancellationToken ct)
        {
            string project = projectId.StartsWith("b.", StringComparison.Ordinal) ? projectId.Substring(2) : projectId;
            string url = $"{Base}/construction/rcm/v1/projects/{project}/published-versions/{Uri.EscapeDataString(versionId)}/linked-files?includeHost=true";
            var j = await GetJson(url, token, ct).ConfigureAwait(false);
            string signed = (string)j["hostFile"]?["signedUrl"] ?? (string)j["data"]?["hostFile"]?["signedUrl"];
            if (string.IsNullOrEmpty(signed)) throw new InvalidOperationException("no host file URL in the response");
            return signed;
        }

        // "urn:adsk.objects:os.object:<bucket>/<object key>" -> OSS signed S3 download URL.
        private static async Task<string> StorageUrl(string storageId, string token, CancellationToken ct)
        {
            const string prefix = "urn:adsk.objects:os.object:";
            if (!storageId.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("unexpected storage id " + storageId);
            string rest = storageId.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            string bucket = rest.Substring(0, slash), key = rest.Substring(slash + 1);
            var j = await GetJson($"{Base}/oss/v2/buckets/{bucket}/objects/{Uri.EscapeDataString(key)}/signeds3download", token, ct).ConfigureAwait(false);
            return (string)j["url"] ?? throw new InvalidOperationException("no download URL for the file");
        }

        private static async Task<JObject> GetJson(string url, string token, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(body)}");
                    return JObject.Parse(body);
                }
            }
        }

        private static async Task DownloadTo(string url, string path, IProgress<double> progress, CancellationToken ct)
        {
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(path))
                {
                    var buf = new byte[1 << 16];
                    long done = 0; int n; var last = DateTime.MinValue;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                        done += n;
                        if ((DateTime.UtcNow - last).TotalMilliseconds > 200)
                        {
                            last = DateTime.UtcNow;
                            progress?.Report(total > 0 ? (double)done / total.Value : -1);
                        }
                    }
                }
            }
        }

        private static bool IsZip(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[4];
                return fs.Read(b, 0, 4) == 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;
            }
        }

        private static string Safe(string s) => string.Concat((s ?? "model").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        private static string Trim(string s) => s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }
}
