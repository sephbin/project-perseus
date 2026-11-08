using System;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Something a person has to do about a changed element: review it, approve it, respond to the consultant...
    /// Created by a ReviewEvent carrying it, and closed by a later event with ClosesActionId. Types are free text
    /// (DefaultTypes are only suggestions), so firms can add their own without a code change.
    /// </summary>
    public class ReviewAction
    {
        public const string Done = "Done";
        public const string Cancelled = "Cancelled";

        public static readonly string[] DefaultTypes = { "Review", "Approve", "Respond", "Coordinate", "Update model" };

        public string Id { get; set; }
        public string Type { get; set; }
        /// <summary>A Revit user name, or any name / discipline (e.g. "Structural engineer").</summary>
        public string Assignee { get; set; }

        // Filled in from the creating event by federator/ReviewLog; not stored twice.
        [JsonIgnore] public string CreatedBy { get; set; }
        [JsonIgnore] public DateTime CreatedUtc { get; set; }
        [JsonIgnore] public string ComparisonId { get; set; }
    }
}
