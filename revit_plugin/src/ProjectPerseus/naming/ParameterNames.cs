using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using ProjectPerseus.web;

namespace ProjectPerseus.naming
{
    /// <summary>
    /// Names of the parameters Perseus creates. Code refers to them by key (PerseusId, LinkStatus...); the name comes from
    /// the model's naming convention on the server (django-perseus perseus/parameter_naming.py: built-in default &lt;
    /// project (forked from server-wide) &lt; model) and from what the model already has:
    ///
    ///   For(doc, key)    = the name IN USE: the model's record (ParameterNameRecords) -> the built-in name when that
    ///                      Perseus parameter is already bound (models from before naming conventions) -> Wanted.
    ///   Wanted(doc, key) = the convention's name (what a new parameter is called, what Rename renames to).
    ///
    /// So changing a convention never renames existing parameters by itself; Rename parameters (ParameterNamingWindow)
    /// does, on request. Conventions are loaded with the model (SyncOrchestrator.OnDocumentOpened) and cached per model
    /// GUID; without one (server unreachable) the built-in names apply. Revit API context for the doc-based calls.
    /// </summary>
    internal static class ParameterNames
    {
        internal const string PerseusId = "perseus.id";
        internal const string LinkSource = "linksync.source";
        internal const string LinkStatus = "linksync.status";
        internal const string LinkDecision = "linksync.decision";
        internal const string LinkComment = "linksync.comment";
        internal const string LinkAssignee = "linksync.assignee";

        /// <summary>Key -> built-in name, in display order (same list as the server's PARAMETER_KEYS).</summary>
        internal static readonly (string Key, string Default, string Label)[] Keys =
        {
            (PerseusId, "Perseus_ID", "Element UniqueId, written by Auto Parameters"),
            (LinkSource, "Perseus_LinkSource", "Link Sync: link and consultant element a family follows"),
            (LinkStatus, "Perseus_LinkStatus", "Link Sync: sync status of a mapped family"),
            (LinkDecision, "Perseus_LinkDecision", "Link Sync: last decision (accepted / rejected, who, when)"),
            (LinkComment, "Perseus_LinkComment", "Link Sync: latest comment"),
            (LinkAssignee, "Perseus_LinkAssignee", "Link Sync: assignee"),
        };

        internal static readonly string[] LinkSyncKeys = { LinkSource, LinkStatus, LinkDecision, LinkComment, LinkAssignee };

        internal static string Default(string key) => Keys.FirstOrDefault(k => k.Key == key).Default ?? key;

        // ── conventions from the server, per model GUID ─────────────────────

        private static readonly ConcurrentDictionary<string, ParameterNamingDto> _conventions =
            new ConcurrentDictionary<string, ParameterNamingDto>();

        internal static void LoadAsync(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return;
            Task.Run(() => LoadNow(docGuid, baseUrl));
        }

        /// <summary>Synchronous fetch (background threads / commands); null when the server could not be reached.</summary>
        internal static ParameterNamingDto LoadNow(string docGuid, string baseUrl)
        {
            if (string.IsNullOrEmpty(docGuid) || string.IsNullOrEmpty(baseUrl)) return null;
            try
            {
                string json = WebHelper.Get($"{baseUrl.TrimEnd('/')}/api/source/{docGuid}/parameter-naming/",
                    AuthService.GetAuthTokenSafely(), null, AuthService.GetAuthSchemeSafely());
                var dto = JsonConvert.DeserializeObject<ParameterNamingDto>(json);
                if (dto == null) return null;
                _conventions[docGuid] = dto;
                Log.Info($"[ParameterNames] convention for {docGuid}: prefix '{dto.Prefix}', group {dto.ParameterGroup}; " +
                         string.Join(", ", dto.Resolved.Select(kv => $"{kv.Key}={kv.Value}")));
                return dto;
            }
            catch (Exception ex)
            {
                // Older servers have no endpoint: built-in names, as before.
                Log.Info($"[ParameterNames] no convention for {docGuid} ({ex.Message}); built-in names apply.");
                return null;
            }
        }

        internal static void Remove(string docGuid)
        {
            if (!string.IsNullOrEmpty(docGuid)) _conventions.TryRemove(docGuid, out _);
        }

        internal static ParameterNamingDto Convention(Document doc)
        {
            string guid = doc == null ? null : ModelGuidStorage.TryGet(doc);
            return guid != null && _conventions.TryGetValue(guid, out var dto) ? dto : null;
        }

