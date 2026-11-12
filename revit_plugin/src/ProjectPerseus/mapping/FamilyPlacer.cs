using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.revit;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.mapping
{
    /// <summary>
    /// Places our families where the consultant's are (models/FamilyMapRule: consultant type -> our type + offset +
    /// rotation + workset), or links our existing families to them, and records the link on our family
    /// (mapping/MappedFamilyStore). Placement is "floating" first: level-based families on the nearest level below
    /// (plan rotation only), work-plane / face-based families on their own work plane through the point (full XYZ
    /// rotation). Families that need a wall / ceiling / curve host aren't placed. API context.
    /// </summary>
    internal static class FamilyPlacer
    {
        /// <summary>A consultant family type present in the link, with its instances.</summary>
        internal class SourceType
        {
            internal string Key, Category, Family, Type;
            internal List<string> Uids = new List<string>();
            public override string ToString() => $"{Family} : {Type}";
        }

        /// <summary>One of our loaded family types.</summary>
        internal class TargetType
        {
            internal string Uid, Category, Family, Type;
            internal FamilyPlacementType Placement;
            internal bool Floating => Placement == FamilyPlacementType.OneLevelBased || Placement == FamilyPlacementType.TwoLevelsBased
                                      || Placement == FamilyPlacementType.WorkPlaneBased;
            internal bool FullRotation => Placement == FamilyPlacementType.WorkPlaneBased;
            public override string ToString() => $"{Family} : {Type}";
        }

        internal class Result
        {
            internal int Placed, Linked, AlreadyMapped, Failed, Skipped;
            internal List<string> Warnings = new List<string>();
            internal ElementId Last;
        }

        // ── Reading ──

        internal static List<SourceType> SourceTypes(Document linkDoc)
        {
            var byKey = new Dictionary<string, SourceType>(StringComparer.Ordinal);
            if (linkDoc == null) return new List<SourceType>();
            foreach (FamilyInstance fi in new FilteredElementCollector(linkDoc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                if (fi.SuperComponent != null) continue;   // nested: comes with its parent
                var sym = fi.Symbol;
                if (sym == null || fi.Category == null) continue;
                string key = FamilyMapRule.Key(fi.Category.Name, sym.FamilyName, sym.Name);
                if (!byKey.TryGetValue(key, out var st))
                    byKey[key] = st = new SourceType { Key = key, Category = fi.Category.Name, Family = sym.FamilyName, Type = sym.Name };
                st.Uids.Add(fi.UniqueId);
            }
            return byKey.Values.ToList();
        }

        internal static List<TargetType> TargetTypes(Document host)
        {
            var list = new List<TargetType>();
            foreach (FamilySymbol s in new FilteredElementCollector(host).OfClass(typeof(FamilySymbol)))
            {
                if (s.Category == null || s.Family == null || s.Family.IsInPlace) continue;
                FamilyPlacementType p;
                try { p = s.Family.FamilyPlacementType; } catch { continue; }
                if (p == FamilyPlacementType.ViewBased || p == FamilyPlacementType.CurveBasedDetail || p == FamilyPlacementType.Invalid) continue;
                list.Add(new TargetType { Uid = s.UniqueId, Category = s.Category.Name, Family = s.FamilyName, Type = s.Name, Placement = p });
            }
            return list.OrderBy(t => t.Category).ThenBy(t => t.Family).ThenBy(t => t.Type).ToList();
        }

        internal static List<string> Worksets(Document host)
        {
            if (!host.IsWorkshared) return new List<string>();
            return new FilteredWorksetCollector(host).OfKind(WorksetKind.UserWorkset).ToWorksets().Select(w => w.Name)
                                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The consultant element's frame in host coordinates: family transform, else location point / curve.</summary>
        internal static Frame SourceFrame(Element el, Transform linkToHost)
        {
            Transform t = null;
            if (el is FamilyInstance fi)
            {
                try { t = fi.GetTotalTransform(); } catch { t = null; }
            }
            if (t == null)
            {
                t = Transform.Identity;
                if (el.Location is LocationPoint lp)
                {
                    t = Transform.CreateRotation(XYZ.BasisZ, lp.Rotation);
                    t.Origin = lp.Point;
                }
                else if (el.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                    t = Transform.Identity;
                    t.BasisX = d;
                    t.BasisZ = XYZ.BasisZ;
                    t.BasisY = XYZ.BasisZ.CrossProduct(d).Normalize();
                    t.Origin = c.Evaluate(0.5, true);
                }
                else
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb != null) t.Origin = (bb.Min + bb.Max) / 2;
                }
            }
            var h = linkToHost.Multiply(t);
            return new Frame
            {
                O = new[] { h.Origin.X, h.Origin.Y, h.Origin.Z },
                X = new[] { h.BasisX.X, h.BasisX.Y, h.BasisX.Z },
                Y = new[] { h.BasisY.X, h.BasisY.Y, h.BasisY.Z },
                Z = new[] { h.BasisZ.X, h.BasisZ.Y, h.BasisZ.Z },
            };
        }

        /// <summary>Consultant elements of a rule's type, in the link.</summary>
        internal static List<Element> SourceElements(Document linkDoc, FamilyMapRule rule)
        {
            var list = new List<Element>();
            if (linkDoc == null) return list;
            foreach (FamilyInstance fi in new FilteredElementCollector(linkDoc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType())
            {
                if (fi.SuperComponent != null || fi.Category == null || fi.Symbol == null) continue;
                if (FamilyMapRule.Key(fi.Category.Name, fi.Symbol.FamilyName, fi.Symbol.Name) == rule.SourceKey) list.Add(fi);
            }
            return list;
        }

        // ── Placing ──

        /// <summary>
        /// Places our family for one consultant element (inside the caller's transaction) and records it. Returns the
        /// instance, or null with a reason.
        /// </summary>
        internal static FamilyInstance Place(Document host, RevitLinkInstance inst, FamilyMapRule rule, FamilySymbol symbol, Element source,
                                             List<Level> levels, WorksetId workset, string user, out string warning)
        {
            warning = null;
            var placement = symbol.Family.FamilyPlacementType;
            var src = SourceFrame(source, inst.GetTotalTransform());
            var f = MappingMath.Place(src, rule.OffsetMm, rule.RotationDeg);
            var p = new XYZ(f.O[0], f.O[1], f.O[2]);
            if (!symbol.IsActive) { symbol.Activate(); host.Regenerate(); }
            FamilyInstance fi;
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                case FamilyPlacementType.TwoLevelsBased:
                {
                    var level = LevelBelow(levels, p.Z);
                    if (level == null) { warning = "the model has no levels"; return null; }
                    var dir = MappingMath.PlanDirection(f);
                    fi = host.Create.NewFamilyInstance(p, symbol, new XYZ(dir[0], dir[1], dir[2]), level, StructuralType.NonStructural);
                    SetOffset(fi, p.Z - level.ProjectElevation);
                    if (MappingMath.IsTilted(f)) warning = $"{symbol.FamilyName} is level-based: it stays upright (X / Y rotation and tilt ignored)";
                    break;
                }
                case FamilyPlacementType.WorkPlaneBased:
                {
                    var plane = Plane.CreateByOriginAndBasis(p, new XYZ(f.X[0], f.X[1], f.X[2]), new XYZ(f.Y[0], f.Y[1], f.Y[2]));
                    var sp = SketchPlane.Create(host, plane);
                    fi = host.Create.NewFamilyInstance(sp.GetPlaneReference(), p, new XYZ(f.X[0], f.X[1], f.X[2]), symbol);
                    break;
                }
                default:
                    warning = $"{symbol.FamilyName} needs a host ({placement}): only level-, work-plane- and face-based families are placed";
                    return null;
            }
            if (workset != null && workset != WorksetId.InvalidWorksetId)
            {
                var wp = fi.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (wp != null && !wp.IsReadOnly) wp.Set(workset.IntegerValue);
            }
            MappedFamilyStore.Write(fi, inst.UniqueId, source.UniqueId, source.Id.GetIdValue(), rule.Id, user, MappedFamilyStore.Placed);
            return fi;
        }

        /// <summary>
        /// Places our family for every consultant element of the rule's type that has none yet (also counting families
        /// mapped to an element it replaced: replacementMap old -> current). One undo step.
        /// </summary>
        internal static Result PlaceAll(Document host, RevitLinkInstance inst, FamilyMapRule rule, IDictionary<string, string> replacementMap,
                                        string user, Action<string, double> progress, int limit = int.MaxValue)
        {
            var r = new Result();
            var linkDoc = inst.GetLinkDocument();
            var symbol = host.GetElement(rule.TargetSymbolUid) as FamilySymbol;
            if (linkDoc == null || symbol == null) { r.Warnings.Add(linkDoc == null ? "The link isn't loaded." : "Our family type isn't in the model any more."); return r; }
            var mapped = new HashSet<string>(MappedFamilyStore.All(host, inst.UniqueId).Select(x => MappedFamilyStore.Current(x, replacementMap)), StringComparer.Ordinal);
            var sources = SourceElements(linkDoc, rule).Where(s => !mapped.Contains(s.UniqueId)).Take(limit).ToList();
            r.AlreadyMapped = SourceElements(linkDoc, rule).Count - sources.Count;
            if (sources.Count == 0) return r;
            var levels = Levels(host);
            var ws = WorksetByName(host, rule.Workset);
            using (var tg = new TransactionGroup(host, $"Perseus: place {symbol.FamilyName} for {rule.SourceFamily}"))
            {
                tg.Start();
                using (var tx = new Transaction(host, "Perseus: place mapped families"))
                {
                    tx.Start();
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new Quiet());
                    tx.SetFailureHandlingOptions(fo);
                    for (int i = 0; i < sources.Count; i++)
                    {
                        if (i % 25 == 0) progress?.Invoke($"{i} of {sources.Count}", (double)i / sources.Count);
                        try
                        {
                            var fi = Place(host, inst, rule, symbol, sources[i], levels, ws, user, out string warning);
                            if (fi != null) { r.Placed++; r.Last = fi.Id; }
                            else r.Failed++;
                            if (warning != null && !r.Warnings.Contains(warning)) r.Warnings.Add(warning);
                            if (fi == null && warning != null && warning.Contains("needs a host")) { r.Failed = sources.Count; break; }
                        }
                        catch (Exception ex)
                        {
                            r.Failed++;
                            string w = ex.Message;
                            if (!r.Warnings.Contains(w)) r.Warnings.Add(w);
                        }
                    }
                    tx.Commit();
                }
                tg.Assimilate();
            }
            Log.Info($"[Mapping] {rule.SourceFamily} : {rule.SourceType} -> {rule.TargetName}: placed {r.Placed}, already mapped {r.AlreadyMapped}, failed {r.Failed}" +
                     (r.Warnings.Count > 0 ? " (" + string.Join("; ", r.Warnings.Take(3)) + ")" : ""));
            return r;
        }

        /// <summary>
        /// Links our existing families of the rule's target type (not mapped yet) to the consultant elements they sit at:
        /// nearest expected position (consultant frame + offset) within tolerance, closest pairs first, one-to-one.
        /// Families checked out by others are skipped.
        /// </summary>
        internal static Result LinkExisting(Document host, RevitLinkInstance inst, FamilyMapRule rule, IDictionary<string, string> replacementMap,
                                            double toleranceMm, string user)
        {
            var r = new Result();
            var linkDoc = inst.GetLinkDocument();
            var symbol = host.GetElement(rule.TargetSymbolUid) as FamilySymbol;
            if (linkDoc == null || symbol == null) { r.Warnings.Add(linkDoc == null ? "The link isn't loaded." : "Our family type isn't in the model any more."); return r; }
            var records = MappedFamilyStore.All(host);
            var mappedOurs = new HashSet<ElementId>(records.Select(x => x.Element));
            var mappedSources = new HashSet<string>(records.Where(x => x.LinkInstanceUid == inst.UniqueId)
                                                           .Select(x => MappedFamilyStore.Current(x, replacementMap)), StringComparer.Ordinal);
            var ours = new FilteredElementCollector(host).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Id == symbol.Id && !mappedOurs.Contains(fi.Id))
                .Select(fi => new { El = fi, P = Position(fi) }).Where(x => x.P != null).ToList();
            var sources = SourceElements(linkDoc, rule).Where(s => !mappedSources.Contains(s.UniqueId)).ToList();
            var toHost = inst.GetTotalTransform();
            double tol = toleranceMm / MappingMath.MmPerFoot;
            var pairs = new List<Tuple<double, int, int>>();
            var expected = sources.Select(s => MappingMath.Place(SourceFrame(s, toHost), rule.OffsetMm, rule.RotationDeg).O).ToList();
            for (int i = 0; i < sources.Count; i++)
                for (int j = 0; j < ours.Count; j++)
                {
                    double d = MappingMath.Distance(expected[i], ours[j].P);
                    if (d <= tol) pairs.Add(Tuple.Create(d, i, j));
                }
            var usedS = new HashSet<int>();
            var usedO = new HashSet<int>();
            using (var tx = new Transaction(host, "Perseus: link existing families to consultant elements"))
            {
                tx.Start();
                foreach (var p in pairs.OrderBy(x => x.Item1))
                {
                    if (usedS.Contains(p.Item2) || usedO.Contains(p.Item3)) continue;
                    var ourEl = ours[p.Item3].El;
                    if (host.IsWorkshared && WorksharingUtils.GetCheckoutStatus(host, ourEl.Id, out _) == CheckoutStatus.OwnedByOtherUser) { r.Skipped++; usedO.Add(p.Item3); continue; }
                    var s = sources[p.Item2];
                    MappedFamilyStore.Write(ourEl, inst.UniqueId, s.UniqueId, s.Id.GetIdValue(), rule.Id, user, MappedFamilyStore.Linked);
                    usedS.Add(p.Item2); usedO.Add(p.Item3);
                    r.Linked++;
                    r.Last = ourEl.Id;
                }
                tx.Commit();
            }
            r.Failed = sources.Count - r.Linked;   // consultant elements with none of ours nearby
            Log.Info($"[Mapping] link existing {rule.TargetName} to {rule.SourceFamily} : {rule.SourceType}: linked {r.Linked}, " +
                     $"{r.Failed} consultant elements without one within {toleranceMm:0} mm, {ours.Count - usedO.Count} of ours left, {r.Skipped} checked out by others");
            return r;
        }

        internal static double[] Position(Element el)
        {
            if (el.Location is LocationPoint lp) return new[] { lp.Point.X, lp.Point.Y, lp.Point.Z };
            if (el is FamilyInstance fi)
            {
                try { var t = fi.GetTotalTransform(); return new[] { t.Origin.X, t.Origin.Y, t.Origin.Z }; } catch { }
            }
            var bb = el.get_BoundingBox(null);
            return bb == null ? null : new[] { (bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, (bb.Min.Z + bb.Max.Z) / 2 };
        }

        internal static List<Level> Levels(Document host) =>
            new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();

        /// <summary>Highest level at or below z (1 mm tolerance), else the lowest.</summary>
        internal static Level LevelBelow(List<Level> levels, double z)
        {
            Level best = null;
            foreach (var l in levels) if (l.ProjectElevation <= z + 1 / MappingMath.MmPerFoot) best = l;
            return best ?? levels.FirstOrDefault();
        }

        internal static WorksetId WorksetByName(Document host, string name)
        {
            if (!host.IsWorkshared || string.IsNullOrEmpty(name)) return null;
            var ws = new FilteredWorksetCollector(host).OfKind(WorksetKind.UserWorkset).ToWorksets().FirstOrDefault(w => w.Name == name);
            return ws?.Id;
        }

        private static void SetOffset(FamilyInstance fi, double offset)
        {
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                var p = fi.get_Parameter(bip);
                if (p != null && !p.IsReadOnly) { p.Set(offset); return; }
            }
        }

        internal class Quiet : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                a.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }
    }
}
