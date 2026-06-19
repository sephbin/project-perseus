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

        // Cache trigger list per document (keyed by PathName or Title). Populated once per session.
        private static readonly Dictionary<string, List<string>> _triggerCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Called from SyncOrchestrator when a document finishes opening.
        // Connects to Django, reads the trigger list, then runs onStart and onIdle immediately.
        // onIdle runs here too because opening a document is the first idle moment for it —
        // there's no reason to wait for the 30-minute timer to lap.
        public static void HandleDocumentOpened(Document doc)
        {
            Log.Info($"[KeyScheduleAutoImporter] Document opened — '{doc.Title}'");
            RunForDocument(doc, TriggerOnStart);
            RunForDocument(doc, TriggerOnIdle);
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

                    if (!File.Exists(cfg.ExcelFilePath))
                    {
                        Log.Warn($"[KeyScheduleAutoImporter]   '{cfg.RevitScheduleName}': file not found — {cfg.ExcelFilePath}");
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

                        var (created, updated) = service.ImportRows(doc, data, cfg.RevitScheduleName);
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

        private static List<string> GetCachedTriggers(Document doc)
        {
            string key = !string.IsNullOrEmpty(doc.PathName) ? doc.PathName : doc.Title;
            if (!_triggerCache.TryGetValue(key, out List<string> cached))
            {
                // FetchTriggers returns null when auth isn't ready or a network error occurs.
                // Don't cache null — let the next trigger event retry.
                var fetched = FetchTriggers(doc);
                if (fetched != null)
                    _triggerCache[key] = fetched;
                return fetched ?? new List<string>();
            }
            return cached;
        }

        // Returns the trigger list on success, or null if auth isn't ready / network failed.
        // Null is NOT cached so the next trigger event will retry.
        // An empty list IS cached — it means Django was reached but no triggers are configured.
        private static List<string> FetchTriggers(Document doc)
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
                string response = WebHelper.Post(endpoint, token, payload,
                                                 AuthService.GetAuthSchemeSafely());

                var json = JObject.Parse(response);
                var triggers = json["source"]?["parameter_dict"]?["perseusOption_importKeyScheduleTriggers"]
                                   ?.ToObject<List<string>>()
                               ?? new List<string>();

                Log.Info($"[KeyScheduleAutoImporter] '{doc.Title}' triggers: [{string.Join(", ", triggers)}]");
                return triggers;
            }
            catch (Exception ex)
            {
                Log.Info($"[KeyScheduleAutoImporter] FetchTriggers '{doc.Title}': {ex.Message}");
                return null; // treat any failure as "not ready" — will retry next event
            }
        }
    }
}
