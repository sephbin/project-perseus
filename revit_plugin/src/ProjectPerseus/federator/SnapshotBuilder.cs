using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using Element = Autodesk.Revit.DB.Element;
using StorageType = Autodesk.Revit.DB.StorageType;
using ProjectPerseus.revit;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// Reads every model element of a document (a loaded link's document, or an archived version opened by
    /// OldModelOpener) into a LinkSnapshot: identity, family/type/level/workset, bounding box, location, a solid
    /// signature (volume / area / face and edge counts) and instance + type parameter values as display strings.
    /// Read-only; must run in a Revit API context. Element-level failures are logged and the element is skipped.
    /// </summary>
    internal static class SnapshotBuilder
    {
        /// <param name="fileStamp">SnapshotCache.StampOf(sourcePath) taken when the file was read, or null for a
        /// snapshot that must not be cached (a loaded link: its file on disk may already be newer).</param>
        /// <param name="progress">(done, total), called every few hundred elements.</param>
        internal static LinkSnapshot Build(Document doc, string sourcePath, string fileStamp, Action<int, int> progress = null)
        {
            var started = DateTime.UtcNow;
            var snap = new LinkSnapshot
            {
                Title = doc.Title,
                SourcePath = sourcePath,
                FileStamp = fileStamp,
                TakenUtc = started,
            };

            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
            var typeParams = new Dictionary<long, Dictionary<string, string>>();
            var names = new Dictionary<long, string>();
            WorksetTable worksets = doc.IsWorkshared ? doc.GetWorksetTable() : null;

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .ToElements();

            int done = 0, skipped = 0;
            foreach (Element el in elements)
            {
                done++;
                if (progress != null && done % 250 == 0) progress(done, elements.Count);
                try
                {
                    var e = Read(doc, el, options, worksets, typeParams, names);
                    if (e != null) snap.Elements.Add(e);
                }
                catch (Exception ex)
                {
                    skipped++;
                    if (skipped <= 20) Log.Info($"[Federator] snapshot skipped element {el.Id.GetIdValue()}: {ex.Message}");
                }
            }
            progress?.Invoke(elements.Count, elements.Count);
            Log.Info($"[Federator] snapshot of '{doc.Title}': {snap.Elements.Count} elements ({skipped} failed) " +
                     $"in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
            return snap;
        }

        private static ElementSnapshot Read(Document doc, Element el, Options options, WorksetTable worksets,
                                            Dictionary<long, Dictionary<string, string>> typeParams, Dictionary<long, string> names)
        {
            Category cat = el.Category;
            if (cat == null || cat.CategoryType != CategoryType.Model) return null;
            if (el is RevitLinkInstance) return null; // nested links are out of scope
            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb == null) return null; // non-geometric (e.g. area schemes, analytical leftovers)

            var e = new ElementSnapshot
            {
                UniqueId = el.UniqueId,
                Id = el.Id.GetIdValue(),
                Category = cat.Name,
                Min = new[] { bb.Min.X, bb.Min.Y, bb.Min.Z },
                Max = new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
            };

            ElementId typeId = el.GetTypeId();
            if (typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is ElementType et)
            {
                e.Family = et.FamilyName;
                e.Type = et.Name;
            }
            else e.Type = el.Name;

            if (el.LevelId != ElementId.InvalidElementId) e.Level = NameOf(doc, el.LevelId, names);
            if (worksets != null && el.WorksetId != WorksetId.InvalidWorksetId)
            {
                try { e.Workset = worksets.GetWorkset(el.WorksetId)?.Name; } catch { /* not a user workset */ }
            }

            switch (el.Location)
            {
                case LocationPoint lp:
                    e.Location = new[] { lp.Point.X, lp.Point.Y, lp.Point.Z };
                    break;
                case LocationCurve lc when lc.Curve != null && lc.Curve.IsBound:
                    XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                    e.Location = new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z };
                    break;
            }

            GeometryElement geo = null;
            try { geo = el.get_Geometry(options); } catch { /* no geometry */ }
            ReadSolids(geo, e);
            if (geo != null) e.Obb = OrientedBox(el, geo, bb);
            ReadParameters(el.Parameters, e.Parameters, "");
            if (typeId != ElementId.InvalidElementId)
            {
                long key = typeId.GetIdValue();
                if (!typeParams.TryGetValue(key, out var tp))
                {
                    tp = new Dictionary<string, string>();
                    Element type = doc.GetElement(typeId);
                    if (type != null) ReadParameters(type.Parameters, tp, LinkDiff.TypeParameterPrefix);
                    typeParams[key] = tp;
                }
                foreach (var kv in tp) e.Parameters[kv.Key] = kv.Value;
            }
            return e;
        }

        // Oriented box: frame from the element itself (family instance rotation, else its location curve direction), extents
        // from its geometry (solid edges, meshes, curves; a nested family's symbol box with its transform). Kept only when
        // clearly tighter in plan than the axis-aligned box. Rotation about Z only: plans are where the boxes get messy.
        private const int MaxObbPoints = 20000;

        private static double[] OrientedBox(Element el, GeometryElement geo, BoundingBoxXYZ bb)
        {
            XYZ dir = null;
            try
            {
                if (el is FamilyInstance fi) dir = fi.GetTransform().BasisX;
                else if (el.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
                    dir = lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0);
            }
            catch { return null; }
            if (dir == null) return null;
            double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (len < 1e-9) return null;
            double cx = dir.X / len, cy = dir.Y / len;
            if (Math.Abs(cx) > 0.9998 || Math.Abs(cy) > 0.9998) return null;   // square to the axes (within ~1 degree)
            double boxArea = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);
            if (boxArea < 1e-9) return null;

            var pts = new List<XYZ>();
            if (!CollectPoints(geo, pts, 0) || pts.Count == 0) return null;
            double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
            foreach (var p in pts)
            {
                double a = p.X * cx + p.Y * cy, b = -p.X * cy + p.Y * cx;
                a0 = Math.Min(a0, a); a1 = Math.Max(a1, a);
                b0 = Math.Min(b0, b); b1 = Math.Max(b1, b);
                z0 = Math.Min(z0, p.Z); z1 = Math.Max(z1, p.Z);
            }
            if ((a1 - a0) * (b1 - b0) > 0.85 * boxArea) return null;
            double la = a1 - a0, lb = b1 - b0;
            return new[]
            {
                a0 * cx - b0 * cy, a0 * cy + b0 * cx, z0,
                la * cx, la * cy, 0,
                -lb * cy, lb * cx, 0,
                0, 0, z1 - z0,
            };
        }

        // False when the geometry has too many points to read (then no oriented box: a partial set would be too small).
        private static bool CollectPoints(GeometryElement geo, List<XYZ> pts, int depth)
        {
            if (depth > 4) return true;
            foreach (GeometryObject g in geo)
            {
                switch (g)
                {
                    case Solid s when s.Volume > 1e-9:
                        foreach (Edge ed in s.Edges) pts.AddRange(ed.Tessellate());
                        break;
                    case Mesh m:
                        for (int i = 0; i < m.Vertices.Count; i++) pts.Add(m.Vertices[i]);
                        break;
                    case Curve c when c.IsBound:
                        pts.AddRange(c.Tessellate());
                        break;
                    case GeometryInstance gi:
                        // The symbol's own box moved by the instance transform: exact for the usual family whose geometry is
                        // square to its own axes, and cheap.
                        var box = gi.SymbolGeometry?.GetBoundingBox();
                        if (box != null)
                        {
                            var t = gi.Transform.Multiply(box.Transform ?? Transform.Identity);
                            for (int i = 0; i < 8; i++)
                                pts.Add(t.OfPoint(new XYZ((i & 1) == 0 ? box.Min.X : box.Max.X,
                                                          (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                                                          (i & 4) == 0 ? box.Min.Z : box.Max.Z)));
                        }
                        else
                        {
                            var inner = gi.GetInstanceGeometry();
                            if (inner != null && !CollectPoints(inner, pts, depth + 1)) return false;
                        }
                        break;
                }
                if (pts.Count > MaxObbPoints) return false;
            }
            return true;
        }

        private static void ReadSolids(GeometryElement geo, ElementSnapshot e)
        {
            if (geo == null) return;
            double volume = 0, area = 0;
            int faces = 0, edges = 0, solids = 0;
            Accumulate(geo, ref volume, ref area, ref faces, ref edges, ref solids, 0);
            if (solids == 0) return;
            e.Volume = volume;
            e.Area = area;
            e.FaceCount = faces;
            e.EdgeCount = edges;
        }

        private static void Accumulate(GeometryElement geo, ref double volume, ref double area, ref int faces, ref int edges,
                                       ref int solids, int depth)
        {
            if (depth > 4) return;
            foreach (GeometryObject g in geo)
            {
                if (g is Solid s && s.Volume > 1e-9)
                {
                    solids++;
                    volume += s.Volume;
                    area += s.SurfaceArea;
                    faces += s.Faces.Size;
                    edges += s.Edges.Size;
                }
                else if (g is GeometryInstance gi)
                {
                    GeometryElement inner = gi.GetInstanceGeometry();
                    if (inner != null) Accumulate(inner, ref volume, ref area, ref faces, ref edges, ref solids, depth + 1);
                }
            }
        }

        private static void ReadParameters(ParameterSet parameters, Dictionary<string, string> into, string prefix)
        {
            foreach (Parameter p in parameters)
            {
                string name = p.Definition?.Name;
                if (string.IsNullOrEmpty(name) || p.StorageType == StorageType.None) continue;
                string key = prefix + name;
                // Two parameters with one name (e.g. a shared and a project parameter): the id keeps them apart and
                // is stable across versions of the same model.
                if (into.ContainsKey(key)) key += " #" + p.Id.GetIdValue();
                into[key] = ValueOf(p);
            }
        }

        private static string ValueOf(Parameter p)
        {
            if (!p.HasValue) return "";
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? "";
                case StorageType.Integer:
                    return p.AsValueString() ?? p.AsInteger().ToString();
                case StorageType.Double:
                    return p.AsValueString() ?? p.AsDouble().ToString("R");
                case StorageType.ElementId:
                    return p.AsValueString() ?? p.AsElementId().GetIdValue().ToString();
                default:
                    return "";
            }
        }

        private static string NameOf(Document doc, ElementId id, Dictionary<long, string> names)
        {
            long key = id.GetIdValue();
            if (!names.TryGetValue(key, out var n))
            {
                n = doc.GetElement(id)?.Name;
                names[key] = n;
            }
            return n;
        }
    }
}
