using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Link Sync proposals rejected that have none of our families to store the rejection on: consultant elements of a
    /// mapped type the user doesn't want ours at (New / Match rejected). Per link instance, shared
    /// (federator/FederatorSettingsStore "linksync-rejected:&lt;link instance uid&gt;").
    /// </summary>
    public class LinkSyncRejections
    {
        public List<string> Sources { get; set; } = new List<string>();
    }
}
