using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    // Response of GET /api/source/<guid>/id-rules/ ("Auto Parameters": ID rules + value rules)
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
            $"pid={PerseusId.ApplyToAll}/{string.Join(",", PerseusId.Categories)}#" + string.Join("|", Rules.Select(r => $"{r.Kind}:{r.ParameterName}={r.Pattern}/{r.ParameterGroup}/{r.ApplyToAll}/{r.Enabled}/{string.Join(",", r.Categories)}/{r.NumberingOrder}/{r.Fill}/{r.Transform}/{r.UniqueWith}/{r.IncrementMode}/{r.ReadOnly}/{r.TemplateSource}")) + "#" +
            string.Join("|", CategoryPrefixes.Select(p => $"{p.Category}={p.Prefix}"));

        /// <summary>At least one enabled unique-ID rule (value rules need no clash check).</summary>
        [JsonIgnore]
        public bool HasIdRules => Rules.Any(r => r.Enabled && !r.IsValue);

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
        public const string KindId = "id";
        public const string KindValue = "value";

        // "id" = unique numbered value (one {Increment|N}); "value" = copied / computed, not unique (no {Increment}).
        [JsonProperty("kind")]
        public string Kind { get; set; } = KindId;

        [JsonIgnore]
        public bool IsValue => string.Equals(Kind, KindValue, System.StringComparison.OrdinalIgnoreCase);

        // Value rules: optional util/FilterEvaluator expression over `value` (rendered pattern) and row["<token>"].
        [JsonProperty("transform")]
        public string Transform { get; set; } = "";

        // Value rules: "always" keeps the value current (overwrites hand edits); "empty" only fills empty values.
        [JsonProperty("fill")]
        public string Fill { get; set; } = "always";

        [JsonIgnore]
        public bool FillOnlyEmpty => string.Equals(Fill, "empty", System.StringComparison.OrdinalIgnoreCase);

        // ID rules: tokens the value only has to be unique with, not printed in it, e.g. "{Element.Door Tag Room Number}"
        // numbers doors D01, D02... separately per room. Blank = unique across the model. See util/IdPattern.UniqueKey.
        [JsonProperty("unique_with")]
        public string UniqueWith { get; set; } = "";

        // ID rules: "keep" (default), "next" (after the highest held, no gap filling) or "fill" (lowest free number).
        // next / fill also minimise the number a new or copied element arrives with. See util/IdNumbering.cs.
        [JsonProperty("increment_mode")]
        public string IncrementMode { get; set; } = "keep";

        // Lock the parameter: created with UserModifiable = false, so it is greyed out in Properties, Type Properties
        // and schedules while Perseus still writes it. Revit fixes this when a parameter is created; switching an
        // existing Perseus parameter needs Audit's conversion (SharedParameterManager.SwitchLock).
        [JsonProperty("read_only")]
        public bool ReadOnly { get; set; }

        // Value rules: render the template held in this parameter instead of a fixed pattern, e.g. "Type.Tag Template"
        // (the type's text "{Level.Name}-{{Element.Mark}}" rendered for each instance). Pattern = default template for
        // elements whose source is empty (blank = cleared). See IdAssigner.PatternFor.
        [JsonProperty("template_source")]
        public string TemplateSource { get; set; } = "";

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

        // Sort keys for Renumber IDs, e.g. "X, -Y"; blank = ElementId order. See util/PositionOrder.cs.
        [JsonProperty("numbering_order")]
        public string NumberingOrder { get; set; } = "";
    }

    public class IdCategoryPrefixDto
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("prefix")]
        public string Prefix { get; set; }
    }
}
