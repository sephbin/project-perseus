using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProjectPerseus.models
{
    /// <summary>
    /// Reference collections of a model, from GET /api/source/&lt;guid&gt;/collections/ (django-perseus
    /// perseus/reference_collections.py; project collections merged with the model's own, disabled ones left out).
    /// </summary>
    public class CollectionsDto
    {
        [JsonProperty("prefix")] public string Prefix { get; set; } = "";
        [JsonProperty("parameter_group")] public string ParameterGroup { get; set; } = "other";
        [JsonProperty("collections")] public List<CollectionDto> Collections { get; set; } = new List<CollectionDto>();
        [JsonProperty("updated_at")] public string UpdatedAt { get; set; }
    }

    /// <summary>
    /// One reference collection: owner elements (LivesOn type / instance, categories, family regex) hold an ordered list
    /// of member elements (TargetKind, categories) in slot parameters (MembersParameter with {n}) or one JSON list
    /// parameter; instances may override member values in OverrideParameter (JSON keyed by slot). *Name properties are
    /// the server's resolved names (naming convention prefix added).
    /// </summary>
    public class CollectionDto
    {
        public const string SlotToken = "{n}";

        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("lives_on")] public string LivesOn { get; set; } = "type";
        [JsonProperty("owner_categories")] public List<string> OwnerCategories { get; set; } = new List<string>();
        [JsonProperty("owner_family")] public string OwnerFamily { get; set; } = "";
        [JsonProperty("storage")] public string Storage { get; set; } = "slots";
        [JsonProperty("members_parameter")] public string MembersParameter { get; set; } = "";
        [JsonProperty("target_categories")] public List<string> TargetCategories { get; set; } = new List<string>();
        [JsonProperty("target_kind")] public string TargetKind { get; set; } = "type";
        [JsonProperty("override_parameter")] public string OverrideParameter { get; set; } = "";
        [JsonProperty("overridable_fields")] public List<string> OverridableFields { get; set; } = new List<string>();
        [JsonProperty("from")] public string From { get; set; } = "";
        [JsonProperty("members_parameter_name")] public string MembersParameterName { get; set; } = "";
        [JsonProperty("override_parameter_name")] public string OverrideParameterName { get; set; } = "";
        /// <summary>Member values listed on the owner (collections/CollectionLists): "Sign Type" of each sign -> pack type.</summary>
        [JsonProperty("lists")] public List<CollectionListDto> Lists { get; set; } = new List<CollectionListDto>();

        [JsonIgnore] public bool OnType => LivesOn != "instance";
        [JsonIgnore] public bool UsesSlots => Storage != "list";
        [JsonIgnore] public bool TargetsTypes => TargetKind != "instance";
        [JsonIgnore] public bool HasOverrides => !string.IsNullOrWhiteSpace(OverrideParameter);
    }

    /// <summary>
    /// One list of a collection: each member's MemberParameter, in slot order, joined with Delimiter into the owner's
    /// OwnerParameter (a type parameter when the collection lives on types). The owner parameter name is used as typed
    /// (no naming prefix): it is often an existing shared parameter.
    /// </summary>
    public class CollectionListDto
    {
        [JsonProperty("member_parameter")] public string MemberParameter { get; set; } = "";
        [JsonProperty("owner_parameter")] public string OwnerParameter { get; set; } = "";
        [JsonProperty("delimiter")] public string Delimiter { get; set; } = ", ";
        [JsonProperty("skip_empty")] public bool SkipEmpty { get; set; } = true;
        [JsonProperty("unique")] public bool Unique { get; set; }

        /// <summary>The owner parameter, or the member parameter's name when left blank.</summary>
        [JsonIgnore] public string Target => string.IsNullOrWhiteSpace(OwnerParameter) ? (MemberParameter ?? "").Trim() : OwnerParameter.Trim();
        [JsonIgnore] public bool IsValid => !string.IsNullOrWhiteSpace(MemberParameter);
    }
}
