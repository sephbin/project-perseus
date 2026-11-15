using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;
using StorageType = Autodesk.Revit.DB.StorageType;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Parameter mapping values on real elements (stage 5 phase 3, API context): find a mapped parameter on the consultant
    /// element / its type or on ours / our type, read the consultant value, work out the local value it becomes (mapping/
    /// ParamCompat.TargetValue, display values converted with the host's units) and write it.
    /// </summary>
    internal static class ParamValues
    {
        /// <summary>The parameter on an element (type parameters: on its type) by shared GUID, built-in, else name + storage.</summary>
        internal static Parameter Find(Element el, ParamRef r)
        {
            if (el == null || r == null || r.Computed != null) return null;
            var target = r.IsType ? el.Document.GetElement(el.GetTypeId()) ?? el : el;
            try
            {
                if (r.Guid != null && Guid.TryParse(r.Guid, out var g)) { var p = target.get_Parameter(g); if (p != null) return p; }
                if (r.BuiltIn != null && Enum.TryParse<BuiltInParameter>(r.BuiltIn, out var bip)) { var p = target.get_Parameter(bip); if (p != null) return p; }
                return target.GetParameters(r.Name).FirstOrDefault(p => r.Storage == null || p.StorageType.ToString() == r.Storage) ?? target.LookupParameter(r.Name);
            }
            catch { return null; }
        }

        /// <summary>The consultant value of a mapping on one consultant element.</summary>
        internal static SourceValue Read(Element source, ParamRef r, string linkName)
        {
            if (source == null || r == null) return new SourceValue();
            if (r.Computed == ParamDiscovery.ComputedElementId) return new SourceValue { HasValue = true, Text = source.Id.GetIdValue().ToString() };
            if (r.Computed == ParamDiscovery.ComputedLinkName) return new SourceValue { HasValue = !string.IsNullOrEmpty(linkName), Text = linkName };
            var p = Find(source, r);
            return p == null ? new SourceValue() : ParamDiscovery.Value(source.Document, p, r);
        }

        /// <summary>A planner state for one mapping on one of ours (null = the local parameter isn't there / can't be written).</summary>
        internal static ParamState State(Document doc, Element ours, Element source, ParamMap m, string linkName)
        {
            var p = Find(ours, m.Target);
            if (p == null || p.IsReadOnly) return null;
            var v = Read(source, m.Source, linkName);
            var row = ParamTransforms.UsesRow(m.Transform) ? ParamDiscovery.Row(source, linkName) : null;
            var tv = ParamTransforms.TargetValue(m.Source, m.Transform, m.Target, v, row);
            return new ParamState
            {
                MapId = m.Id, Name = m.Target.Name, AlwaysApply = m.AlwaysApply,
                Src = ParamTransforms.SourceCanon(m.Source, m.Transform, v, row),
                Expected = tv.Ok ? Canon(doc, p, tv) : null,
                Local = LocalCanon(p),
                ExpectedShown = tv.Ok ? tv.Shown : tv.Problem,
                LocalShown = Shown(p),
            };
        }

        internal static string LocalCanon(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String: return ParamCompat.LocalCanon("String", p.AsString(), null, null);
                case StorageType.Integer: return ParamCompat.LocalCanon("Integer", null, p.AsInteger(), null);
                case StorageType.Double: return ParamCompat.LocalCanon("Double", null, null, p.AsDouble());
                default: return "?";
            }
        }

        private static string Shown(Parameter p)
        {
            try
            {
                string s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                return string.IsNullOrEmpty(s) ? "(empty)" : s;
            }
            catch { return "?"; }
        }

        /// <summary>The value to write, in the parameter's storage (display numbers to internal units with the host's units).</summary>
        private static bool Resolve(Document doc, Parameter p, TypedValue tv, out string text, out int i, out double d)
        {
            text = null; i = 0; d = 0;
            switch (p.StorageType)
            {
                case StorageType.String: text = tv.Text ?? tv.Shown ?? ""; return true;
                case StorageType.Integer:
                    if (tv.Int.HasValue) { i = tv.Int.Value; return true; }
                    if (tv.Internal.HasValue || tv.Display.HasValue) { i = (int)Math.Round(tv.Internal ?? tv.Display.Value); return true; }
                    return false;
                case StorageType.Double:
                    if (tv.Internal.HasValue) { d = tv.Internal.Value; return true; }
                    double? x = tv.Display ?? (tv.Int.HasValue ? tv.Int.Value : (double?)null);
                    if (!x.HasValue) return false;
                    d = x.Value;
                    try
                    {
                        var spec = p.Definition.GetDataType();
                        if (spec != null && UnitUtils.IsMeasurableSpec(spec))
                            d = UnitUtils.ConvertToInternalUnits(x.Value, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
                    }
                    catch { }
                    return true;
                default: return false;
            }
        }

        private static string Canon(Document doc, Parameter p, TypedValue tv) =>
            !Resolve(doc, p, tv, out var t, out var i, out var d) ? null
            : ParamCompat.LocalCanon(p.StorageType == StorageType.String ? "String" : p.StorageType == StorageType.Integer ? "Integer" : "Double", t, i, d);

        /// <summary>Writes one mapping on one of ours (inside the caller's transaction). Returns (consultant canon, written canon) or null.</summary>
        internal static Tuple<string, string> Write(Document doc, Element ours, Element source, ParamMap m, string linkName, out string problem)
        {
            problem = null;
            var p = Find(ours, m.Target);
            if (p == null) { problem = $"{m.Target?.Name} isn't on the local family"; return null; }
            if (p.IsReadOnly) { problem = $"{m.Target.Name} is read-only"; return null; }
            var v = Read(source, m.Source, linkName);
            var row = ParamTransforms.UsesRow(m.Transform) ? ParamDiscovery.Row(source, linkName) : null;
            var tv = ParamTransforms.TargetValue(m.Source, m.Transform, m.Target, v, row);
            if (!tv.Ok) { problem = $"{m.Target.Name}: {tv.Problem}"; return null; }
            if (!Resolve(doc, p, tv, out var text, out var i, out var d)) { problem = $"{m.Target.Name}: doesn't fit"; return null; }
            bool ok;
            switch (p.StorageType)
            {
                case StorageType.String: ok = p.AsString() == text || p.Set(text); break;
                case StorageType.Integer: ok = p.AsInteger() == i || p.Set(i); break;
                default: ok = Math.Abs(p.AsDouble() - d) < 1e-9 || p.Set(d); break;
            }
            if (!ok) { problem = $"{m.Target.Name}: Revit refused the value"; return null; }
            return Tuple.Create(ParamTransforms.SourceCanon(m.Source, m.Transform, v, row), LocalCanon(p));
        }

        /// <summary>
        /// Writes the rule's mappings (only = one mapping id, else all) on one of ours and records them (baseline / written,
        /// rejection cleared) in its record. Inside the caller's transaction. Returns how many were written; problems appended.
        /// </summary>
        internal static int Apply(Document doc, Element ours, Element source, FamilyMapRule rule, string linkName, string only, List<string> problems)
        {
            if (rule?.Parameters == null || rule.Parameters.Count == 0 || ours == null || source == null) return 0;
            var rec = MappedFamilyStore.Get(ours)?.Data;
            int n = 0;
            foreach (var m in rule.Parameters.Where(x => x.Usable && (only == null || x.Id == only)))
            {
                var done = Write(doc, ours, source, m, linkName, out var problem);
                if (done == null) { if (problem != null && problems != null && !problems.Contains(problem)) problems.Add(problem); continue; }
                n++;
                if (rec != null)
                {
                    rec.ParamBaseline = rec.ParamBaseline ?? new Dictionary<string, string>();
                    rec.ParamWritten = rec.ParamWritten ?? new Dictionary<string, string>();
                    rec.ParamBaseline[m.Id] = done.Item1;
                    rec.ParamWritten[m.Id] = done.Item2;
                    rec.ParamRejected?.Remove(m.Id);
                }
            }
            if (rec != null && n > 0) MappedFamilyStore.Write(ours, rec);
            return n;
        }

        internal class ClearResult { internal int Cleared, HandEdited, Skipped, Failed; }

        /// <summary>
        /// A removed mapping: clears the values it wrote on the local families of this link (ruleIds = only families placed by these rules,
        /// null = all) and forgets its baseline / written / rejected entries. Values edited by hand since (local value != written) are kept.
        /// Own transaction (one undo step); elements checked out by others skipped.
        /// </summary>
        internal static ClearResult Clear(Document doc, string linkUid, ParamMap m, ICollection<string> ruleIds)
        {
            var res = new ClearResult();
            if (m?.Target == null) return res;
            var typesDone = new HashSet<long>();
            using (var tx = new Transaction(doc, "Perseus: clear mapped parameter values"))
            {
                tx.Start();
                foreach (var r in MappedFamilyStore.All(doc, linkUid))
                {
                    var d = r.Data;
                    if (d?.ParamWritten == null || !d.ParamWritten.TryGetValue(m.Id, out var written)) continue;
                    if (ruleIds != null && !ruleIds.Contains(d.RuleId)) continue;
                    var el = doc.GetElement(r.Element);
                    if (el == null) continue;
                    if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, el.Id, out _) == CheckoutStatus.OwnedByOtherUser) { res.Skipped++; continue; }
                    var p = Find(el, m.Target);
                    bool again = m.Target.IsType && !typesDone.Add(el.GetTypeId().GetIdValue());
                    if (p != null && !again)
                    {
                        if (LocalCanon(p) != written) res.HandEdited++;
                        else if (m.Target.IsType && doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, el.GetTypeId(), out _) == CheckoutStatus.OwnedByOtherUser) res.Skipped++;
                        else if (!p.IsReadOnly && ClearOne(p)) res.Cleared++;
                        else res.Failed++;
                    }
                    d.ParamWritten.Remove(m.Id);
                    d.ParamBaseline?.Remove(m.Id);
                    d.ParamRejected?.Remove(m.Id);
                    MappedFamilyStore.Write(el, d);
                }
                tx.Commit();
            }
            return res;
        }

        /// <summary>Empties a parameter: text = "", others Parameter.ClearValue (no value; by reflection, not in every Revit version).</summary>
        private static bool ClearOne(Parameter p)
        {
            try
            {
                if (p.StorageType == StorageType.String) return p.Set("");
                var mi = p.GetType().GetMethod("ClearValue", Type.EmptyTypes);
                if (mi == null) return false;
                mi.Invoke(p, null);
                return true;
            }
            catch { return false; }
        }
    }
}
