using System.Collections.Generic;
using Newtonsoft.Json;
using ProjectPerseus.models;

namespace ProjectPerseus.violations
{
    internal class ViolationSettings
    {
        [JsonProperty("protected_element_ids")]
        public List<string> ProtectedElementIds { get; set; } = new List<string>();

        [JsonProperty("protected_categories")]
        public List<string> ProtectedCategories { get; set; } = new List<string>();

        [JsonProperty("protected_filters")]
        public List<ProtectedFilter> ProtectedFilters { get; set; } = new List<ProtectedFilter>();

        [JsonProperty("default_on_edit_style")]
        public string DefaultOnEditStyle { get; set; } = "log";

        [JsonProperty("default_on_sync_style")]
        public string DefaultOnSyncStyle { get; set; } = "log";

        [JsonProperty("enforce_element_deletion_edit")]
        public string EnforceElementDeletionEdit { get; set; } = "inherit";

        [JsonProperty("enforce_ungroup_edit")]
        public string EnforceUngroupEdit { get; set; } = "inherit";

        [JsonProperty("enforce_link_unload_edit")]
        public string EnforceLinkUnloadEdit { get; set; } = "log";

        [JsonProperty("enforce_warnings_edit")]
        public string EnforceWarningsEdit { get; set; } = "log";

        [JsonProperty("enforce_unpin_edit")]
        public string EnforceUnpinEdit { get; set; } = "log";

        [JsonProperty("enforce_element_deletion_sync")]
        public string EnforceElementDeletionSync { get; set; } = "inherit";

        [JsonProperty("enforce_ungroup_sync")]
        public string EnforceUngroupSync { get; set; } = "inherit";

        [JsonProperty("enforce_link_unload_sync")]
        public string EnforceLinkUnloadSync { get; set; } = "log";

        [JsonProperty("enforce_warnings_sync")]
        public string EnforceWarningsSync { get; set; } = "log";

        [JsonProperty("enforce_unpin_sync")]
        public string EnforceUnpinSync { get; set; } = "log";

        internal string ResolveSyncStyle(string actionType)
        {
            string raw;
            switch (actionType)
            {
                case ActionType.ElementDeleted:   raw = EnforceElementDeletionSync; break;
                case ActionType.Ungroup:          raw = EnforceUngroupSync;         break;
                case ActionType.LinkUnload:       raw = EnforceLinkUnloadSync;      break;
                case ActionType.WarningDismissed: raw = EnforceWarningsSync;        break;
                case ActionType.Unpin:            raw = EnforceUnpinSync;           break;
                default:                          raw = "log";                      break;
            }
            return raw == "inherit" ? DefaultOnSyncStyle : raw;
        }

        internal string ResolveEditStyle(string actionType)
        {
            string raw;
            switch (actionType)
            {
                case ActionType.ElementDeleted:   raw = EnforceElementDeletionEdit; break;
                case ActionType.Ungroup:          raw = EnforceUngroupEdit;         break;
                case ActionType.LinkUnload:       raw = EnforceLinkUnloadEdit;      break;
                case ActionType.WarningDismissed: raw = EnforceWarningsEdit;        break;
                case ActionType.Unpin:            raw = EnforceUnpinEdit;           break;
                default:                          raw = "log";                      break;
            }
            return raw == "inherit" ? DefaultOnEditStyle : raw;
        }
    }

    internal class ProtectedFilter
    {
        [JsonProperty("field")]          public string Field       { get; set; }
        [JsonProperty("condition")]      public string Condition   { get; set; }
        [JsonProperty("value")]          public string Value       { get; set; }
        [JsonProperty("on_edit_style")]  public string OnEditStyle { get; set; } = "inherit";
        [JsonProperty("on_sync_style")]  public string OnSyncStyle { get; set; } = "inherit";
    }
}
