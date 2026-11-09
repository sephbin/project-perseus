using System;

namespace ProjectPerseus.models
{
    /// <summary>
    /// One version of a consultant model that has a snapshot cached on this computer (federator/VersionCatalog), so it
    /// can be compared again without opening or downloading it. Identified by the model's save GUID
    /// (Document.GetDocumentVersion) when known, so the same version reached through the live link or a downloaded
    /// file is recognised as one.
    /// </summary>
    public class LinkVersion
    {
        /// <summary>federator/SnapshotCache key the snapshot is stored under.</summary>
        public string CacheKey { get; set; }
        /// <summary>DocumentVersion.VersionGUID of that save; null when unknown.</summary>
        public string VersionGuid { get; set; }
        /// <summary>DocumentVersion.NumberOfSaves (orders versions); 0 when unknown.</summary>
        public int Saves { get; set; }
        public string Title { get; set; }
        /// <summary>Where it was read from: the link's path (live) or the archived file.</summary>
        public string SourcePath { get; set; }
        public bool FromLiveLink { get; set; }
        /// <summary>Archived file: its last write time; live link: when Perseus read it.</summary>
        public DateTime SavedUtc { get; set; }
        public DateTime SnapshotUtc { get; set; }
        public int Elements { get; set; }
    }
}
