using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// The state of every model element in one version of a (consultant) model, as read by
    /// federator/SnapshotBuilder. Coordinates are the model's own internal coordinates in feet; the federator
    /// moves them into host coordinates with the link instance's current transform at display time.
    /// </summary>
    public class LinkSnapshot
    {
        public const int CurrentFormat = 1;

        public int Format { get; set; } = CurrentFormat;
        public string Title { get; set; }
        public string SourcePath { get; set; }
        /// <summary>File size + last write time of SourcePath when read; the snapshot cache key.</summary>
        public string FileStamp { get; set; }
        public DateTime TakenUtc { get; set; }
        public List<ElementSnapshot> Elements { get; set; } = new List<ElementSnapshot>();
    }

    public class ElementSnapshot
    {
        public string UniqueId { get; set; }
        public long Id { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public string Level { get; set; }
        public string Workset { get; set; }

        /// <summary>Bounding box min / max (x, y, z), null when the element has none.</summary>
        public double[] Min { get; set; }
        public double[] Max { get; set; }

        /// <summary>Location point (3 values) or location curve end points (6 values); null otherwise.</summary>
        public double[] Location { get; set; }

        /// <summary>Solid signature (sums over the element's solids); null when it has no solids.</summary>
        public double? Volume { get; set; }
        public double? Area { get; set; }
        public int FaceCount { get; set; }
        public int EdgeCount { get; set; }

        /// <summary>Instance parameter values by name, then type parameters as "[Type] name".</summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }
}
