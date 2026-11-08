using System;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>Review statuses. Stored as strings so new ones can be added without breaking old models.</summary>
    public static class ReviewStatus
    {
        public const string Unreviewed = "Unreviewed"; // implicit: no status event yet
        public const string Accepted   = "Accepted";
        public const string Query      = "Query";      // raised with the consultant
        public const string Resolved   = "Resolved";   // a query that has been answered / fixed
        public const string Ignored    = "Ignored";

        public static readonly string[] All = { Unreviewed, Accepted, Query, Resolved, Ignored };
        /// <summary>Statuses a user can set (Unreviewed resets).</summary>
        public static readonly string[] Settable = { Accepted, Query, Resolved, Ignored, Unreviewed };
    }

    /// <summary>
    /// One comment, status change and/or action on a linked element, stored in the host model by
    /// federator/ReviewStore as its own DataStorage element. Events are append-only: nobody edits another user's
    /// event, so reviewing never causes worksharing ownership conflicts. The linked element is referenced
    /// symbolically (link instance UniqueId + linked element UniqueId, with the ElementId and a stable Reference
    /// string as helpers). Any combination of Status / Text / Action / ClosesActionId may be set.
    /// </summary>
    public class ReviewEvent
    {
        public string Id { get; set; }
        public string ComparisonId { get; set; }

        public string LinkInstanceUid { get; set; }
        public string LinkedElementUid { get; set; }
        public long LinkedElementId { get; set; }
        /// <summary>Reference.ConvertToStableRepresentation of the link reference; null for deleted elements.</summary>
        public string StableRef { get; set; }
        /// <summary>ElementChange.Signature when the event was written: lets a status carry over to later comparisons.</summary>
        public string Signature { get; set; }

        public string Author { get; set; }
        public DateTime CreatedUtc { get; set; }

        /// <summary>New status (one of ReviewStatus), or null.</summary>
        public string Status { get; set; }
        /// <summary>Comment text, or null.</summary>
        public string Text { get; set; }
        /// <summary>An action this event opens, or null.</summary>
        public ReviewAction Action { get; set; }
        /// <summary>Id of an open action this event closes, with CloseOutcome ReviewAction.Done / Cancelled.</summary>
        public string ClosesActionId { get; set; }
        public string CloseOutcome { get; set; }

        [JsonIgnore] public long StorageId { get; set; }
    }
}
