using System;

namespace ProjectPerseus.models
{
    /// <summary>
    /// One Link Sync event (mapping/LinkSyncLog, append-only plugin data in the host model): a decision (a change
    /// approved / rejected), a comment or an assignment, on one of our mapped families (Key = its UniqueId) or on a
    /// consultant element that has none of ours yet (Key = "src:&lt;link uid&gt;|&lt;consultant uid&gt;").
    /// </summary>
    public class LinkSyncEvent
    {
        public const string Decision = "decision", Comment = "comment", Assign = "assign";

        public string Id { get; set; }
        public string LinkUid { get; set; }
        public string Key { get; set; }
        /// <summary>decision / comment / assign.</summary>
        public string Type { get; set; }
        /// <summary>Decision: the mapping/SyncKind name of the change decided.</summary>
        public string Kind { get; set; }
        public bool Approved { get; set; }
        /// <summary>Comment text (decisions may carry a note).</summary>
        public string Text { get; set; }
        /// <summary>Assign: who it's assigned to; empty = unassigned.</summary>
        public string Assignee { get; set; }
        public string Author { get; set; }
        public DateTime Utc { get; set; }
    }
}
