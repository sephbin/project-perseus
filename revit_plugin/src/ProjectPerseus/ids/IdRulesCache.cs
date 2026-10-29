using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.web;

namespace ProjectPerseus.ids
{
    internal static class IdRulesCache
    {
        private static readonly ConcurrentDictionary<string, IdRulesDto> _cache =
            new ConcurrentDictionary<string, IdRulesDto>();

        // True once any document has had its rules loaded (even an empty rule set). Cheap gate for
        // DocumentChanged so nothing runs when the server has never been reached.
        internal static bool HasAny => !_cache.IsEmpty;

        internal static void LoadAsync(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return;
            Task.Run(() => LoadNow(docGuid, baseUrl));
        }

        // Synchronous fetch; returns the rules, or null when the server could not be reached.
        internal static IdRulesDto LoadNow(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return null;
            try
            {
                var endpoint = $"{baseUrl}/api/source/{docGuid}/id-rules/";
                var token    = AuthService.GetAuthTokenSafely();
                var scheme   = AuthService.GetAuthSchemeSafely();
                var json     = WebHelper.Get(endpoint, token, null, scheme);
                var rules    = JsonConvert.DeserializeObject<IdRulesDto>(json);
                if (rules == null) return null;
                _cache[docGuid] = rules;
                Log.Info($"[IdRulesCache] Loaded for {docGuid}: {rules.Rules.Count(r => r.Enabled)} active rules, " +
                         $"{rules.CategoryPrefixes.Count} category prefixes");
                return rules;
            }
            catch (Exception ex)
            {
                Log.Warn($"[IdRulesCache] Load failed for {docGuid}: {ex.Message}");
                return null;
            }
        }

        internal static IdRulesDto Get(string docGuid) =>
            _cache.TryGetValue(docGuid, out var rules) ? rules : null;

        internal static void Remove(string docGuid) => _cache.TryRemove(docGuid, out _);
    }
}