        /// <summary>The convention's name for a key (prefix included).</summary>
        internal static string Wanted(Document doc, string key)
        {
            var c = Convention(doc);
            if (c?.Resolved != null && c.Resolved.TryGetValue(key, out var n) && !string.IsNullOrWhiteSpace(n)) return n.Trim();
            return Default(key);
        }

        /// <summary>The convention's prefix applied to a name (not added twice), e.g. for collection parameters.</summary>
        internal static string WithPrefix(Document doc, string name)
        {
            string prefix = Convention(doc)?.Prefix ?? "";
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(name) || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return name;
            return prefix + name;
        }

        /// <summary>Parameter group key (Django ID_PARAMETER_GROUPS) for Perseus parameters.</summary>
        internal static string Group(Document doc)
        {
            string g = Convention(doc)?.ParameterGroup;
            return string.IsNullOrWhiteSpace(g) ? "other" : g;
        }

        // ── names in use, per document ──────────────────────────────────────

        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _inUse =
            new ConcurrentDictionary<string, Dictionary<string, string>>();

        private static string DocKey(Document doc) =>
            string.IsNullOrEmpty(doc.PathName) ? "unsaved|" + doc.GetHashCode() : doc.PathName;

        /// <summary>Forget the cached names in use (after a record is written, a sync, a reload, closing).</summary>
        internal static void Invalidate(Document doc)
        {
            if (doc != null) _inUse.TryRemove(DocKey(doc), out _);
        }

        /// <summary>The name a key's parameter has in this model (or will get when Perseus creates it).</summary>
        internal static string For(Document doc, string key)
        {
            if (doc == null) return Default(key);
            var map = _inUse.GetOrAdd(DocKey(doc), _ => ReadInUse(doc));
            return map.TryGetValue(key, out var n) ? n : Wanted(doc, key);
        }

        /// <summary>The name recorded for a key in this model (ParameterNameRecords), or null. For keys outside Keys, e.g.
        /// collection parameters ("collection.Signs.members:Sign_{n}" -> "COX_Sign_{n}").</summary>
        internal static string Recorded(Document doc, string key)
        {
            if (doc == null || string.IsNullOrEmpty(key)) return null;
            var map = _inUse.GetOrAdd(DocKey(doc), _ => ReadInUse(doc));
            return map.TryGetValue(key, out var n) ? n : null;
        }

        /// <summary>Names in use for every key: recorded, else built-in when that Perseus parameter is bound. Keys without
        /// either aren't listed (For falls back to Wanted).</summary>
        private static Dictionary<string, string> ReadInUse(Document doc)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var kv in ParameterNameRecords.Load(doc)) map[kv.Key] = kv.Value;
                var missing = Keys.Where(k => !map.ContainsKey(k.Key)).ToList();
                if (missing.Count > 0)
                {
                    var bound = new HashSet<Guid>(new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
                        .Cast<SharedParameterElement>().Select(s => s.GuidValue));
                    foreach (var k in missing)
                        if (bound.Contains(ids.SharedParameterManager.GuidFor(k.Default, false)) ||
                            bound.Contains(ids.SharedParameterManager.GuidFor(k.Default, true)))
                            map[k.Key] = k.Default;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[ParameterNames] reading the names in use failed: {ex.Message}");
            }
            return map;
        }

        /// <summary>Keys whose name in use differs from the convention's (what Rename parameters would change) and whose
        /// parameter exists in the model.</summary>
        internal static List<(string Key, string Current, string Wanted)> Pending(Document doc)
        {
            var bound = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
                .Cast<SharedParameterElement>().Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var result = new List<(string, string, string)>();
            foreach (var k in Keys)
            {
                string cur = For(doc, k.Key), want = Wanted(doc, k.Key);
                if (!string.Equals(cur, want, StringComparison.Ordinal) && bound.Contains(cur)) result.Add((k.Key, cur, want));
            }
            return result;
        }

        /// <summary>Every Link Sync parameter name in use (for filtering them out of parameter pickers).</summary>
        internal static HashSet<string> LinkSyncNames(Document doc) =>
            new HashSet<string>(LinkSyncKeys.Select(k => For(doc, k)).Concat(LinkSyncKeys.Select(Default)), StringComparer.OrdinalIgnoreCase);
    }
}
