using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    public class BatchInstruction
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("models_to_process")]
        public List<BatchModelInfo> ModelsToProcess { get; set; } = new List<BatchModelInfo>();

        public bool IsValid()
        {
            // Check if the file is older than 4 hours
            TimeSpan age = DateTime.Now - Timestamp;
            return age.TotalHours < 4;
        }
    }

    public class BatchModelInfo
    {
        [JsonProperty("project_guid")]
        public string ProjectGuid { get; set; }

        [JsonProperty("model_guid")]
        public string ModelGuid { get; set; }
     
        [JsonProperty("region")]
        public string Region { get; set; } = "US";
        // Optional Regex strings for worksets
        [JsonProperty("workset_whitelist_regex")]
        public string WorksetWhitelistRegex { get; set; }

        [JsonProperty("workset_blacklist_regex")]
        public string WorksetBlacklistRegex { get; set; }
    }
}