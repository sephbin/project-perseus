using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ProjectPerseus.models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum BatchExportMode { Web, File, IncrementalFile }

    public class BatchInstruction
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("models_to_process")]
        public List<BatchModelInfo> ModelsToProcess { get; set; } = new List<BatchModelInfo>();

        // "Web" posts to Django (default). "File" writes a JSON file to output_dir instead.
        [JsonProperty("export_mode")]
        public BatchExportMode ExportMode { get; set; } = BatchExportMode.Web;

        // Directory for JSON output when export_mode is "File".
        // Defaults to Documents\PerseusExports if omitted.
        [JsonProperty("output_dir")]
        public string OutputDirectory { get; set; }

        public bool IsValid()
        {
            // Window is generous because batch_task.json is now mutated as work progresses
            // (completed models removed, timestamp refreshed). A multi-session run with
            // poison-driven Revit restarts can legitimately stretch over many hours, and
            // the timestamp is bumped on every save so this only fires when a stale file
            // is genuinely abandoned.
            TimeSpan age = DateTime.Now - Timestamp;
            return age.TotalHours < 24;
        }
    }

    public class BatchModelInfo
    {
        // Cloud model (BIM360/ACC) — provide project_guid + model_guid + region
        [JsonProperty("project_guid")]
        public string ProjectGuid { get; set; }

        [JsonProperty("model_guid")]
        public string ModelGuid { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; } = "US";

        // Local file — provide local_path instead of cloud GUIDs
        [JsonProperty("local_path")]
        public string LocalPath { get; set; }

        [JsonProperty("workset_whitelist_regex")]
        public string WorksetWhitelistRegex { get; set; }

        [JsonProperty("workset_blacklist_regex")]
        public string WorksetBlacklistRegex { get; set; }

        // Mirror of perseusCategories in Django parameter_dict.
        // If null or empty, all element categories are exported.
        [JsonProperty("perseus_categories")]
        public List<string> PerseusCategories { get; set; }

        // Mirror of perseusOption_collectConnectedElements in Django parameter_dict.
        [JsonProperty("collect_connected_elements")]
        public bool CollectConnectedElements { get; set; } = false;

        // Count of times this model has crashed Revit during GetUserWorksetInfo (the
        // poison path). Persisted across Revit restarts via batch_task.json so we can
        // give up after a cap instead of restarting forever on a permanently bad model.
        [JsonProperty("attempts")]
        public int Attempts { get; set; } = 0;

        public bool IsLocalFile => !string.IsNullOrEmpty(LocalPath);

        public string DisplayName => IsLocalFile ? Path.GetFileName(LocalPath) : ModelGuid;
    }
}