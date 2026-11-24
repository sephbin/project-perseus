using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.web;

namespace ProjectPerseus.collections
{
    /// <summary>
    /// Per-model cache of GET /api/source/&lt;guid&gt;/collections/ (django-perseus perseus/reference_collections.py).
    /// Loaded on DocumentOpened (SyncOrchestrator) and refreshed by the Collections window; no network on the UI thread
    /// except LoadNow from a command.
    /// </summary>
    internal static class CollectionsCache
    {
        private static readonly ConcurrentDictionary<string, CollectionsDto> _cache =
            new ConcurrentDictionary<string, CollectionsDto>();

        internal static void LoadAsync(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return;
            Task.Run(() => LoadNow(docGuid, baseUrl));
        }

        /// <summary>Synchronous fetch; null when the server could not be reached (or predates collections).</summary>
        internal static CollectionsDto LoadNow(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return null;
            try
            {
                string json = WebHelper.Get($"{baseUrl.TrimEnd('/')}/api/source/{docGuid}/collections/",
                    AuthService.GetAuthTokenSafely(), null, AuthService.GetAuthSchemeSafely());
                var dto = JsonConvert.DeserializeObject<CollectionsDto>(json);
                if (dto == null) return null;
                dto.Collections = dto.Collections?.Where(c => c != null && c.Enabled && !string.IsNullOrWhiteSpace(c.Name)).ToList()
                                  ?? new System.Collections.Generic.List<CollectionDto>();
                string before = _cache.TryGetValue(docGuid, out var old) ? Signature(old) : "";
                _cache[docGuid] = dto;
                // The Auto Parameters index records which packs depend on which member signs; rebuild it (at idle) when
                // the collections it was built with changed, including when they arrive after the index was built.
                if (Signature(dto) != before) ids.IdAssigner.InvalidateIndex(docGuid);
                Log.Info($"[CollectionsCache] {docGuid}: " + (dto.Collections.Count == 0 ? "no collections" :
                    string.Join("; ", dto.Collections.Select(c =>
                        $"{c.Name} on {c.LivesOn} [{string.Join(", ", c.OwnerCategories)}] {c.Storage} '{c.MembersParameterName}'" +
                        (c.HasOverrides ? $", overrides '{c.OverrideParameterName}'" : "")))));
                return dto;
            }
            catch (Exception ex)
            {
                Log.Info($"[CollectionsCache] no collections for {docGuid} ({ex.Message}).");
                return null;
            }
        }

        private static string Signature(CollectionsDto dto) =>
            dto?.Collections == null || dto.Collections.Count == 0 ? "" : JsonConvert.SerializeObject(dto.Collections);

        internal static CollectionsDto Get(string docGuid) =>
            docGuid != null && _cache.TryGetValue(docGuid, out var dto) ? dto : null;

        internal static void Remove(string docGuid)
        {
            if (!string.IsNullOrEmpty(docGuid)) _cache.TryRemove(docGuid, out _);
        }
    }
}
