using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.util;
using ProjectPerseus.web;

namespace ProjectPerseus.sync
{
    // Runs ImportKeyScheduleCommand logic automatically when Revit fires onStart / onSync / onIdle.
    // The trigger list for each document is fetched from Django:
    //   GET {baseUrl}/key-schedule-config/{docGuid}/
    //   → { "triggers": ["onSync"] }
    //
    // Auto-import is non-interactive: creates and updates rows but does NOT prompt to delete
    // extras — use the manual Import button to review and confirm any deletions.
    public static class KeyScheduleAutoImporter
    {
        public const string TriggerOnStart = "onStart";
        public const string TriggerOnSync  = "onSync";
        public const string TriggerOnIdle  = "onIdle";

        private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);

        private static DateTime _lastIdleRun = DateTime.MinValue;

        // What was last imported per document + schedule: the Excel file's last-write time and size. An unchanged
        // file is not read or re-imported again (it used to be on every sync: hundreds of element updates each time).
        private static readonly Dictionary<string, (DateTime WriteUtc, long Size)> _imported =
            new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);

        // Cache trigger list per document (keyed by PathName or Title). Populated once per session.
        private static readonly Dictionary<string, List<string>> _triggerCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // A failed fetch (server error / timeout) isn't retried for a while, so a broken server can't slow every trigger.
        private static readonly Dictionary<string, DateTime> _failedUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _fetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

        // Called from SyncOrchestrator when a document finishes opening.
        // Connects to Django, reads the trigger list, then runs onStart and onIdle immediately.
        // onIdle runs here too because opening a document is the first idle moment for it —
        // there's no reason to wait for the 30-minute timer to lap.
        // The trigger list is fetched on a background thread (it was two blocking server calls on the UI thread:
        // ~4 s added to every model open while the server returned 500s); the imports then run in an API context.
        public static void HandleDocumentOpened(Document doc)
        {
            Log.Info($"[KeyScheduleAutoImporter] Document opened — '{doc.Title}'");
            string key = DocKey(doc);
            if (TryCached(key, out _))
            {
                RunForDocument(doc, TriggerOnStart);
                RunForDocument(doc, TriggerOnIdle);
                return;
            }
            var request = BuildRequest(doc);
            if (request == null) return;
            lock (_triggerCache)
            {
                if (!_fetching.Add(key)) return;
            }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var triggers = Send(request.Value.Endpoint, request.Value.Payload, request.Value.Token, request.Value.Title);
                    Remember(key, triggers);
                    if (triggers == null || triggers.Count == 0) return;
                    queue.OwnershipUiEvent.Run(app =>
                    {
                        foreach (Document d in app.Application.Documents)
                        {
                            if (d.IsFamilyDocument || !string.Equals(DocKey(d), key, StringComparison.OrdinalIgnoreCase)) continue;
                            RunForDocument(d, TriggerOnStart);
                            RunForDocument(d, TriggerOnIdle);
                            break;
                        }
                    });
                }
                catch (Exception ex) { Log.Info($"[KeyScheduleAutoImporter] background trigger fetch: {ex.Message}"); }
                finally { lock (_triggerCache) _fetching.Remove(key); }
            });
        }

        // Called from Plugin's Idling handler on every idle tick.
        public static void HandleIdling(UIApplication uiApp)
        {
            if (DateTime.UtcNow - _lastIdleRun < IdleInterval) return;

            _lastIdleRun = DateTime.UtcNow;
            Log.Info("[KeyScheduleAutoImporter] onIdle trigger firing");

            foreach (Document doc in uiApp.Application.Documents)
            {
                if (!doc.IsFamilyDocument)
                    RunForDocument(doc, TriggerOnIdle);
            }
        }

        // Called from SyncOrchestrator after a successful sync for the document that synced.
        public static void HandleSync(Document doc)
        {
            Log.Info($"[KeyScheduleAutoImporter] onSync trigger — '{doc.Title}'");
            RunForDocument(doc, TriggerOnSync);
        }

        private static void RunForDocument(Document doc, string trigger)
        {
            try
            {
                List<string> triggers = GetCachedTriggers(doc);
                if (!triggers.Any(t => string.Equals(t, trigger, StringComparison.OrdinalIgnoreCase)))
                    return;

                var service = new KeyScheduleService();
                List<KeyScheduleConfig> configs = service.LoadConfig(doc);
                if (configs.Count == 0) return;

                Log.Info($"[KeyScheduleAutoImporter] [{trigger}] '{doc.Title}': {configs.Count} schedule(s)");

                foreach (var cfg in configs)
                {
                    if (string.IsNullOrEmpty(cfg.ExcelFilePath))
                    {
                        Log.Info($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': no file path — skipped");
                        continue;
                    }

                    string excelPath = Environment.ExpandEnvironmentVariables(cfg.ExcelFilePath);
                    if (!File.Exists(excelPath))
                    {
                        Log.Warn($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': file not found — {cfg.ExcelFilePath}");
                        continue;
                    }

                    var info = new FileInfo(excelPath);
                    var stamp = (info.LastWriteTimeUtc, info.Length);
                    string stampKey = DocKey(doc) + "|" + cfg.RevitScheduleName;
                    if (_imported.TryGetValue(stampKey, out var last) && last == stamp)
                    {
                        Log.Info($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': Excel unchanged since last import — skipped");
                        continue;
                    }

                    try
                    {
                        KeyScheduleData data = service.ReadFromExcel(cfg.ExcelFilePath, cfg.ExcelSheetName);
                        if (data == null)
                        {
                            Log.Warn($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': sheet '{cfg.ExcelSheetName}' not found in file");
                            continue;
                        }

                        data = KeyScheduleService.ApplyFilters(data, cfg.Filters);
                        var (created, updated) = service.ImportRows(doc, data, cfg.RevitScheduleName);
                        _imported[stampKey] = stamp;
                        Log.Info($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': +{created} created  ~{updated} updated");

                        // Count extras but do NOT delete — auto-import is non-interactive.
                        var excelKeys = new HashSet<string>(
                            data.Rows
                                .Select(r => r.TryGetValue(KeyScheduleService.KeyParamName, out string k) ? k : "")
                                .Where(k => !string.IsNullOrEmpty(k)),
                            StringComparer.OrdinalIgnoreCase);

                        int extras = 0;
                        foreach (var (key, _) in service.GetScheduleElements(doc, cfg.RevitScheduleName))
                        {
                            if (string.IsNullOrEmpty(key) || !excelKeys.Contains(key))
                                extras++;
                        }

                        if (extras > 0)
                            Log.Info($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': {extras} extra row(s) not in Excel — use manual Import to review/delete");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': {ex.Message}");
                        Log.Exception(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[KeyScheduleAutoImporter] [{trigger}] '{doc.Title}': {ex.Message}");
                Log.Exception(ex);
            }
        }

        private static string DocKey(Document doc) => !string.IsNullOrEmpty(doc.PathName) ? doc.PathName : doc.Title;

        private static bool TryCached(string key, out List<string> triggers)
        {
            lock (_triggerCache) return _triggerCache.TryGetValue(key, out triggers);
        }

        /// <summary>Caches a successful fetch (an empty list too: server reached, nothing configured); a failure (null) backs off.</summary>
        private static void Remember(string key, List<string> triggers)
        {
            lock (_triggerCache)
            {
                if (triggers != null) { _triggerCache[key] = triggers; _failedUntil.Remove(key); }
                else _failedUntil[key] = DateTime.UtcNow + RetryAfterFailure;
            }
        }

        private static List<string> GetCachedTriggers(Document doc)
        {
            string key = DocKey(doc);
            if (TryCached(key, out List<string> cached)) return cached;
            lock (_triggerCache)
            {
                if (_fetching.Contains(key)) return new List<string>();   // the open-time fetch is still running
                if (_failedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) return new List<string>();
            }
            var request = BuildRequest(doc);
            if (request == null) return new List<string>();
            var fetched = Send(request.Value.Endpoint, request.Value.Payload, request.Value.Token, request.Value.Title);
            Remember(key, fetched);
            return fetched ?? new List<string>();
        }

        // What the trigger request needs from the model (read in the API context); null = not configured / not signed in yet.
        private static (string Endpoint, string Payload, string Token, string Title)? BuildRequest(Document doc)
        {
            try
            {
                string baseUrl = Config.Instance.BaseUrl;
                if (!UrlUtils.IsValidUrl(baseUrl)) return null;

                string token = AuthService.GetAuthTokenSilentlyOrNull();
                if (token == null) return null; // not authenticated yet — will retry next event

                string docGuid = ModelGuidStorage.GetOrCreate(doc);

                var metadata = new
                {
                    documentGuid = docGuid,
                    fileName     = doc.Title,
                    filePath     = doc.PathName,
                    revitVersion = doc.Application.VersionNumber,
                    timestamp    = DateTime.UtcNow.ToString("o"),
                    projectInfo  = new
                    {
                        number = doc.ProjectInformation?.Number     ?? "",
                        name   = doc.ProjectInformation?.Name       ?? "",
                        client = doc.ProjectInformation?.ClientName ?? ""
                    }
                };

                string endpoint = $"{baseUrl.TrimEnd('/')}/registersource/";
                string payload  = Newtonsoft.Json.JsonConvert.SerializeObject(metadata);
                return (endpoint, payload, token, doc.Title);
            }
            catch (Exception ex)
            {
                Log.Info($"[KeyScheduleAutoImporter] trigger request for '{doc.Title}': {ex.Message}");
                return null;
            }
        }

        // The server call (no Revit API: safe on a background thread). Null = failed (backs off, retried later).
        private static List<string> Send(string endpoint, string payload, string token, string title)
        {
            try
            {
                string response = WebHelper.Post(endpoint, token, payload,
                                                 AuthService.GetAuthSchemeSafely(), timeoutMs: WebHelper.SyncTimeoutMs);
                var json = JObject.Parse(response);
                var triggers = json["source"]?["parameter_dict"]?["perseusOption_importKeyScheduleTriggers"]
                                   ?.ToObject<List<string>>()
                               ?? new List<string>();
                Log.Info($"[KeyScheduleAutoImporter] '{title}' triggers: [{string.Join(", ", triggers)}]");
                return triggers;
            }
            catch (Exception ex)
            {
                Log.Info($"[KeyScheduleAutoImporter] FetchTriggers '{title}': {ex.Message} (retry in {RetryAfterFailure.TotalMinutes:0} min)");
                return null;
            }
        }
    }
}
