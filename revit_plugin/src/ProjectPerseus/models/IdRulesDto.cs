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

        // Changes whenever anything that affects ID generation changes (ignores updated_at).
        [JsonIgnore]
        public string Signature =>
            string.Join("|", Rules.Select(r => $"{r.ParameterName}={r.Pattern}/{r.ApplyToAll}/{r.Enabled}/{string.Join(",", r.Categories)}")) + "#" +
            string.Join("|", CategoryPrefixes.Select(p => $"{p.Category}={p.Prefix}"));

        // Prefix for a Revit category name, or null when the category has no prefix configured.
        public string PrefixFor(string categoryName)
        {
            var hit = CategoryPrefixes.FirstOrDefault(p =>
                string.Equals(p.Category, categoryName, System.StringComparison.OrdinalIgnoreCase));
            return hit?.Prefix;
        }
    }

    public class IdRuleDto
    {
        [JsonProperty("parameter_name")]
        public string ParameterName { get; set; }

        [JsonProperty("pattern")]
        public string Pattern { get; set; }

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
