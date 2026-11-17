using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>The family mapping of one consultant model (models/FamilyMapRule per consultant type), shared in the host model.</summary>
    public class FamilyMapping
    {
        public List<FamilyMapRule> Rules { get; set; } = new List<FamilyMapRule>();
        /// <summary>Consultant categories turned off in the mapper's category filter (not worth mapping).</summary>
        public List<string> HiddenCategories { get; set; } = new List<string>();
        /// <summary>The consultant model's name when last saved (link type name), so the mapping can be named without the link.</summary>
        public string ModelName { get; set; }
    }
}
