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
            /// <summary>"Work Plane-Based" ticked in the family (level-based family that can sit on any work plane).</summary>
            internal bool WorkPlaneFlag;
            internal bool Floating => Placement == FamilyPlacementType.OneLevelBased || Placement == FamilyPlacementType.TwoLevelsBased
                                      || Placement == FamilyPlacementType.WorkPlaneBased;
            /// <summary>Hosted by a wall / floor / ceiling / roof: placed in ours at the target point (e.g. a wall penetration).</summary>
            internal bool Hosted => Placement == FamilyPlacementType.OneLevelBasedHosted;
            /// <summary>"wall" / "floor" / "ceiling" / "roof" for hosted families.</summary>
            internal string HostKind = "wall";
            internal bool Placeable => Floating || Hosted;
            /// <summary>Placed on a work plane (full rotation): work-plane-based or face-based.</summary>
            internal bool FullRotation => Placement == FamilyPlacementType.WorkPlaneBased || WorkPlaneFlag;
            internal bool FaceBased => Placement == FamilyPlacementType.WorkPlaneBased && !WorkPlaneFlag;
            /// <summary>work-plane-based / face-based / level-based / hosted (needs a wall, ceiling...).</summary>
            internal string Hosting => Hosted ? HostKind + "-hosted" : !Floating ? "hosted" : FaceBased ? "face-based" : FullRotation ? "work-plane-based" : "level-based";
            /// <summary>Preferred first: work-plane-based, then level-based, then face-based, hosted last.</summary>
            internal int Preference => Hosted ? 2 : !Floating ? 3 : FaceBased ? 2 : FullRotation ? 0 : 1;
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
                bool wp = false;
                try { wp = s.Family.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger() == 1; } catch { }
                list.Add(new TargetType { Uid = s.UniqueId, Category = s.Category.Name, Family = s.FamilyName, Type = s.Name, Placement = p, WorkPlaneFlag = wp,
                                          HostKind = HostKindOf(s.Family) });
            }
            return list.OrderBy(t => t.Category).ThenBy(t => t.Preference).ThenBy(t => t.Family).ThenBy(t => t.Type).ToList();
        }

        /// <summary>What a hosted family goes in (Family "Host" behaviour): wall (default), floor, ceiling or roof.</summary>
        internal static string HostKindOf(Family f)
        {
            int b = 1;
            try { b = f.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsInteger() ?? 1; } catch { }
            return b == 2 ? "floor" : b == 3 ? "ceiling" : b == 4 ? "roof" : "wall";
        }

        /// <summary>
        /// Our wall / floor / ceiling / roof at a point for a hosted family: elements whose box holds the point (50 mm), nearest
        /// first. Walls: the point must lie ALONG the wall (within 10 mm of its length: at a junction the wall that runs through
        /// it, not the one ending next to it, where Revit clamps the family to the wall's end and it cuts nothing) and within
        /// half the wall's width + 50 mm across it. Others: height distance. Null when none qualifies.
        /// </summary>
        internal static Element FindHost(Document host, string kind, XYZ p) => FindHost(host, kind, p, out _);

        internal static Element FindHost(Document host, string kind, XYZ p, out string why)
        {
            why = null;
            var cat = kind == "floor" ? BuiltInCategory.OST_Floors : kind == "ceiling" ? BuiltInCategory.OST_Ceilings
                    : kind == "roof" ? BuiltInCategory.OST_Roofs : BuiltInCategory.OST_Walls;
            double tol = 50 / MappingMath.MmPerFoot;
            Element best = null;
            double bestD = double.MaxValue;
            foreach (var e in new FilteredElementCollector(host).OfCategory(cat).WhereElementIsNotElementType()
                         .WherePasses(new BoundingBoxContainsPointFilter(p, tol)))
            {
                double d;
                if (e is Wall w && w.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var flat = new XYZ(p.X, p.Y, c.GetEndPoint(0).Z);
                    var pr = c.Project(flat);
                    if (pr == null) continue;
                    var v = flat - pr.XYZPoint;
                    XYZ t;
                    try { t = c.ComputeDerivatives(pr.Parameter, false).BasisX.Normalize(); } catch { continue; }
                    double along = Math.Abs(v.DotProduct(t));                    // beyond the wall's end
                    double across = (v - t.Multiply(v.DotProduct(t))).GetLength();
                    string label = $"wall {e.Id.GetIdValue()} ({along * MappingMath.MmPerFoot:N0} mm past its end, {across * MappingMath.MmPerFoot:N0} mm from its line, {w.Width * MappingMath.MmPerFoot:N0} mm thick)";
                    if (along > 10 / MappingMath.MmPerFoot || across > w.Width / 2 + tol) { why = (why == null ? "" : why + "; ") + label; continue; }
                    // At a T / L junction the abutting wall's location line runs on to the other wall's centre while its body
                    // stops at that wall's face: only a wall whose SOLID is at the point can host the penetration.
                    if (!BodyAt(w, pr.XYZPoint, t))
                    {
                        why = (why == null ? "" : why + "; ") + label.TrimEnd(')') + ", but its body doesn't reach the point: cut back at a wall join)";
                        continue;
                    }
                    d = Math.Max(0, across - w.Width / 2);
                }
                else
                {
                    var bb = e.get_BoundingBox(null);
                    d = bb == null ? double.MaxValue : Math.Max(0, Math.Max(bb.Min.Z - p.Z, p.Z - bb.Max.Z));
                }
                if (d < bestD) { bestD = d; best = e; }
            }
            return bestD <= tol ? best : null;
        }

        /// <summary>
        /// True when the wall's body reaches the point along the wall: the solid's extent along its direction <paramref name="t"/>
        /// covers <paramref name="onLine"/> (the point projected on the location line). Holes cut by openings already there don't
        /// change that extent, a join that cuts the wall back does. True when the geometry can't be read.
        /// </summary>
        private static bool BodyAt(Wall w, XYZ onLine, XYZ t)
        {
            try
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (var g in w.get_Geometry(new Options { ComputeReferences = false }) ?? Enumerable.Empty<GeometryObject>())
                {
                    var solids = g is Solid s ? new[] { s }
                               : g is GeometryInstance gi ? gi.GetInstanceGeometry().OfType<Solid>().ToArray() : new Solid[0];
                    foreach (var so in solids)
                    {
                        if (so == null || so.Volume <= 1e-9) continue;
                        foreach (Edge e in so.Edges)
                            foreach (var q in e.Tessellate())
                            {
                                double d = (q - onLine).DotProduct(t);
                                if (d < min) min = d;
                                if (d > max) max = d;
                            }
                    }
                }
                if (min > max) return true;
                double slack = 5 / MappingMath.MmPerFoot;
                bool ok = min <= slack && max >= -slack;
                if (!ok) Log.Info($"[Mapping] wall {w.Id.GetIdValue()}: body runs {min * MappingMath.MmPerFoot:N0} to {max * MappingMath.MmPerFoot:N0} mm from the point along it (cut back at a join)");
                return ok;
            }
            catch (Exception ex)
            {
                Log.Info($"[Mapping] wall {w.Id.GetIdValue()} body check failed: {ex.Message}");
                return true;
            }
        }

        private static string Mm(XYZ q) => q == null ? "-" : $"({q.X * MappingMath.MmPerFoot:N0}, {q.Y * MappingMath.MmPerFoot:N0}, {q.Z * MappingMath.MmPerFoot:N0})";

        /// <summary>Diagnostics for hosted placement: where the consultant element is (host mm) and the target point.</summary>
        internal static void LogSourceGeometry(Element source, Transform toHost, XYZ p)
        {
            try
            {
                string line = "", box = "";
                if (source.Location is LocationCurve lc && lc.Curve != null)
                    line = $", line {Mm(toHost.OfPoint(lc.Curve.GetEndPoint(0)))} -> {Mm(toHost.OfPoint(lc.Curve.GetEndPoint(1)))}";
                else if (source.Location is LocationPoint lp) line = $", point {Mm(toHost.OfPoint(lp.Point))}";
                var bb = source.get_BoundingBox(null);
                if (bb != null) box = $", box {Mm(toHost.OfPoint(bb.Min))} .. {Mm(toHost.OfPoint(bb.Max))}";
                string origin = source is FamilyInstance fi ? $", family origin {Mm(toHost.OfPoint(fi.GetTotalTransform().Origin))}" : "";
                Log.Info($"[Mapping] consultant {source.Id.GetIdValue()}{line}{box}{origin}; target {Mm(p)} (host mm)");
            }
            catch (Exception ex) { Log.Info($"[Mapping] consultant geometry: {ex.Message}"); }
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
                // Line-based (structural framing, e.g. penetration voids drawn as framing): the transform sits at the line's start,
                // so a mapped family would drift with the length. Use the element's centre (its box, else the line's middle).
                if (t != null && fi.Location is LocationCurve flc && flc.Curve != null)
                {
                    XYZ centre = null;
                    var bb = el.get_BoundingBox(null);
                    if (bb != null && bb.Max.DistanceTo(bb.Min) > 1e-3) centre = (bb.Min + bb.Max) / 2;
                    else { try { centre = flc.Curve.Evaluate(0.5, true); } catch { } }
                    if (centre != null) t.Origin = centre;
                }
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
            XYZ hostNormal = null;   // hosted: Revit keeps it in the host, so it's only snapped along the host
            bool workPlaneFlag = false;
            try { workPlaneFlag = symbol.Family.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger() == 1; } catch { }
            if (workPlaneFlag && (placement == FamilyPlacementType.OneLevelBased || placement == FamilyPlacementType.TwoLevelsBased))
                placement = FamilyPlacementType.WorkPlaneBased;   // place it on a work plane: all three rotations apply
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
                    var level = LevelBelow(levels, p.Z);
                    if (level == null) { warning = "the model has no levels"; return null; }
                    fi = PlaceOnWorkPlane(host, symbol, f, p, level, workset, out warning);
                    if (fi == null) return null;
                    break;
                }
                case FamilyPlacementType.OneLevelBasedHosted:
                {
                    string kind = HostKindOf(symbol.Family);
                    LogSourceGeometry(source, inst.GetTotalTransform(), p);
                    var hostEl = FindHost(host, kind, p, out string notHere);
                    if (hostEl == null)
                    {
                        warning = $"no {kind} of ours runs through the consultant element ({symbol.FamilyName} is {kind}-hosted)" + (notHere != null ? $"; nearby: {notHere}" : "");
                        Log.Info($"[Mapping] {warning}");
                        return null;
                    }
                    var level = host.GetElement(hostEl.LevelId) as Level ?? LevelBelow(levels, p.Z);
                    if (level == null) { warning = "the model has no levels"; return null; }
                    fi = host.Create.NewFamilyInstance(p, symbol, hostEl, level, StructuralType.NonStructural);
                    hostNormal = hostEl is Wall wall ? wall.Orientation : XYZ.BasisZ;
                    if (hostEl is Wall)
                    {
                        // Revit puts a wall-hosted family at the level (the wall's base); its height is the elevation / sill parameter.
                        host.Regenerate();
                        double dz = p.Z - fi.GetTotalTransform().Origin.Z;
                        foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM })
                        {
                            var hp = fi.get_Parameter(bip);
                            if (hp == null || hp.IsReadOnly || hp.StorageType != Autodesk.Revit.DB.StorageType.Double) continue;
                            try { hp.Set(hp.AsDouble() + dz); break; } catch { }
                        }
                    }
                    Log.Info($"[Mapping] {symbol.FamilyName}: placed in {kind} {hostEl.Id.GetIdValue()} on '{level.Name}'");
                    break;
                }
                default:
                    warning = $"{symbol.FamilyName} needs a host ({placement}): only level-, work-plane-, face-based and wall / floor / ceiling / roof-hosted families are placed";
                    return null;
            }
            if (workset != null && workset != WorksetId.InvalidWorksetId)
            {
                var wp = fi.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (wp != null && !wp.IsReadOnly) wp.Set(workset.IntegerValue);
            }
            string residual = SnapTo(host, fi, p, hostNormal);
            if (residual != null) warning = warning == null ? residual : warning + "; " + residual;
            var placedFrame = FrameOf(fi);
            MappedFamilyStore.Write(fi, inst.UniqueId, source.UniqueId, source.Id.GetIdValue(), rule.Id, user, MappedFamilyStore.Placed,
                                    TypeKey(source), src, placedFrame, MappingMath.Realisation(f, placedFrame, hostNormal != null));
            return fi;
        }

        /// <summary>
        /// Moves the placed family so its insertion point is exactly the target point: offset parameters mean different
        /// things for level-, face- and work-plane-based families (and their sign follows the host's side), so the
        /// result is measured and corrected instead of trusted. Returns a warning when Revit kept it away.
        /// </summary>
        private static string SnapTo(Document host, FamilyInstance fi, XYZ target, XYZ keepInHost = null)
        {
            try
            {
                host.Regenerate();
                var at = fi.GetTotalTransform().Origin;
                var delta = target - at;
                if (keepInHost != null)
                {
                    // Hosted: it can't leave the wall / floor; only move it along it (its depth in the host is the family's).
                    var n = keepInHost.Normalize();
                    delta -= n.Multiply(delta.DotProduct(n));
                    target = at + delta;
                }
                if (delta.GetLength() < 0.5 / MappingMath.MmPerFoot) return null;
                Log.Info($"[Mapping] placed {delta.GetLength() * MappingMath.MmPerFoot:N0} mm from the target (dx {delta.X * MappingMath.MmPerFoot:N0}, dy {delta.Y * MappingMath.MmPerFoot:N0}, dz {delta.Z * MappingMath.MmPerFoot:N0} mm): moving it onto the target");
                ElementTransformUtils.MoveElement(host, fi.Id, delta);
                host.Regenerate();
                double left = (target - fi.GetTotalTransform().Origin).GetLength() * MappingMath.MmPerFoot;
                return left < 1 ? null : $"Revit kept it {left:N0} mm from the target point (constrained by its host)";
            }
            catch (Exception ex)
            {
                return "couldn't move it onto the target point: " + ex.Message;
            }
        }

        internal const string PlanePrefix = "Perseus mapping";

        /// <summary>Perseus mapping sketch / reference planes that no family stands on (ignoring <paramref name="except"/>, about to be deleted).</summary>
        internal static List<ElementId> UnusedPerseusPlanes(Document host, ElementId except)
        {
            var list = new List<ElementId>();
            var instances = new ElementClassFilter(typeof(FamilyInstance));
            foreach (var e in new FilteredElementCollector(host).WherePasses(new LogicalOrFilter(
                         new ElementClassFilter(typeof(SketchPlane)), new ElementClassFilter(typeof(ReferencePlane)))))
            {
                string name;
                try { name = e.Name; } catch { continue; }
                if (name == null || !name.StartsWith(PlanePrefix, StringComparison.Ordinal)) continue;
                try
                {
                    var users = e.GetDependentElements(instances).Where(d => except == null || d != except).ToList();
                    if (users.Count == 0) list.Add(e.Id);
                }
                catch { /* keep it */ }
            }
            return list;
        }

        /// <summary>
        /// Work-plane / face-based families, floating, the way Rhino.Inside.Revit's "Add Component (Work Plane)" does it:
        /// a SketchPlane is the work plane and the family is created with NewFamilyInstance(point, type, x direction,
        /// sketchPlane, NonStructural) - the sketch plane passed as the host ELEMENT (the Reference overload hosts
        /// face-based families "on face"). Upright or upside-down (ceiling) elements: a horizontal sketch plane at the
        /// target height (one per level + height + facing, reused); tilted ones: a sketch plane through the point in the
        /// element's own axes. Offset from host stays 0 in both (Revit ignored it on a level-height plane). Schedule level = level below. Fallbacks: the level's plane
        /// reference, a Perseus reference plane, plain level placement. Each attempt runs in a sub-transaction.
        /// </summary>
        private static FamilyInstance PlaceOnWorkPlane(Document host, FamilySymbol symbol, Frame f, XYZ p, Level level, WorksetId workset, out string warning)
        {
            warning = null;
            var n = new XYZ(f.Z[0], f.Z[1], f.Z[2]).Normalize();
            var x = new XYZ(f.X[0], f.X[1], f.X[2]).Normalize();
            var lp = new XYZ(0, 0, level.ProjectElevation);
            double off = (p - lp).DotProduct(n);
            var onPlane = p - n.Multiply(off);
            var errors = new List<string>();
            var dir = MappingMath.PlanDirection(f);
            var xh = new XYZ(dir[0], dir[1], 0);

            // 1. Sketch plane as the host element (Rhino.Inside's way).
            bool vertical = !MappingMath.IsTilted(f);   // Z straight up or straight down
            FamilyInstance fi = Try(host, errors, "sketch plane", () =>
            {
                XYZ origin, bx, by, at;
                double offset;
                string name = null;
                if (vertical)
                {
                    // Facing up (or down for ceiling devices) AT THE TARGET HEIGHT, offset 0 (Rhino.Inside's way): Revit
                    // ignored Offset from Host on a level-height plane and a work-plane family can't be moved off its
                    // plane, so it stayed at the level. One plane per level + height + facing, named so it's recognisable.
                    var nz = n.Z >= 0 ? XYZ.BasisZ : XYZ.BasisZ.Negate();
                    origin = new XYZ(0, 0, p.Z);
                    bx = xh.GetLength() > 1e-9 ? xh : XYZ.BasisX;
                    by = nz.CrossProduct(bx).Normalize();
                    at = p;
                    offset = 0;
                    name = $"{PlanePrefix} · {level.Name} {(p.Z - level.ProjectElevation) * MappingMath.MmPerFoot:+0;-0;+0} mm · {(nz.Z > 0 ? "up" : "down")}";
                }
                else
                {
                    origin = p; bx = x; by = n.CrossProduct(x).Normalize(); at = p; offset = 0;
                }
                var sp = name == null ? null : new FilteredElementCollector(host).OfClass(typeof(SketchPlane)).Cast<SketchPlane>()
                    .FirstOrDefault(s => s.Name == name && s.GetPlane().Normal.IsAlmostEqualTo(bx.CrossProduct(by).Normalize())
                                         && Math.Abs(s.GetPlane().Origin.Z - origin.Z) < 0.5 / MappingMath.MmPerFoot);
                if (sp == null)
                {
                    sp = SketchPlane.Create(host, Plane.CreateByOriginAndBasis(origin, bx, by));
                    if (name != null) { try { sp.Name = name; } catch { } }
                    if (workset != null && workset != WorksetId.InvalidWorksetId)
                    {
                        var wp = sp.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (wp != null && !wp.IsReadOnly) { try { wp.Set(workset.IntegerValue); } catch { } }
                    }
                }
                var made = host.Create.NewFamilyInstance(at, symbol, bx, sp, StructuralType.NonStructural);
                var ho = made.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                if (ho != null && !ho.IsReadOnly && Math.Abs(ho.AsDouble() - offset) > 1e-9) ho.Set(offset);
                var sl = made.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (sl != null && !sl.IsReadOnly) { try { sl.Set(level.Id); } catch { } }
                return made;
            });
            if (fi != null)
            {
                Log.Info($"[Mapping] {symbol.FamilyName}: placed on a sketch plane ({(vertical ? $"at level '{level.Name}' {(p.Z - level.ProjectElevation) * MappingMath.MmPerFoot:+0;-0;+0} mm, facing {(n.Z >= 0 ? "up" : "down")}" : "through the point, tilted")}); {OffsetParams(fi)}");
                return fi;
            }

            // 2. The level as the work plane (plan rotation), height = elevation from host.
            fi = Try(host, errors, "level work plane", () =>
            {
                var made = host.Create.NewFamilyInstance(level.GetPlaneReference(), new XYZ(p.X, p.Y, level.ProjectElevation), xh, symbol);
                SetHostOffset(made, p.Z - level.ProjectElevation);
                return made;
            });
            if (fi != null)
            {
                Log.Info($"[Mapping] {symbol.FamilyName}: sketch plane refused ({errors.LastOrDefault()}); placed on work plane = level '{level.Name}', elevation from host {(p.Z - level.ProjectElevation) * MappingMath.MmPerFoot:N0} mm");
                if (MappingMath.IsTilted(f)) warning = "on the level's work plane (faces up): X / Y rotation not applied";
                return fi;
            }

            // 3. Reference plane with this orientation through the level (face-based families end up "on face").
            fi = Try(host, errors, "reference plane", () =>
            {
                var rp = ReferencePlaneFor(host, level, n, x, workset);
                if (rp == null) throw new InvalidOperationException("no view to create a reference plane in");
                var made = host.Create.NewFamilyInstance(rp.GetReference(), onPlane, x, symbol);
                SetHostOffset(made, off);
                return made;
            });
            if (fi != null)
            {
                Log.Info($"[Mapping] {symbol.FamilyName}: level work plane refused ({errors.LastOrDefault()}); placed on a Perseus reference plane");
                warning = "Revit wouldn't place it on the level's work plane, so it's on a Perseus reference plane";
                return fi;
            }

            // 4. Plain level placement.
            fi = Try(host, errors, "level", () =>
            {
                var made = host.Create.NewFamilyInstance(p, symbol, xh, level, StructuralType.NonStructural);
                SetOffset(made, p.Z - level.ProjectElevation);
                return made;
            });
            if (fi != null) { warning = "placed on the level: X / Y rotation and tilt not applied"; return fi; }
            warning = $"{symbol.FamilyName} couldn't be placed floating ({string.Join("; ", errors)})";
            Log.Info($"[Mapping] {warning}");
            return null;
        }

        private static FamilyInstance Try(Document host, List<string> errors, string how, Func<FamilyInstance> place)
        {
            using (var st = new SubTransaction(host))
            {
                st.Start();
                try
                {
                    var fi = place();
                    host.Regenerate();
                    st.Commit();
                    return fi;
                }
                catch (Exception ex)
                {
                    errors.Add($"{how}: {ex.Message}");
                    if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                    return null;
                }
            }
        }

        /// <summary>The Perseus reference plane for this level and normal (rounded), created when missing.</summary>
        private static ReferencePlane ReferencePlaneFor(Document host, Level level, XYZ n, XYZ inPlane, WorksetId workset)
        {
            string name = $"{PlanePrefix} · {level.Name} · {n.X:0.000},{n.Y:0.000},{n.Z:0.000}";
            var existing = new FilteredElementCollector(host).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().FirstOrDefault(r => r.Name == name);
            if (existing != null) return existing;
            View view = new FilteredElementCollector(host).OfClass(typeof(View3D)).Cast<View3D>().FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective)
                        ?? (host.ActiveView != null && !host.ActiveView.IsTemplate ? host.ActiveView : null);
            if (view == null) return null;
            var origin = new XYZ(0, 0, level.ProjectElevation);
            // Make the plane pass through the level origin with normal n: the line and the cut vector span the plane.
            var a = inPlane - n.Multiply(inPlane.DotProduct(n));
            if (a.GetLength() < 1e-6) a = Math.Abs(n.Z) < 0.9 ? XYZ.BasisZ.CrossProduct(n) : XYZ.BasisX;
            a = a.Normalize();
            var cut = n.CrossProduct(a).Normalize();
            var rp = host.Create.NewReferencePlane(origin, origin + a.Multiply(10), cut, view);
            // Orientation check: NewReferencePlane's normal = line x cut; flip the cut vector if it came out reversed.
            if (rp.Normal.DotProduct(n) < 0)
            {
                host.Delete(rp.Id);
                rp = host.Create.NewReferencePlane(origin, origin + a.Multiply(10), cut.Negate(), view);
            }
            try { rp.Name = name; } catch { }
            if (workset != null && workset != WorksetId.InvalidWorksetId)
            {
                var wp = rp.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (wp != null && !wp.IsReadOnly) wp.Set(workset.IntegerValue);
            }
            Log.Info($"[Mapping] created reference plane '{name}' (normal {rp.Normal.X:0.000},{rp.Normal.Y:0.000},{rp.Normal.Z:0.000})");
            return rp;
        }

        /// <summary>Diagnostics: the instance's height parameters as Revit reports them (value, read-only).</summary>
        private static string OffsetParams(FamilyInstance fi)
        {
            var parts = new List<string>();
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM,
                                        BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM })
            {
                try
                {
                    var prm = fi.get_Parameter(bip);
                    if (prm == null) continue;
                    string v = prm.StorageType == Autodesk.Revit.DB.StorageType.Double ? $"{prm.AsDouble() * MappingMath.MmPerFoot:N0} mm" : prm.AsValueString();
                    parts.Add($"{prm.Definition.Name} = {v}{(prm.IsReadOnly ? " (read-only)" : "")}");
                }
                catch { }
            }
            return parts.Count == 0 ? "no height parameters" : string.Join(", ", parts);
        }

        /// <summary>Elevation / offset from host: whichever of the host offset parameters the family has (then SnapTo corrects any remainder).</summary>
        private static void SetHostOffset(FamilyInstance fi, double offset)
        {
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM })
            {
                var p = fi.get_Parameter(bip);
                if (p != null && !p.IsReadOnly) { p.Set(offset); return; }
            }
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
            if (linkDoc == null || symbol == null) { r.Warnings.Add(linkDoc == null ? "The link isn't loaded." : "The local family type isn't in the model any more."); return r; }
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
                    var quiet = new Quiet().On(tx);
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
                    foreach (var e in quiet.Errors) if (!r.Warnings.Contains("Revit: " + e)) r.Warnings.Add("Revit: " + e);
                    if (quiet.RolledBack) { r.Failed += r.Placed; r.Placed = 0; r.Last = null; }
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
            if (linkDoc == null || symbol == null) { r.Warnings.Add(linkDoc == null ? "The link isn't loaded." : "The local family type isn't in the model any more."); return r; }
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
                    MappedFamilyStore.Write(ourEl, inst.UniqueId, s.UniqueId, s.Id.GetIdValue(), rule.Id, user, MappedFamilyStore.Linked,
                                            TypeKey(s), SourceFrame(s, toHost), FrameOf(ourEl));
                    usedS.Add(p.Item2); usedO.Add(p.Item3);
                    r.Linked++;
                    r.Last = ourEl.Id;
                }
                tx.Commit();
            }
            r.Failed = sources.Count - r.Linked;   // consultant elements with none of ours nearby
            Log.Info($"[Mapping] link existing {rule.TargetName} to {rule.SourceFamily} : {rule.SourceType}: linked {r.Linked}, " +
                     $"{r.Failed} consultant elements without one within {toleranceMm:0} mm, {ours.Count - usedO.Count} local families left, {r.Skipped} checked out by others");
            return r;
        }

        /// <summary>Rule key of a consultant element's type (category | family | type), or null.</summary>
        internal static string TypeKey(Element source)
        {
            if (source is FamilyInstance fi && fi.Symbol != null && fi.Category != null)
                return FamilyMapRule.Key(fi.Category.Name, fi.Symbol.FamilyName, fi.Symbol.Name);
            return null;
        }

        /// <summary>Our family's frame (family transform, host coordinates).</summary>
        internal static Frame FrameOf(Element el)
        {
            if (el is FamilyInstance fi)
            {
                try
                {
                    var t = fi.GetTotalTransform();
                    return new Frame
                    {
                        O = new[] { t.Origin.X, t.Origin.Y, t.Origin.Z }, X = new[] { t.BasisX.X, t.BasisX.Y, t.BasisX.Z },
                        Y = new[] { t.BasisY.X, t.BasisY.Y, t.BasisY.Z }, Z = new[] { t.BasisZ.X, t.BasisZ.Y, t.BasisZ.Z },
                    };
                }
                catch { }
            }
            var p = Position(el);
            return p == null ? null : new Frame { O = p };
        }

        /// <summary>
        /// Moves one of ours to the expected frame (Link Sync approve). In place when Revit allows it (plan move +
        /// rotation about the vertical; level-based families move vertically too); a work-plane family that has to
        /// change height or tilt can't leave its plane, so it's re-placed: same type, writable instance parameters
        /// copied (not Perseus_ID), workset kept, old one deleted. Returns the instance now standing there (maybe new)
        /// or null with a warning. Inside the caller's transaction.
        /// </summary>
        internal static FamilyInstance MoveTo(Document host, FamilyInstance fi, Frame expected, double tolXyMm, double tolZMm,
                                              RevitLinkInstance inst, FamilyMapRule rule, Element source, List<Level> levels, string user,
                                              out string warning, out bool replaced)
        {
            warning = null; replaced = false;
            var now = FrameOf(fi);
            if (now == null) { warning = "the local family has no location"; return null; }
            bool upright = !MappingMath.IsTilted(expected) && !MappingMath.IsTilted(now) && Math.Sign(expected.Z[2]) == Math.Sign(now.Z[2]);
            if (upright)
            {
                try
                {
                    using (var st = new SubTransaction(host))
                    {
                        st.Start();
                        var d = new XYZ(expected.O[0] - now.O[0], expected.O[1] - now.O[1], expected.O[2] - now.O[2]);
                        if (d.GetLength() > 1e-9) ElementTransformUtils.MoveElement(host, fi.Id, d);
                        double a = MappingMath.PlanAngleBetween(now, expected);
                        if (Math.Abs(a) > 1e-6)
                        {
                            var o = new XYZ(expected.O[0], expected.O[1], expected.O[2]);
                            ElementTransformUtils.RotateElement(host, fi.Id, Line.CreateBound(o, o + XYZ.BasisZ), a);
                        }
                        host.Regenerate();
                        var after = FrameOf(fi);
                        if (after != null && MappingMath.Within(MappingMath.Delta(after, expected), Math.Min(tolXyMm, 5), Math.Min(tolZMm, 5)))
                        {
                            st.Commit();
                            return fi;
                        }
                        st.RollBack();
                    }
                }
                catch (Exception ex) { Log.Info($"[LinkSync] in-place move of {fi.Id.GetIdValue()} refused: {ex.Message}"); }
            }
            // Re-place: new instance at the expected frame, carrying ours over.
            var symbol = fi.Symbol;
            var ws = host.IsWorkshared ? fi.WorksetId : null;
            var placeRule = new FamilyMapRule
            {
                Id = rule.Id, SourceCategory = rule.SourceCategory, SourceFamily = rule.SourceFamily, SourceType = rule.SourceType,
                TargetSymbolUid = symbol.UniqueId, TargetName = rule.TargetName, OffsetMm = rule.OffsetMm, RotationDeg = rule.RotationDeg,
            };
            var made = Place(host, inst, placeRule, symbol, source, levels, ws, user, out warning);
            if (made == null) return null;
            CopyInstanceParameters(fi, made);
            var old = MappedFamilyStore.Get(fi);
            if (old != null)
            {
                var d = MappedFamilyStore.Get(made)?.Data;
                if (d != null) { d.How = old.How; MappedFamilyStore.Write(made, d); }
            }
            host.Delete(fi.Id);
            replaced = true;
            return made;
        }

        /// <summary>Writable instance parameters from one of ours to its replacement (Mark, comments, shared / project parameters; not Perseus_ID).</summary>
        internal static void CopyInstanceParameters(Element from, Element to)
        {
            foreach (Parameter p in from.Parameters)
            {
                try
                {
                    if (p.IsReadOnly || !p.HasValue || p.Definition == null) continue;
                    var name = p.Definition.Name;
                    if (string.Equals(name, naming.ParameterNames.For(from.Document, naming.ParameterNames.PerseusId), StringComparison.OrdinalIgnoreCase)) continue;
                    var bip = p.Definition is InternalDefinition idf ? idf.BuiltInParameter : BuiltInParameter.INVALID;
                    bool builtIn = bip != BuiltInParameter.INVALID;
                    if (builtIn && bip != BuiltInParameter.ALL_MODEL_MARK && bip != BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) continue;
                    var q = p.IsShared ? to.get_Parameter(p.GUID) : builtIn ? to.get_Parameter(bip) : to.LookupParameter(name);
                    if (q == null || q.IsReadOnly || q.StorageType != p.StorageType) continue;
                    switch (p.StorageType)
                    {
                        case Autodesk.Revit.DB.StorageType.String: q.Set(p.AsString()); break;
                        case Autodesk.Revit.DB.StorageType.Integer: q.Set(p.AsInteger()); break;
                        case Autodesk.Revit.DB.StorageType.Double: q.Set(p.AsDouble()); break;
                        case Autodesk.Revit.DB.StorageType.ElementId: q.Set(p.AsElementId()); break;
                    }
                }
                catch { /* best effort */ }
            }
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

        /// <summary>
        /// No Revit dialogs while placing: warnings are dropped; errors are resolved with Revit's default resolution when it has
        /// one (usually deleting the offending family), else the transaction is rolled back. Either way the error text is kept
        /// in Errors so the caller can report it. Pair with FailureHandlingOptions.SetClearAfterRollback(true).
        /// </summary>
        internal class Quiet : IFailuresPreprocessor
        {
            internal readonly List<string> Errors = new List<string>();
            internal bool RolledBack;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                bool resolved = false, stuck = false;
                foreach (var f in a.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) { a.DeleteWarning(f); continue; }
                    string text = f.GetDescriptionText();
                    if (!Errors.Contains(text)) Errors.Add(text);
                    if (f.HasResolutions()) { try { a.ResolveFailure(f); resolved = true; continue; } catch { } }
                    stuck = true;
                }
                if (Errors.Count > 0) Log.Info($"[Mapping] Revit errors while placing: {string.Join(" | ", Errors)}" + (stuck ? " (rolled back)" : " (resolved)"));
                if (stuck) { RolledBack = true; return FailureProcessingResult.ProceedWithRollBack; }
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }

            /// <summary>Puts this preprocessor on a transaction (no dialogs, also after a roll back).</summary>
            internal Quiet On(Transaction tx)
            {
                var fo = tx.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(this);
                fo.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fo);
                return this;
            }
        }
    }
}
