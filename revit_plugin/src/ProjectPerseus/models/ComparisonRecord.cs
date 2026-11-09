using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>
    /// One comparison between two versions of a linked model ("from" = Old*, "to" = New*; the newer one is
    /// usually the loaded link), stored in the host model by
    /// federator/ComparisonStore. The header (everything but Changes) is stored as JSON; Changes is stored
    /// separately as gzip JSON and only read when a comparison is opened.
    /// </summary>
    public class ComparisonRecord
    {
        public string Id { get; set; }

        /// <summary>The host model's RevitLinkInstance (UniqueId) the comparison was made through.</summary>
        public string LinkInstanceUid { get; set; }
        public string LinkTitle { get; set; }
        public string LinkPath { get; set; }

        public string OldPath { get; set; }
        public string OldTitle { get; set; }
        public DateTime OldFileTimeUtc { get; set; }

        /// <summary>Labels of the two versions as shown in the version pickers; null in older records (to = live link).</summary>
        public string OldLabel { get; set; }
        public string NewLabel { get; set; }
        public string NewTitle { get; set; }
        public string NewPath { get; set; }
        public string OldVersionGuid { get; set; }
        public string NewVersionGuid { get; set; }

        public DateTime OldSnapshotUtc { get; set; }
        public DateTime NewSnapshotUtc { get; set; }

        public string CreatedBy { get; set; }
        public DateTime CreatedUtc { get; set; }

        public int OldCount { get; set; }
        public int NewCount { get; set; }
        /// <summary>Elements present in both versions (a low share means the file probably isn't an earlier version).</summary>
        public int Matched { get; set; }
        public int Added { get; set; }
        public int Deleted { get; set; }
        public int Modified { get; set; }

        /// <summary>Length tolerance used, in feet.</summary>
        public double Tolerance { get; set; }

        [JsonIgnore] public List<ElementChange> Changes { get; set; }
        /// <summary>The DataStorage element holding this record (set when read from the model).</summary>
        [JsonIgnore] public long StorageId { get; set; }
    }
}
