using System;

namespace ProjectPerseus.models
{
    /// <summary>One version of a cloud model (ACC / BIM 360 / Forma) as listed by the APS Data Management API.</summary>
    public class CloudVersion
    {
        /// <summary>Version URN, e.g. "urn:adsk.wipprod:fs.file:vf.abc?version=12".</summary>
        public string Id { get; set; }
        public int Number { get; set; }
        public string Name { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string User { get; set; }
        /// <summary>extension.type, e.g. "versions:autodesk.bim360:C4RModel" (cloud workshared) or ":File".</summary>
        public string ExtensionType { get; set; }
        /// <summary>OSS object of the file ("urn:adsk.objects:os.object:bucket/key"); null when there's none.</summary>
        public string StorageId { get; set; }

        public bool IsCloudWorkshared => ExtensionType != null && ExtensionType.EndsWith("C4RModel", StringComparison.OrdinalIgnoreCase);
    }
}
