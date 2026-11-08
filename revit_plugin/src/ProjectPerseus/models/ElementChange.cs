using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    public enum ChangeKind { Added, Deleted, Modified }

    [Flags]
    public enum ChangeAspect
    {
        None       = 0,
        Moved      = 1,
        Geometry   = 2,
        Type       = 4,
        Level      = 8,
        Parameters = 16,
        Category   = 32,
        Workset    = 64,
    }

    public class ParameterChange
    {
        public string Name { get; set; }
        public string Old { get; set; }   // null = parameter didn't exist in the old version
        public string New { get; set; }   // null = parameter no longer exists
    }

    /// <summary>
    /// One element that differs between two versions of a linked model (federator/LinkDiff). Identity and
    /// descriptive fields come from the new version, or the old one for deleted elements. Boxes are in the
    /// link's internal coordinates (feet).
    /// </summary>
    public class ElementChange
    {
        public ChangeKind Kind { get; set; }
        public ChangeAspect Aspects { get; set; }

        public string UniqueId { get; set; }
        public long Id { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public string Level { get; set; }

        /// <summary>Old family/type/level etc. when those changed ("Family : Type" style, for the review grid).</summary>
        public string OldType { get; set; }
        public string OldLevel { get; set; }

        /// <summary>Distance the element's centre moved, in feet (0 unless Moved).</summary>
        public double MoveDistance { get; set; }

        public double[] OldMin { get; set; }
        public double[] OldMax { get; set; }
        public double[] NewMin { get; set; }
        public double[] NewMax { get; set; }

        public List<ParameterChange> ParameterChanges { get; set; } = new List<ParameterChange>();
    }
}
