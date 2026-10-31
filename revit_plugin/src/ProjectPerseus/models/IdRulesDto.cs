using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    // Response of GET /api/source/<guid>/id-rules/
    public class IdRulesDto
    {
        [JsonProperty("rules")]
        public List<IdRuleDto> Rules { get; set; } = new List<IdRuleDto>();

        [JsonProperty("category_prefixes")]
        public List<IdCategoryPrefixDto> CategoryPrefixes { get; set; } = new List<IdCategoryPrefixDto>();

        // Which categories receive the built-in Perseus_ID parameter (default: all).
        [JsonProperty("perseus_id")]
        public PerseusIdDto PerseusId { get; set; } = new PerseusIdDto();

        // Changes whenever anything that affects ID generation changes (ignores updated_at).
        [JsonIgnore]
        public string Signature =>
            $"pid={PerseusId.ApplyToAll}/{string.Join(",", PerseusId.Categories)}#" + string.Join("|", Rules.Select(r => $"{r.ParameterName}={r.Pattern}/{r.ParameterGroup}/{r.ApplyToAll}/{r.Enabled}/{string.Join(",", r.Categories)}")) + "#" +
            string.Join("|", CategoryPrefixes.Select(p => $"{p.Category}={p.Prefix}"));

        // Prefix for a Revit category name, or null when the category has no prefix configured.
        public string PrefixFor(string categoryName)
        {
            var hit = CategoryPrefixes.FirstOrDefault(p =>
                string.Equals(p.Category, categoryName, System.StringComparison.OrdinalIgnoreCase));
            return hit?.Prefix;
        }
    }

    public class PerseusIdDto
    {
        [JsonProperty("apply_to_all")]
        public bool ApplyToAll { get; set; } = true;

        [JsonProperty("categories")]
        public List<string> Categories { get; set; } = new List<string>();

        public bool AppliesTo(string categoryName) =>
            ApplyToAll || Categories.Any(c => string.Equals(c, categoryName, System.StringComparison.OrdinalIgnoreCase));
    }

    public class IdRuleDto
    {
        [JsonProperty("parameter_name")]
        public string ParameterName { get; set; }

        [JsonProperty("pattern")]
        public string Pattern { get; set; }

        // Key such as "identity_data" or "other"; see ID_PARAMETER_GROUPS in Django views.py.
        [JsonProperty("parameter_group")]
        public string ParameterGroup { get; set; } = "identity_data";

        [JsonProperty("apply_to_all")]
        public bool ApplyToAll { get; set; } = true;

        [JsonProperty("categories")]
        public List<string> Categories { get; set; } = new List<string>();

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;
    }

    public class IdCategoryPrefixDto
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("prefix")]
        public string Prefix { get; set; }
    }
}
