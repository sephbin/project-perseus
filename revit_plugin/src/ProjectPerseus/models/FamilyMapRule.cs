using System;

namespace ProjectPerseus.models
{
    /// <summary>
    /// One consultant type -> one of our family types (mapping/FamilyPlacer), with the 1:1 offset (mm, in the
    /// consultant element's axes) and rotation (degrees about its X, Y, Z) and the workset our families go on.
    /// </summary>
    public class FamilyMapRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public bool Enabled { get; set; } = true;
        // Consultant type (as in the link): category name, family name, type name.
        public string SourceCategory { get; set; }
        public string SourceFamily { get; set; }
        public string SourceType { get; set; }
        // Our family type (host FamilySymbol).
        public string TargetSymbolUid { get; set; }
        public string TargetName { get; set; }
        public double[] OffsetMm { get; set; } = new double[3];
        public double[] RotationDeg { get; set; } = new double[3];
        /// <summary>Workset for placed families (name, so it survives detach / recreate); empty = active workset.</summary>
        public string Workset { get; set; } = "";
        /// <summary>Link Sync: how far ours may be from the expected position (horizontally / vertically, mm) before it's flagged.</summary>
        public double ToleranceXyMm { get; set; } = 25;
        public double ToleranceZMm { get; set; } = 25;
        public string Author { get; set; }
        public DateTime SavedUtc { get; set; }

        public string SourceKey => Key(SourceCategory, SourceFamily, SourceType);

        public static string Key(string category, string family, string type) => $"{category ?? ""}|{family ?? ""}|{type ?? ""}";
    }
}
