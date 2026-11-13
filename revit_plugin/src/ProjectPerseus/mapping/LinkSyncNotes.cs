using System;
using System.Collections.Generic;
using System.Linq;
using ProjectPerseus.models;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Pure fold of Link Sync events (models/LinkSyncEvent) into what the panel shows per item: decisions (newest first),
    /// the comment thread, the current assignee. An item's events are those on our family's UniqueId and on its
    /// consultant element ("src:&lt;link&gt;|&lt;uid&gt;"), so notes made before ours was placed / re-placed / repointed
    /// stay with it. Tested in ProjectPerseusTests/LinkSyncPlannerTest.cs.
    /// </summary>
    public class LinkSyncNotes
    {
        public List<LinkSyncEvent> Decisions = new List<LinkSyncEvent>();   // newest first
        public List<LinkSyncEvent> Comments = new List<LinkSyncEvent>();    // oldest first
        public string Assignee = "";

        public LinkSyncEvent LastDecision => Decisions.FirstOrDefault();
        public LinkSyncEvent LastComment => Comments.LastOrDefault();

        public static string SourceKey(string linkUid, string sourceUid) => $"src:{linkUid}|{sourceUid}";

        /// <summary>Consultant uids of this link ever linked to one of ours (connect events).</summary>
        public static HashSet<string> Connected(IEnumerable<LinkSyncEvent> events, string linkUid)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string prefix = SourceKey(linkUid, "");
            foreach (var e in events ?? Enumerable.Empty<LinkSyncEvent>())
                if (e?.Type == LinkSyncEvent.Connect && e.Key != null && e.Key.StartsWith(prefix, StringComparison.Ordinal)) set.Add(e.Key.Substring(prefix.Length));
            return set;
        }

        public static LinkSyncEvent ConnectEvent(string linkUid, string sourceUid, string oursUid, string user) => new LinkSyncEvent
        {
            LinkUid = linkUid, Key = SourceKey(linkUid, sourceUid), Type = LinkSyncEvent.Connect, Text = oursUid, Author = user, Utc = DateTime.UtcNow,
        };

        /// <summary>Keys an item's events can be under: ours, the consultant element, its replacement.</summary>
        public static IEnumerable<string> Keys(SyncItem i)
        {
            if (!string.IsNullOrEmpty(i.OursUid)) yield return i.OursUid;
            if (!string.IsNullOrEmpty(i.SourceUid)) yield return SourceKey(i.LinkUid, i.SourceUid);
            if (!string.IsNullOrEmpty(i.NewSourceUid)) yield return SourceKey(i.LinkUid, i.NewSourceUid);
        }

        /// <summary>The key new events for an item go under: ours when there is one, else the consultant element.</summary>
        public static string KeyFor(SyncItem i) => !string.IsNullOrEmpty(i.OursUid) ? i.OursUid : SourceKey(i.LinkUid, i.NewSourceUid ?? i.SourceUid);

        /// <summary>Events grouped by key (each key's list oldest first).</summary>
        public static Dictionary<string, List<LinkSyncEvent>> Index(IEnumerable<LinkSyncEvent> events)
        {
            var d = new Dictionary<string, List<LinkSyncEvent>>(StringComparer.Ordinal);
            foreach (var e in events.Where(e => !string.IsNullOrEmpty(e?.Key)).OrderBy(e => e.Utc))
            {
                if (!d.TryGetValue(e.Key, out var l)) d[e.Key] = l = new List<LinkSyncEvent>();
                l.Add(e);
            }
            return d;
        }

        public static LinkSyncNotes For(SyncItem i, Dictionary<string, List<LinkSyncEvent>> index)
        {
            var all = Keys(i).Distinct().SelectMany(k => index.TryGetValue(k, out var l) ? l : Enumerable.Empty<LinkSyncEvent>())
                             .GroupBy(e => e.Id ?? Guid.NewGuid().ToString()).Select(g => g.First()).OrderBy(e => e.Utc).ToList();
            var n = new LinkSyncNotes();
            foreach (var e in all)
            {
                switch (e.Type)
                {
                    case LinkSyncEvent.Decision:
                        n.Decisions.Insert(0, e);
                        if (!string.IsNullOrWhiteSpace(e.Text)) n.Comments.Add(e);
                        break;
                    case LinkSyncEvent.Comment: n.Comments.Add(e); break;
                    case LinkSyncEvent.Assign: n.Assignee = e.Assignee ?? ""; break;
                }
            }
            return n;
        }

        /// <summary>"Accepted (moved) · A.Butler · 13 Nov 14:02" for a decision event.</summary>
        public static string Describe(LinkSyncEvent d, bool withWhen = true)
        {
            if (d == null) return "";
            string kind = Enum.TryParse<SyncKind>(d.Kind ?? "", out var k) ? LinkSyncPlanner.Label(k).ToLowerInvariant() : d.Kind;
            return $"{(d.Approved ? "Accepted" : "Rejected")} ({kind}) · {d.Author}" + (withWhen ? $" · {d.Utc.ToLocalTime():d MMM HH:mm}" : "");
        }
    }
}
