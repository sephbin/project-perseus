using System;
using System.Collections.Generic;
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

        /// <summary>Instance parameter, else type parameter, else a pseudo field (Family, Type, Category, Level...).</summary>
        public string Parameter { get; set; } = "";

        /// <summary>Checked in order; the first matching entry marks the element.</summary>
        public List<Entry> Entries { get; set; } = new List<Entry>();

        public enum MatchKind { Equals, Contains, Regex, AnyValue, Empty }

        public class Entry
        {
            public bool Enabled { get; set; } = true;
            public string Label { get; set; } = "";

            [JsonConverter(typeof(StringEnumConverter))]
            public MatchKind Match { get; set; } = MatchKind.Equals;

            public string Value { get; set; } = "";
            /// <summary>"#RRGGBB".</summary>
            public string Color { get; set; } = "#E6007E";
            /// <summary>Key in ui/MarkerIcons ("" = no icon).</summary>
            public string Icon { get; set; } = "";
        }

        public MarkerSet Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            var copy = JsonConvert.DeserializeObject<MarkerSet>(json);
            copy.Id = Guid.NewGuid().ToString("N");
            return copy;
        }
    }
}
