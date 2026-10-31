using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.web;

namespace ProjectPerseus.ids
{
    /// <summary>One ID value an element holds, as sent to the server's claims registry.</summary>
    internal class IdClaimItem
    {
        public string Parameter;
        public string Uid;
        public long ElementId;
        public string Value;
    }

    /// <summary>Tells <see cref="IdAssigner.Run"/> which elements to renumber and which values are taken elsewhere.</summary>
    internal class IdClashFix
    {
        public readonly HashSet<string> Force = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "param|elementId"
        public readonly HashSet<string> Taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "param|value"

        public static string ForceKey(string parameter, long elementId) => parameter + "|" + elementId;
        public static string TakenKey(string parameter, string value) => parameter + "|" + value;
    }

    /// <summary>
    /// Pre-sync clash check. Two users can allocate the same number before either syncs, so just before a
    /// sync the model's ID values are posted to the server's registry. A value already held by another
    /// element is a clash (the lower ElementId keeps it); the other element is renumbered here, while it is
    /// still this user's to edit, so the clash never reaches central.
    /// </summary>
    internal static class IdClaims
    {
        internal class CheckResult
        {
            public readonly List<IdClaimItem> Conflicts = new List<IdClaimItem>();
        }

        internal class FixResult
        {
            public int Renumbered;
            public readonly List<IdClaimItem> Unfixable = new List<IdClaimItem>();
            public bool ServerUnreachable;
            public int TotalClaims;
        }

        private const int MaxRounds = 30;

        // docGuid -> "param|uid" -> value last accepted by the server. Lets later checks send only changes.
        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _sent =
            new ConcurrentDictionary<string, Dictionary<string, string>>();

        internal static void Reset(string docGuid) => _sent.TryRemove(docGuid, out _);

        /// <summary>Posts the snapshot; returns null when the server could not be reached (fail open).</summary>
        internal static CheckResult Check(string docGuid, string baseUrl, List<IdClaimItem> snapshot)
        {
            try
            {
                bool full = !_sent.TryGetValue(docGuid, out var last);
                var now = snapshot.ToDictionary(i => i.Parameter + "|" + i.Uid, i => i, StringComparer.Ordinal);

                var send = full
                    ? snapshot
                    : snapshot.Where(i => !last.TryGetValue(i.Parameter + "|" + i.Uid, out var v) || v != i.Value).ToList();
                var release = full
                    ? new List<object>()
                    : last.Keys.Where(k => !now.ContainsKey(k))
                           .Select(k => { int cut = k.IndexOf('|'); return (object)new { p = k.Substring(0, cut), uid = k.Substring(cut + 1) }; })
                           .ToList();

                var body = JsonConvert.SerializeObject(new
                {
                    user = Environment.UserName.ToLowerInvariant(),
                    full,
                    claims = send.Select(i => new { p = i.Parameter, uid = i.Uid, eid = i.ElementId, v = i.Value }),
                    release,
                });
                string json = WebHelper.Post($"{baseUrl}/api/source/{docGuid}/id-claims/",
                                             AuthService.GetAuthTokenSafely(), body, AuthService.GetAuthSchemeSafely());
                var reply = JsonConvert.DeserializeAnonymousType(json, new { conflicts = new List<ConflictDto>() });

                var result = new CheckResult();
                var clash = new HashSet<string>();
                foreach (var c in reply?.conflicts ?? new List<ConflictDto>())
                {
                    clash.Add(c.p + "|" + c.uid);
                    if (now.TryGetValue(c.p + "|" + c.uid, out var item)) result.Conflicts.Add(item);
                }

                var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in now) if (!clash.Contains(kv.Key)) accepted[kv.Key] = kv.Value.Value;
                _sent[docGuid] = accepted;
                Log.Info($"[IdClaims] checked {snapshot.Count} value(s), sent {send.Count}, released {release.Count}, {result.Conflicts.Count} clash(es)");
                return result;
            }
            catch (Exception ex)
            {
                Log.Warn($"[IdClaims] check failed (continuing without it): {ex.Message}");
                return null;
            }
        }

        private class ConflictDto { public string p; public string uid; public string v; }

        /// <summary>
        /// Renumbers this user's clashing elements. True when the model is clean afterwards (or the server could not
        /// be asked); false when some clashes could not be fixed. <paramref name="summary"/> describes what happened,
        /// empty when nothing clashed. Must run outside any document event.
        /// </summary>
        internal static bool EnsureNoClashes(Document doc, out string summary)
        {
            summary = "";
            string docGuid = revit.ModelGuidStorage.GetOrCreate(doc);
            var rules = IdRulesCache.Get(docGuid);
            string baseUrl = config.Config.Instance.BaseUrl;
            if (rules == null || string.IsNullOrEmpty(baseUrl) || !rules.Rules.Any(r => r.Enabled)) return true;

            var fix = Resolve(doc, docGuid, baseUrl, rules);
            if (fix.Unfixable.Count > 0)
            {
                summary = $"{fix.Unfixable.Count} ID(s) clash with values other users already hold and could not be renumbered " +
                          "(the elements may be checked out by someone else or read-only):\n" +
                          string.Join("\n", fix.Unfixable.Take(8).Select(c => $"  {c.Value}  ({c.Parameter})")) +
                          (fix.Unfixable.Count > 8 ? "\n  ..." : "");
                return false;
            }
            if (fix.Renumbered > 0)
                summary = $"{fix.Renumbered} ID(s) clashed with values other users had already taken, so they were renumbered before syncing.";
            return true;
        }

        /// <summary>
        /// Checks the model against the registry and renumbers this user's clashing elements until none remain.
        /// Opens its own transactions, so it must run outside any document event (e.g. from an ExternalEvent).
        /// </summary>
        internal static FixResult Resolve(Document doc, string docGuid, string baseUrl, IdRulesDto rules)
        {
            var fix = new IdClashFix();
            var result = new FixResult();
            var tried = new HashSet<string>(StringComparer.Ordinal);

            for (int round = 0; round < MaxRounds; round++)
            {
                var snapshot = IdAssigner.Snapshot(doc, rules);
                result.TotalClaims = snapshot.Count;
                var check = Check(docGuid, baseUrl, snapshot);
                if (check == null) { result.ServerUnreachable = true; return result; }
                if (check.Conflicts.Count == 0) return result;

                // A clash that survives a renumbering attempt cannot be fixed from here (read-only, owned by
                // someone else...). Report it rather than looping on it.
                var fixable = new List<IdClaimItem>();
                foreach (var c in check.Conflicts)
                {
                    string key = c.Parameter + "|" + c.Uid + "|" + c.Value;
                    fix.Taken.Add(IdClashFix.TakenKey(c.Parameter, c.Value));
                    if (tried.Add(key)) fixable.Add(c);
                    else result.Unfixable.Add(c);
                }
                if (fixable.Count == 0) return result;

                foreach (var c in fixable) fix.Force.Add(IdClashFix.ForceKey(c.Parameter, c.ElementId));
                var ids = new HashSet<long>(fixable.Select(c => c.ElementId));
                var run = IdAssigner.Run(doc, docGuid, rules, ids, fix: fix);
                result.Renumbered += run.Assigned;
                Log.Info($"[IdClaims] round {round + 1}: {fixable.Count} clash(es), renumbered {run.Assigned}");
            }
            return result;
        }
    }
}
