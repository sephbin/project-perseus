using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// How the federator bakes changes into views as printable annotations (federator/AnnotationBaker), saved in the
    /// host model per link (federator/FederatorSettingsStore) so the whole team bakes into the same views. Target views =
    /// views whose name (or sheet number) matches ViewRegex, plus IncludedViewUids, minus ExcludedViewUids
    /// (federator/ViewTargets).
    /// </summary>
    public class BakeSettings
    {
        public const string StyleOutline = "Outline";
        public const string StyleRevisionCloud = "RevisionCloud";
        public const string ScopeShown = "Shown";       // rows passing the panel's filters
        public const string ScopeSelected = "Selected";
        public const string ScopeAll = "All";
        public const string MatchViewName = "ViewName";
        public const string MatchSheetNumber = "SheetNumber";

        public string ViewRegex { get; set; } = "";
        /// <summary>What the regex is matched against: MatchViewName or MatchSheetNumber.</summary>
        public string RegexTarget { get; set; } = MatchViewName;
        public List<string> IncludedViewUids { get; set; } = new List<string>();
        public List<string> ExcludedViewUids { get; set; } = new List<string>();

        public string Scope { get; set; } = ScopeShown;
        public string Style { get; set; } = StyleOutline;
        /// <summary>Revision (UniqueId) for revision clouds.</summary>
        public string RevisionUid { get; set; }
        public bool Labels { get; set; } = true;
        public bool LabelStatus { get; set; } = true;
        /// <summary>Remove this link's earlier baked annotations from a view before baking into it.</summary>
        public bool ReplacePrevious { get; set; } = true;
    }
}
