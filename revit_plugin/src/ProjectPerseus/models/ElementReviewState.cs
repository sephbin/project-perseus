using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>The folded review state of one linked element (federator/ReviewLog.Fold).</summary>
    public class ElementReviewState
    {
        public string Key { get; set; }
        /// <summary>Latest status in the comparison being viewed; ReviewStatus.Unreviewed when none.</summary>
        public string Status { get; set; } = ReviewStatus.Unreviewed;
        public string StatusBy { get; set; }
        public DateTime? StatusUtc { get; set; }
        /// <summary>Every event on the element across all comparisons, oldest first (the comment thread).</summary>
        public List<ReviewEvent> Events { get; set; } = new List<ReviewEvent>();
    }
}
