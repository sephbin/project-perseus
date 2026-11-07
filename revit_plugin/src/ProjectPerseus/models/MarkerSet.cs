using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ProjectPerseus.models
{
    /// <summary>
    /// A parameter marker set: which elements to look at (categories + optional filter expression), which parameter
    /// to read, and a legend of value → ring colour / icon / label. Stored in markers/MarkerSetStore's library and
    /// evaluated by markers/MarkerEngine against the active view.
    /// </summary>
    public class MarkerSet
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New marker set";
        public bool Enabled { get; set; } = true;

        /// <summary>BuiltInCategory names, e.g. "OST_Doors". Empty = no elements.</summary>
        public List<string> Categories { get; set; } = new List<string>();

        /// <summary>Also mark matching elements inside linked Revit models visible in the view.</summary>
        public bool IncludeLinks { get; set; } = true;

        /// <summary>Optional util/FilterEvaluator expression, e.g. <c>"Double" in row["Family"]</c>. Empty = all.</summary>
        public string Filter { get; set; } = "";

        /// <summary>
        /// The set's parameters (instance parameter, else type parameter, else a pseudo field such as Family, Type,
        /// Category, Level). Entries pick one of these; Auto-fill reads values from each. The first is used by entries
        /// that don't name their own.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> Parameters { get; set; } = new List<string>();

        /// <summary>The first non-blank default parameter (trimmed, "" if none). Setting it replaces the first.</summary>
        [JsonIgnore]
        public string Parameter
        {
            get => Parameters?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
            set
            {
                if (Parameters == null) Parameters = new List<string>();
                if (Parameters.Count == 0) Parameters.Add(value ?? "");
                else Parameters[0] = value ?? "";
            }
        }

        /// <summary>The non-blank default parameters, trimmed and without duplicates, in order.</summary>
        [JsonIgnore]
        public List<string> DefaultParameters =>
            (Parameters ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Sets saved before there could be several default parameters had a single "Parameter".
        [JsonProperty("Parameter", NullValueHandling = NullValueHandling.Ignore)]
        private string LegacyParameter
        {
            get => null;
            set { if (!string.IsNullOrWhiteSpace(value) && DefaultParameters.Count == 0) Parameter = value; }
        }

        /// <summary>
        /// Checked in order, per parameter: among the entries reading the same parameter the first match marks the
        /// element; entries reading different parameters mark independently (one element can get several marks).
        /// </summary>
        public List<Entry> Entries { get; set; } = new List<Entry>();

        /// <summary>GreaterThan..LessOrEqual compare numbers: lengths etc. in the host model's units, else the first number in the text.</summary>
        public enum MatchKind { Equals, Contains, Regex, AnyValue, Empty, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual }

        /// <summary>
        /// Ring / Square: around the element's projected box (icon on it). Outline: the element's own view
        /// geometry traced thick over it (icon at its top-right). IconOnly: icon (or a dot) on the centre.
        /// </summary>
        public enum DisplayKind { Ring, Square, Outline, IconOnly }

        public class Entry
        {
            public bool Enabled { get; set; } = true;
            public string Label { get; set; } = "";

            /// <summary>Parameter this entry reads (one of the set's Parameters); "" = the set's first.</summary>
            public string Parameter { get; set; } = "";

            /// <summary>How the mark is drawn (ring / square around the element, its view outline, or icon only).</summary>
            [JsonConverter(typeof(StringEnumConverter))]
            public DisplayKind Display { get; set; } = DisplayKind.Ring;

            // Sets saved by the build that only had a ring on/off switch: ShowRing=false meant icon only.
            [JsonProperty("ShowRing", NullValueHandling = NullValueHandling.Ignore)]
            private bool? LegacyShowRing
            {
                get => null;
                set { if (value == false) Display = DisplayKind.IconOnly; }
            }

            [JsonConverter(typeof(StringEnumConverter))]
            public MatchKind Match { get; set; } = MatchKind.Equals;

            public string Value { get; set; } = "";
            /// <summary>"#RRGGBB".</summary>
            public string Color { get; set; } = "#E6007E";
            /// <summary>Key in ui/MarkerIcons ("" = no icon).</summary>
            public string Icon { get; set; } = "";
        }

        /// <summary>The parameter an entry actually reads (its own, else the set's), trimmed.</summary>
        public string ParameterFor(Entry e) =>
            (string.IsNullOrWhiteSpace(e.Parameter) ? Parameter ?? "" : e.Parameter).Trim();

        public MarkerSet Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            var copy = JsonConvert.DeserializeObject<MarkerSet>(json);
            copy.Id = Guid.NewGuid().ToString("N");
            return copy;
        }
    }
}
