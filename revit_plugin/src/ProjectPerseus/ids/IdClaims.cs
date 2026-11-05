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
            // Clashes on elements checked out by someone else: not this user's to fix, and this user's sync can't push
            // them (only the owner's sync sends their changes). The owner's Perseus renumbers them when they sync.
            public readonly List<(IdClaimItem Claim, string Owner)> OwnedElsewhere = new List<(IdClaimItem, string)>();
            // Why renumbering failed, from IdAssigner's run results (group members, missing level, stale vs central...).
            public readonly SortedSet<string> Reasons = new SortedSet<string>();
            public bool ServerUnreachable;
            public int TotalClaims;

            public bool Blocking => Unfixable.Count > 0;
        }

        /// <summary>True (with the owner) when another user has this element checked out. Local cache read only.</summary>
        internal static bool IsOwnedElsewhere(Document doc, IdClaimItem c, out string owner)
        {
            owner = null;
            try
            {
                if (!doc.IsWorkshared) return false;
                var status = WorksharingUtils.GetCheckoutStatus(doc, revit.RevitExtensions.CreateId(c.ElementId), out owner);
                return status == CheckoutStatus.OwnedByOtherUser;
            }
            catch { return false; }
        }

        /// <summary>
        /// The clashes this user's sync could actually push (and so must fix first): everything except elements another
        /// user has checked out. Read-only, cheap; used by the pre-sync hook to decide whether to stop the sync.
        /// </summary>
        internal static List<IdClaimItem> BlockingConflicts(Document doc, IEnumerable<IdClaimItem> conflicts)
        {
            var blocking = new List<IdClaimItem>();
            foreach (var c in conflicts)
            {
                if (IsOwnedElsewhere(doc, c, out string owner))
                    Log.Info($"[IdClaims] clash on {c.Value} ({c.Parameter}) is on an element {owner} has checked out; " +
                             "not blocking this sync, their Perseus renumbers it when they sync.");
                else blocking.Add(c);
            }
            return blocking;
        }

        private const int MaxRounds = 30;
        // Server calls made from the fix event / auto-sync run on Revit's UI thread; never wait minutes there.
        internal const int FixTimeoutMs = 15000;

        // docGuid -> "param|uid" -> value last accepted by the server. Lets later checks send only changes.
        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _sent =
            new ConcurrentDictionary<string, Dictionary<string, string>>();

        internal static void Reset(string docGuid)
        {
            _sent.TryRemove(docGuid, out _);
            try { System.IO.File.Delete(SentPath(docGuid)); } catch { }
        }

        // What the server last accepted is saved per model, so the first check after Revit restarts sends only the
        // changes too (users typically open, sync and close, so in-memory state alone meant a full send every time).
        // Older than FullResendAfter, or missing, and the full set is sent so the server's registry can't drift.
        private static readonly TimeSpan FullResendAfter = TimeSpan.FromDays(1);

        private static string SentPath(string docGuid) =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "ProjectPerseus", "idclaims", docGuid + ".json");

        private static Dictionary<string, string> LoadSent(string docGuid)
        {
            try
            {
                var path = SentPath(docGuid);
                var info = new System.IO.FileInfo(path);
                if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > FullResendAfter) return null;
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(System.IO.File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static void SaveSent(string docGuid, Dictionary<string, string> accepted)
        {
            try
            {
                var path = SentPath(docGuid);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, JsonConvert.SerializeObject(accepted));
            }
            catch (Exception ex) { Log.Warn($"[IdClaims] could not save claim state: {ex.Message}"); }
        }

        /// <summary>
        /// Posts the snapshot; returns null when the server could not be reached (fail open). Thread-safe and free of
        /// Revit API calls, so the pre-sync hook runs it on a background task with a short timeout.
        /// </summary>
        internal static CheckResult Check(string docGuid, string baseUrl, List<IdClaimItem> snapshot,
                                          int timeoutMs = WebHelper.DefaultTimeoutMs)
        {
            try
            {
                if (!_sent.TryGetValue(docGuid, out var last))
                {
                    last = LoadSent(docGuid);
                    if (last != null) _sent[docGuid] = last;
                }
                bool full = last == null;
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
                                             AuthService.GetAuthTokenSafely(), body, AuthService.GetAuthSchemeSafely(), timeoutMs);
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
                SaveSent(docGuid, accepted);
                Log.Info($"[IdClaims] checked {snapshot.Count} value(s), sent {send.Count}{(full ? " (full set)" : "")}, released {release.Count}, {result.Conflicts.Count} clash(es)");
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
        /// Renumbers this user's clashing elements. Clashes on elements someone else has checked out are reported in
        /// OwnedElsewhere but never block. Blocking is true only for clashes this user's sync would push and that could
        /// not be renumbered; the caller then asks the user (see IdPreSyncFixEvent.AskAboutClashes). Must run outside
        /// any document event.
        /// </summary>
        internal static FixResult EnsureNoClashes(Document doc)
        {
            string docGuid = revit.ModelGuidStorage.GetOrCreate(doc);
            var rules = IdRulesCache.Get(docGuid);
            string baseUrl = config.Config.Instance.BaseUrl;
            if (rules == null || string.IsNullOrEmpty(baseUrl) || !rules.Rules.Any(r => r.Enabled)) return new FixResult();
            return Resolve(doc, docGuid, baseUrl, rules);
        }

        /// <summary>One-paragraph description of a FixResult for dialogs and the status card ("" when nothing happened).</summary>
        internal static string Describe(FixResult fix)
        {
            var parts = new List<string>();
            if (fix.Renumbered > 0)
                parts.Add($"{fix.Renumbered} ID(s) clashed with values other users had already taken and were renumbered.");
            if (fix.OwnedElsewhere.Count > 0)
            {
                var owners = fix.OwnedElsewhere.Select(o => string.IsNullOrEmpty(o.Owner) ? "another user" : o.Owner).Distinct().ToList();
                parts.Add($"{fix.OwnedElsewhere.Count} clashing ID(s) are on elements checked out by {string.Join(", ", owners)} " +
                          "(" + string.Join(", ", fix.OwnedElsewhere.Take(4).Select(o => o.Claim.Value)) + (fix.OwnedElsewhere.Count > 4 ? ", ..." : "") +
                          "). Their Perseus renumbers them when they sync; they don't stop yours.");
            }
            return string.Join("\n", parts);
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
                var snapshot = IdIndexMaintainer.GetClaimsBlocking(doc, docGuid, rules);
                result.TotalClaims = snapshot.Count;
                var check = Check(docGuid, baseUrl, snapshot, FixTimeoutMs); // UI thread (fix event): bounded wait
                if (check == null) { result.ServerUnreachable = true; return result; }
                if (check.Conflicts.Count == 0) return result;

                // Elements someone else has checked out can't be renumbered here and aren't pushed by this sync: note
                // them and leave them to their owner. A clash that survives a renumbering attempt cannot be fixed from
                // here either (group member, missing level, stale vs central...): report it rather than loop on it.
                var fixable = new List<IdClaimItem>();
                foreach (var c in check.Conflicts)
                {
                    string key = c.Parameter + "|" + c.Uid + "|" + c.Value;
                    fix.Taken.Add(IdClashFix.TakenKey(c.Parameter, c.Value));
                    if (IsOwnedElsewhere(doc, c, out string owner))
                    {
                        if (tried.Add(key)) result.OwnedElsewhere.Add((c, owner));
                        continue;
                    }
                    if (tried.Add(key)) fixable.Add(c);
                    else result.Unfixable.Add(c);
                }
                if (fixable.Count == 0) return result;

                foreach (var c in fixable) fix.Force.Add(IdClashFix.ForceKey(c.Parameter, c.ElementId));
                var ids = new HashSet<long>(fixable.Select(c => c.ElementId));
                var run = IdAssigner.Run(doc, docGuid, rules, ids, fix: fix);
                result.Renumbered += run.Assigned;
                if (run.SkippedInGroups > 0) result.Reasons.Add("it is in a group whose ID parameter can't vary per group instance");
                if (run.SkippedNotOwned > 0) result.Reasons.Add("it was changed in central since your last reload (Reload Latest, then sync again)");
                foreach (var t in run.UnresolvedTokens) result.Reasons.Add($"its {t} is empty, so no ID can be built for it");
                if (run.NoPrefixCategories.Count > 0) result.Reasons.Add("its category has no ID prefix configured");
                if (run.MissingOnElement > 0) result.Reasons.Add("it doesn't carry the ID parameter");
                if (run.RolledBack) result.Reasons.Add("Revit refused the change (" + string.Join(", ", run.BlockedBy) + ")");
                if (run.Errors > 0) result.Reasons.Add("the parameter couldn't be written (read-only?)");
                Log.Info($"[IdClaims] round {round + 1}: {fixable.Count} clash(es), renumbered {run.Assigned}");
            }
            return result;
        }
    }
}
