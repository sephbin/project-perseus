using System;
using System.Collections.Generic;

namespace ProjectPerseus.models
{
    /// <summary>
    /// A parameter on one side of a parameter mapping (mapping/ParamDiscovery). Identity: shared GUID, else built-in, else
    /// name + storage (names collide between shared / project / family parameters).
    /// </summary>
    public class ParamRef
    {
        public string Name { get; set; }
        /// <summary>Shared parameter GUID ("D" format), else null.</summary>
        public string Guid { get; set; }
        /// <summary>BuiltInParameter name, else null.</summary>
        public string BuiltIn { get; set; }
        /// <summary>Computed source field (consultant element id, link name), else null.</summary>
        public string Computed { get; set; }
        /// <summary>String / Double / Integer / ElementId.</summary>
        public string Storage { get; set; }
        /// <summary>Text / Number / Measure / Integer / YesNo / Element (mapping/ParamCompat.Kinds).</summary>
        public string Kind { get; set; }
        /// <summary>Spec (ForgeTypeId) id, e.g. autodesk.spec.aec:length-2.0.0; compared for Measure -> Measure.</summary>
        public string Spec { get; set; }
        /// <summary>Spec label for messages ("Length").</summary>
        public string SpecLabel { get; set; }
        public bool IsType { get; set; }
        /// <summary>shared / builtin / project / family / computed (shown in the picker).</summary>
        public string Origin { get; set; }
        /// <summary>Target side: can't be written (read-only / formula / reporting).</summary>
        public bool ReadOnly { get; set; }

        public string Key => (IsType ? "T:" : "I:") + (Computed != null ? "computed:" + Computed : Guid != null ? "guid:" + Guid
                              : BuiltIn != null ? "bip:" + BuiltIn : $"name:{Name}|{Storage}");

        public override string ToString() =>
            $"{Name}  ({(Computed != null ? "computed" : IsType ? "type" : "instance")}, {SpecLabel ?? Kind}{(ReadOnly ? ", read-only" : "")})";
    }

    /// <summary>One consultant parameter -> one local parameter, on a family mapping rule (models/FamilyMapRule.Parameters).</summary>
    public class ParamMap
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
        public bool Enabled { get; set; } = true;
        public ParamRef Source { get; set; }
        public ParamRef Target { get; set; }
        /// <summary>false = changes go through Link Sync approval (default); true = written on each Link Sync run.</summary>
        public bool AlwaysApply { get; set; }
        /// <summary>What happens to the consultant value before it's written (stage 5 phase 4); null = copied as it is.</summary>
        public ParamTransform Transform { get; set; }

        /// <summary>Can be applied: a target and a consultant parameter or a constant.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool Usable => Enabled && Target != null && (Source != null || Transform?.Constant != null || !string.IsNullOrWhiteSpace(Transform?.Expression));
    }

    /// <summary>
    /// Steps between the consultant value and the local one (mapping/ParamTransforms), in this order: constant (instead of the
    /// consultant value) | default for an empty value -> lookup table -> expression -> prefix / suffix. Any step that changes the value
    /// makes it text, then converted to the local parameter like a text parameter (so "2400" can still fill a length).
    /// </summary>
    public class ParamTransform
    {
        /// <summary>Written instead of the consultant value (no consultant parameter needed).</summary>
        public string Constant { get; set; }
        /// <summary>Used when the consultant value is empty.</summary>
        public string Default { get; set; }
        /// <summary>Consultant value -> local value (trimmed, case-insensitive).</summary>
        public List<LookupEntry> Lookup { get; set; } = new List<LookupEntry>();
        /// <summary>true = a value not in the table isn't written (shown as a problem); false = passed on unchanged.</summary>
        public bool LookupStrict { get; set; }
        /// <summary>util/FilterEvaluator expression; names: value (text), number, empty; row["Name"] = any parameter of the same consultant
        /// element (instance first, then type). Without a consultant parameter the expression alone makes the value.</summary>
        public string Expression { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
    }

    public class LookupEntry
    {
        public string From { get; set; }
        public string To { get; set; }
    }
}
