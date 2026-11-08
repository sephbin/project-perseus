using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>The folded review state of one linked element (federator/ReviewLog.Fold).</summary>
    public class ElementReviewState
    {
        public string Key { get; set; }
        /// <summary>Latest status that applies to the comparison being viewed; ReviewStatus.Unreviewed when none.</summary>
        public string Status { get; set; } = ReviewStatus.Unreviewed;
        public string StatusBy { get; set; }
        public DateTime? StatusUtc { get; set; }
        /// <summary>The status was set in another comparison and carried over because the element hasn't changed since.</summary>
        public bool StatusCarried { get; set; }
        public string StatusComparisonId { get; set; }

        /// <summary>Actions not yet done or cancelled, oldest first.</summary>
        public List<ReviewAction> OpenActions { get; set; } = new List<ReviewAction>();
        /// <summary>Assignees of the open actions, comma separated ("" when none).</summary>
        public string Assignees { get; set; } = "";
        public int CommentCount { get; set; }

        /// <summary>Every event on the element across all comparisons, oldest first (the thread).</summary>
        public List<ReviewEvent> Events { get; set; } = new List<ReviewEvent>();
    }
}
