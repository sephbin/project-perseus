using System;
using System.Collections.Generic;

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
        /// <summary>UniqueId of the family the record was written on: a copy of it (Revit copies the entity) has another one.</summary>
        public string OursUid { get; set; }
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
        /// <summary>
        /// A hand edit (= rejecting the linked position) taken back for a decision: the local family's frame at that moment.
        /// While it hasn't moved since, the planner lists it as Moved (to decide) instead of Kept (edited by hand).
        /// </summary>
        public double[] HandEditReopened { get; set; }

        // Parameter mapping (stage 5), per ParamMap.Id: the consultant value (ParamCompat.SourceCanon) when it was last written /
        // accepted, what was written (ParamCompat.LocalCanon: a different local value since = edited by hand), and a consultant
        // value whose change was rejected.
        public Dictionary<string, string> ParamBaseline { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ParamWritten { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ParamRejected { get; set; } = new Dictionary<string, string>();
    }
}
