using Autodesk.Revit.DB;
using ProjectPerseus.revit;
using ProjectPerseus.util;
using Element = Autodesk.Revit.DB.Element;

namespace ProjectPerseus.ids
{
    /// <summary>Where an element is, in mm in project (internal) coordinates, for numbering by position.</summary>
    internal static class ElementPosition
    {
        private const double FeetToMm = 304.8;

        // Location point; the midpoint of a wall or other line-based element; otherwise the bounding-box centre.
        // Elements with no geometry at all sit at the origin and so sort by ElementId among themselves.
        internal static PositionSample Of(Element el)
        {
            XYZ p = null;
            try
            {
                if (el.Location is LocationPoint lp) p = lp.Point;
                else if (el.Location is LocationCurve lc) p = lc.Curve.Evaluate(0.5, true);
                else
                {
                    var bb = el.get_BoundingBox(null);
                    if (bb != null) p = (bb.Min + bb.Max) * 0.5;
                }
            }
            catch { /* unreadable geometry: treat as unpositioned */ }

            return new PositionSample
            {
                Id = el.Id.GetIdValue(),
                X = p == null ? 0 : p.X * FeetToMm,
                Y = p == null ? 0 : p.Y * FeetToMm,
                Z = p == null ? 0 : p.Z * FeetToMm,
            };
        }
    }
}
