using System;
using System.Collections.Generic;
using Autodesk.Revit.DB.Architecture;
using ARDB = Autodesk.Revit.DB;

namespace ProjectPerseus.models.geometry
{
    public static class ElementGeometryExtractor
    {
        public static List<NamedGeometry> Extract(ARDB.Element element)
        {
            if (element == null) return null;

            var results = new List<NamedGeometry>();

            try { ExtractLocation(element, results); }
            catch (Exception ex) { Utl.WriteLog($"GeometryExtractor [{element.Id}] location: {ex.Message}"); }

            if (element is Room room)
            {
                try { ExtractRoomBoundary(room, results); }
                catch (Exception ex) { Utl.WriteLog($"GeometryExtractor [{element.Id}] room_boundary: {ex.Message}"); }
            }

            return results.Count > 0 ? results : null;
        }

        private static void ExtractLocation(ARDB.Element element, List<NamedGeometry> results)
        {
            var location = element.Location;

            if (location is ARDB.LocationPoint lp)
            {
                var pt = lp.Point;
                double rotation = 0;
                try { rotation = lp.Rotation; } catch { }

                GeoJsonGeometry geom = Math.Abs(rotation) > 1e-9
                    ? (GeoJsonGeometry)new RevitLocationPoint(pt.X, pt.Y, pt.Z, rotation)
                    : new GeoJsonPoint(pt.X, pt.Y, pt.Z);

                results.Add(new NamedGeometry("location_point", geom));
            }
            else if (location is ARDB.LocationCurve lc)
            {
                var geom = TessellateToLineString(lc.Curve);
                if (geom != null)
                    results.Add(new NamedGeometry("location_curve", geom));
            }
        }

        private static GeoJsonLineString TessellateToLineString(ARDB.Curve curve)
        {
            if (curve == null) return null;

            var pts = curve.Tessellate();
            if (pts == null || pts.Count < 2) return null;

            var coords = new double[pts.Count][];
            for (int i = 0; i < pts.Count; i++)
                coords[i] = new[] { pts[i].X, pts[i].Y, pts[i].Z };

            return new GeoJsonLineString(coords);
        }

        private static void ExtractRoomBoundary(Room room, List<NamedGeometry> results)
        {
            var opts = new ARDB.SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = ARDB.SpatialElementBoundaryLocation.Center
            };

            var segments = room.GetBoundarySegments(opts);
            if (segments == null || segments.Count == 0) return;

            var rings = new double[segments.Count][][];
            for (int r = 0; r < segments.Count; r++)
            {
                var ring = segments[r];
                var ringCoords = new List<double[]>();

                foreach (var seg in ring)
                {
                    var pts = seg.GetCurve().Tessellate();
                    for (int i = 0; i < pts.Count - 1; i++)
                        ringCoords.Add(new[] { pts[i].X, pts[i].Y, pts[i].Z });
                }

                if (ringCoords.Count == 0) continue;

                ringCoords.Add(ringCoords[0]);
                rings[r] = ringCoords.ToArray();
            }

            results.Add(new NamedGeometry("room_boundary", new GeoJsonPolygon(rings)));
        }
    }
}
