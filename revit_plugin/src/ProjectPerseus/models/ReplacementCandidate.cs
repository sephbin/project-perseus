namespace ProjectPerseus.models
{
    /// <summary>An added element that probably replaced a deleted one (federator/ReplacementMatcher).</summary>
    public class ReplacementCandidate
    {
        public string UniqueId { get; set; }
        public long Id { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        /// <summary>0..1; higher is a better match.</summary>
        public double Score { get; set; }
        /// <summary>Why it matched, e.g. "same type, overlaps 85 %".</summary>
        public string Reason { get; set; }
    }
}
