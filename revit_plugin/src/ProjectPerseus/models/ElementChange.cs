using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>Replaced = a deleted element linked to the new element that replaces it (federator/ReplacementLinks; panel only, never stored).</summary>
    public enum ChangeKind { Added, Deleted, Modified, Replaced }

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
        /// <summary>Oriented boxes (ElementSnapshot.Obb) of the old / new element, when tighter than the boxes above.</summary>
        public double[] OldObb { get; set; }
        public double[] NewObb { get; set; }

        public List<ParameterChange> ParameterChanges { get; set; } = new List<ParameterChange>();

        /// <summary>Movement of the element's centre (dx, dy, dz in feet, link coordinates) when Moved.</summary>
        public double[] MoveVector { get; set; }
        /// <summary>Location curve length (feet) in each version, for curve-driven elements.</summary>
        public double? OldLength { get; set; }
        public double? NewLength { get; set; }
        /// <summary>Solid volume (cubic feet) in each version, when the element has solids.</summary>
        public double? OldVolume { get; set; }
        public double? NewVolume { get; set; }

        /// <summary>
        /// Fingerprint of the element's state in the current version ("deleted:" + old state for deleted elements).
        /// A review status carries over to a later comparison when the fingerprint is unchanged (federator/ReviewLog).
        /// </summary>
        public string Signature { get; set; }

        /// <summary>Replaced only: the deleted element (UniqueId / Id are the new element's).</summary>
        public string OldUniqueId { get; set; }
        public long OldId { get; set; }

        /// <summary>Deleted elements only: added elements that probably replaced it, best first (federator/ReplacementMatcher).</summary>
        public List<ReplacementCandidate> Replacements { get; set; }
    }
}
