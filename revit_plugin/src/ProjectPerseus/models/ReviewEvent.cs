using System;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>Review statuses. Stored as strings so new ones can be added without breaking old models.</summary>
    public static class ReviewStatus
    {
        public const string Unreviewed = "Unreviewed"; // implicit: no status event yet
        public const string Accepted   = "Accepted";
        public const string Query      = "Query";      // raise with the consultant
        public const string Ignored    = "Ignored";

        public static readonly string[] All = { Unreviewed, Accepted, Query, Ignored };
    }

    /// <summary>
    /// One comment and/or status change on a linked element, stored in the host model by federator/ReviewStore as
    /// its own DataStorage element. Events are append-only: nobody edits another user's event, so reviewing never
    /// causes worksharing ownership conflicts. The linked element is referenced symbolically (link instance
    /// UniqueId + linked element UniqueId, with the ElementId and a stable Reference string as helpers).
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

        public string Author { get; set; }
        public DateTime CreatedUtc { get; set; }

        /// <summary>New status (one of ReviewStatus), or null for a comment only.</summary>
        public string Status { get; set; }
        public string Text { get; set; }

        [JsonIgnore] public long StorageId { get; set; }
    }
}
