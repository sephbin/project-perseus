using System;

namespace ProjectPerseus.models
{
    /// <summary>
    /// What one of our mapped families stands for (mapping/MappedFamilyStore, JSON in the v2 record on our instance):
    /// the consultant element, the rule, and the sync baseline Link Sync compares against - the consultant's frame and
    /// type and our frame when Perseus last placed / accepted it - plus the changes the user rejected. Frames are packed
    /// as 12 numbers (origin, X, Y, Z axes; host feet).
    /// </summary>
    public class MappedFamilyData
    {
        public string LinkInstanceUid { get; set; }
        public string SourceUid { get; set; }
        public string SourceId { get; set; }
        public string RuleId { get; set; }
        public string By { get; set; }
        public DateTime Utc { get; set; }
        /// <summary>placed / linked.</summary>
        public string How { get; set; }

        // Baseline (last placed / accepted). Null on records written before Link Sync (v1).
        public string SourceTypeKey { get; set; }
        public double[] SourceFrame { get; set; }
        public double[] OursFrame { get; set; }

        // Rejected changes: not proposed again until the consultant / mapping changes once more.
        /// <summary>Expected frame (consultant + rule) at which a move was rejected.</summary>
        public double[] RejectedExpected { get; set; }
        public string RejectedSourceType { get; set; }
        public string RejectedTargetUid { get; set; }
        public string RejectedReplacement { get; set; }
        public bool RejectedGone { get; set; }
    }
}
