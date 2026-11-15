using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using StorageType = Autodesk.Revit.DB.StorageType;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Parameters on both sides of a family mapping (API context, read only): the consultant's instance + type parameters of a
    /// mapped type with their values (for mapping/ParamCompat previews), and the local family type's writable targets (its type
    /// parameters, the family's instance parameters read from a placed instance, project parameters bound to its category).
    /// </summary>
    internal static class ParamDiscovery
    {
        internal class SourceParams
        {
            internal List<ParamRef> Params = new List<ParamRef>();
            /// <summary>Per ParamRef.Key: one value per consultant element (instance parameters) or per type (type parameters).</summary>
            internal Dictionary<string, List<SourceValue>> Values = new Dictionary<string, List<SourceValue>>();
            /// <summary>Per consultant element (same order): its values by ParamRef.Key (type ones repeated) + row by name (expressions).</summary>
            internal List<ElementSample> Elements = new List<ElementSample>();
            /// <summary>Parameter names usable as row["..."].</summary>
            internal List<string> RowNames = new List<string>();
        }

        internal class ElementSample
        {
            internal Dictionary<string, SourceValue> Values = new Dictionary<string, SourceValue>();
            internal Dictionary<string, string> Row = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal class TargetParams
        {
            internal List<ParamRef> Params = new List<ParamRef>();
            /// <summary>No placed instance of the family: its own instance parameters couldn't be read (project ones are listed).</summary>
            internal bool NoInstance;
        }

        internal const string ComputedElementId = "elementid", ComputedLinkName = "linkname";
        private const int MaxSourceElements = 400;

        /// <summary>The consultant elements of one type (uids in the link): parameter list + values.</summary>
        internal static SourceParams Source(Document linkDoc, IEnumerable<string> uids, string linkName)
        {
            var res = new SourceParams();
            var byKey = new Dictionary<string, ParamRef>();
            var typeCache = new Dictionary<long, List<Tuple<ParamRef, SourceValue>>>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(ParamRef r, SourceValue v)
            {
                if (!byKey.ContainsKey(r.Key)) { byKey[r.Key] = r; res.Params.Add(r); res.Values[r.Key] = new List<SourceValue>(); }
                res.Values[r.Key].Add(v);
            }
            foreach (var uid in uids.Take(MaxSourceElements))
            {
                var el = linkDoc.GetElement(uid);
                if (el == null) continue;
                var s = Sample(linkDoc, el, linkName, typeCache, Add);
                res.Elements.Add(s);
                names.UnionWith(s.Row.Keys);
            }
            res.RowNames = names.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            res.Params = res.Params.OrderBy(p => p.Computed != null ? 0 : 1).ThenBy(p => p.IsType).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return res;
        }

        /// <summary>
        /// One consultant element: values by key (type parameters from typeCache, read once per type) and its row by name (instance first,
        /// then type, then the computed fields). add = called for instance values and for type values the first time a type is read.
        /// </summary>
        private static ElementSample Sample(Document linkDoc, Element el, string linkName, Dictionary<long, List<Tuple<ParamRef, SourceValue>>> typeCache, Action<ParamRef, SourceValue> add)
        {
            var s = new ElementSample();
            void Put(ParamRef r, SourceValue v)
            {
                s.Values[r.Key] = v;
                if (!s.Row.ContainsKey(r.Name)) s.Row[r.Name] = v.HasValue ? v.Text ?? "" : "";
            }
            foreach (Parameter p in el.Parameters) { var r = Ref(p, false, true); if (r == null) continue; var v = Value(linkDoc, p, r); add?.Invoke(r, v); Put(r, v); }
            var type = linkDoc.GetElement(el.GetTypeId());
            if (type != null)
            {
                long tid = type.Id.GetIdValue();
                if (typeCache == null || !typeCache.TryGetValue(tid, out var list))
                {
                    list = new List<Tuple<ParamRef, SourceValue>>();
                    foreach (Parameter p in type.Parameters) { var r = Ref(p, true, true); if (r == null) continue; var v = Value(linkDoc, p, r); list.Add(Tuple.Create(r, v)); add?.Invoke(r, v); }
                    if (typeCache != null) typeCache[tid] = list;
                }
                foreach (var x in list) Put(x.Item1, x.Item2);
            }
            var idRef = new ParamRef { Name = "Consultant element id", Computed = ComputedElementId, Storage = "String", Kind = ParamCompat.Text, SpecLabel = "Text", Origin = "computed" };
            var idVal = new SourceValue { HasValue = true, Text = el.Id.GetIdValue().ToString() };
            var lnRef = new ParamRef { Name = "Link name", Computed = ComputedLinkName, Storage = "String", Kind = ParamCompat.Text, SpecLabel = "Text", Origin = "computed" };
            var lnVal = new SourceValue { HasValue = true, Text = linkName ?? "" };
            add?.Invoke(idRef, idVal); Put(idRef, idVal);
            add?.Invoke(lnRef, lnVal); Put(lnRef, lnVal);
            return s;
        }

        /// <summary>A consultant element's parameters by name, for expressions reading row["..."].</summary>
        internal static Dictionary<string, string> Row(Element el, string linkName) =>
            el == null ? new Dictionary<string, string>() : Sample(el.Document, el, linkName, null, null).Row;

        /// <summary>Where values can go on a local family type: its type parameters, its instance parameters, project parameters of its category.</summary>
        internal static TargetParams Target(Document doc, FamilySymbol symbol)
        {
            var res = new TargetParams();
            if (symbol == null) return res;
            var bound = BoundNames(doc, symbol.Category);
            var byKey = new Dictionary<string, ParamRef>();
            void Add(ParamRef r) { if (r != null && !byKey.ContainsKey(r.Key)) { byKey[r.Key] = r; res.Params.Add(r); } }
            foreach (Parameter p in symbol.Parameters) Add(Ref(p, true, false, bound));
            var inst = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                .Cast<FamilyInstance>().FirstOrDefault(fi => fi.Symbol?.Id == symbol.Id)
                ?? new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                .Cast<FamilyInstance>().FirstOrDefault(fi => fi.Symbol?.Family?.Id == symbol.Family?.Id);
            if (inst != null) foreach (Parameter p in inst.Parameters) Add(Ref(p, false, false, bound));
            else
            {
                res.NoInstance = true;
                // Project parameters bound to the category as instance parameters (the family's own need a placed instance).
                var it = doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                {
                    if (!(it.Current is InstanceBinding ib) || symbol.Category == null || !ib.Categories.Contains(symbol.Category)) continue;
                    var def = it.Key;
                    var spec = Spec(def);
                    Add(new ParamRef
                    {
                        Name = def.Name, Guid = ParamCreator.SharedGuid(doc, def), Storage = StorageOf(spec), Kind = KindOf(StorageOf(spec), spec),
                        Spec = spec?.TypeId, SpecLabel = Label(spec), IsType = false, Origin = ParamCreator.SharedGuid(doc, def) != null ? "shared" : "project",
                    });
                }
            }
            res.Params = res.Params.Where(p => !p.Name.StartsWith("Perseus_Link", StringComparison.Ordinal))
                                   .OrderBy(p => p.ReadOnly).ThenBy(p => p.IsType).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            return res;
        }

        private static HashSet<string> BoundNames(Document doc, Category cat)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (cat == null) return set;
            var it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Current is ElementBinding eb && eb.Categories.Contains(cat)) set.Add(it.Key.Name);
            return set;
        }

        private static ForgeTypeId Spec(Definition d)
        {
            try { return d?.GetDataType(); } catch { return null; }
        }

        private static string Label(ForgeTypeId spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return null;
            try { return LabelUtils.GetLabelForSpec(spec); } catch { return null; }
        }

        private static string StorageOf(ForgeTypeId spec)
        {
            if (spec == null) return "String";
            if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.MultilineText || spec == SpecTypeId.String.Url) return "String";
            if (spec == SpecTypeId.Boolean.YesNo || spec == SpecTypeId.Int.Integer) return "Integer";
            try { if (UnitUtils.IsMeasurableSpec(spec) || spec == SpecTypeId.Number) return "Double"; } catch { }
            return "ElementId";
        }

        private static string KindOf(string storage, ForgeTypeId spec)
        {
            switch (storage)
            {
                case "String": return ParamCompat.Text;
                case "ElementId": return ParamCompat.Element;
                case "Integer": return spec != null && spec == SpecTypeId.Boolean.YesNo ? ParamCompat.YesNo : ParamCompat.Integer;
                default: return spec != null && spec == SpecTypeId.Number ? ParamCompat.Number : ParamCompat.Measure;
            }
        }

        private static ParamRef Ref(Parameter p, bool isType, bool source, HashSet<string> bound = null)
        {
            var def = p?.Definition;
            if (def == null || string.IsNullOrEmpty(def.Name) || p.StorageType == StorageType.None) return null;
            var spec = Spec(def);
            string storage = p.StorageType.ToString();
            var bip = (def as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
            string guid = null;
            try { if (p.IsShared) guid = p.GUID.ToString(); } catch { }
            return new ParamRef
            {
                Name = def.Name, Guid = guid, BuiltIn = bip != BuiltInParameter.INVALID ? bip.ToString() : null,
                Storage = storage, Kind = KindOf(storage, spec), Spec = spec?.TypeId, SpecLabel = Label(spec) ?? (storage == "String" ? "Text" : null),
                IsType = isType,
                Origin = guid != null ? "shared" : bip != BuiltInParameter.INVALID ? "builtin" : bound != null && bound.Contains(def.Name) ? "project" : "family",
                ReadOnly = !source && p.IsReadOnly,
            };
        }

        internal static SourceValue Value(Document doc, Parameter p, ParamRef r)
        {
            var v = new SourceValue { HasValue = p.HasValue };
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: v.Text = p.AsString(); v.HasValue = !string.IsNullOrEmpty(v.Text); break;
                    case StorageType.Integer: v.Number = p.AsInteger(); v.Text = r.Kind == ParamCompat.YesNo ? (p.AsInteger() != 0 ? "Yes" : "No") : p.AsValueString() ?? p.AsInteger().ToString(); break;
                    case StorageType.Double:
                        v.Number = p.AsDouble();
                        v.Text = p.AsValueString();
                        var spec = p.Definition.GetDataType();
                        if (r.Kind == ParamCompat.Measure && spec != null)
                        {
                            try { v.Display = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()); } catch { }
                        }
                        break;
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        Element e = id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) : null;
                        v.Text = e?.Name ?? p.AsValueString();
                        v.HasValue = e != null || !string.IsNullOrEmpty(v.Text);
                        break;
                }
            }
            catch { v.HasValue = false; }
            return v;
        }
    }
}
