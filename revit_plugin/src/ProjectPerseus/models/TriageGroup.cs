using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>Changes that look alike (federator/ChangeTriage): same category, type, kind and pattern of change.</summary>
    public class TriageGroup
    {
        public string Key { get; set; }
        public string Category { get; set; }
        /// <summary>"Family : Type", or "" when grouping by category only.</summary>
        public string Type { get; set; }
        public ChangeKind Kind { get; set; }
        /// <summary>What changed, e.g. "Parameters: Comments, Mark" or "Moved ≤ 5 mm · Type".</summary>
        public string Description { get; set; }
        /// <summary>Changed parameter names shared by the group (sorted).</summary>
        public List<string> Parameters { get; set; } = new List<string>();
        public List<ElementChange> Changes { get; set; } = new List<ElementChange>();
        /// <summary>Most common before → after values per changed parameter.</summary>
        public List<ParameterTransition> Transitions { get; set; } = new List<ParameterTransition>();
    }

    public class ParameterTransition
    {
        public string Parameter { get; set; }
        public string Old { get; set; }
        public string New { get; set; }
        public int Count { get; set; }
        /// <summary>How many different before → after pairs this parameter has in the group.</summary>
        public int Distinct { get; set; }
    }
}
