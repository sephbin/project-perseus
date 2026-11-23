using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Effective parameter naming convention for a model, from GET /api/source/&lt;guid&gt;/parameter-naming/ (django-perseus
    /// perseus/parameter_naming.py): prefix, parameter group, base name per key and the resolved (prefixed) names.
    /// </summary>
    public class ParameterNamingDto
    {
        [JsonProperty("prefix")] public string Prefix { get; set; } = "";
        [JsonProperty("parameter_group")] public string ParameterGroup { get; set; } = "other";
        [JsonProperty("names")] public Dictionary<string, string> Names { get; set; } = new Dictionary<string, string>();
        [JsonProperty("resolved")] public Dictionary<string, string> Resolved { get; set; } = new Dictionary<string, string>();
    }
}
