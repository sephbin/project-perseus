using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.web;

namespace ProjectPerseus.violations
{
    internal static class ViolationSettingsCache
    {
        private static readonly ConcurrentDictionary<string, (ViolationSettings Settings, DateTime LoadedAt)> _cache
            = new ConcurrentDictionary<string, (ViolationSettings, DateTime)>();

        // Unconditional async fetch — use on document open and after batchClose.
        internal static void LoadAsync(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return;
            Task.Run(() => Load(docGuid, baseUrl));
        }

        // Fetches only when cached value is older than maxAgeMinutes — use when piggybacking
        // on existing server contacts (e.g. real-time action flush) to avoid excess requests.
        internal static void LoadAsyncIfStale(string docGuid, string baseUrl, int maxAgeMinutes = 5)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return;
            if (!_cache.TryGetValue(docGuid, out var entry) ||
                (DateTime.UtcNow - entry.LoadedAt).TotalMinutes >= maxAgeMinutes)
                Task.Run(() => Load(docGuid, baseUrl));
        }

        private static void Load(string docGuid, string baseUrl)
        {
            try
            {
                var endpoint = $"{baseUrl}/api/source/{docGuid}/violation-settings/";
                var token    = AuthService.GetAuthTokenSafely();
                var scheme   = AuthService.GetAuthSchemeSafely();
                var json     = WebHelper.Get(endpoint, token, null, scheme);
                var settings = JsonConvert.DeserializeObject<ViolationSettings>(json);
                if (settings != null)
                {
                    _cache[docGuid] = (settings, DateTime.UtcNow);
                    Log.Info($"[ViolationSettingsCache] Loaded for {docGuid}: " +
                             $"{settings.ProtectedCategories.Count} categories, " +
                             $"{settings.ProtectedElementIds.Count} element IDs");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationSettingsCache] Load failed for {docGuid}: {ex.Message}");
            }
        }

        internal static ViolationSettings Get(string docGuid) =>
            _cache.TryGetValue(docGuid, out var entry) ? entry.Settings : null;
    }
}
