using System;

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
    }
}
