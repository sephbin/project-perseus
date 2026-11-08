using System;
using Autodesk.Revit.DB;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// The part of model space a view shows, for "is this box visible here?": plan = view range (top clip to view
    /// depth) and the crop region when cropped; section / elevation = crop box including its far clip depth;
    /// 3D = section box when active, else everything. Other views (sheets, schedules, drafting) show no model.
    /// Coarse by design (axis boxes, no visibility/graphics overrides): good enough to decide whether to zoom in the
    /// current view or open the federator 3D view.
    /// </summary>
    internal class ViewExtent
    {
        private bool _none, _all;
        private double _zMin = double.MinValue, _zMax = double.MaxValue;
        private Transform _toLocal;            // host → crop / section box coordinates
        private XYZ _localMin, _localMax;
        private bool _checkLocalZ;

        internal static ViewExtent For(View view)
        {
            var e = new ViewExtent();
            try
            {
                switch (view)
                {
                    case View3D v3 when !v3.IsTemplate:
                        if (v3.IsSectionBoxActive) e.SetBox(v3.GetSectionBox(), true);
                        else e._all = true;
                        break;
                    case ViewPlan plan when plan.GenLevel != null:
                        var vr = plan.GetViewRange();
                        double top = PlaneZ(view.Document, plan, vr, PlanViewPlane.TopClipPlane);
                        double bottom = PlaneZ(view.Document, plan, vr, PlanViewPlane.ViewDepthPlane);
                        e._zMin = Math.Min(top, bottom);
                        e._zMax = Math.Max(top, bottom);
                        if (plan.CropBoxActive) e.SetBox(plan.CropBox, false);
                        break;
                    default:
                        if (view.ViewType == ViewType.Section || view.ViewType == ViewType.Elevation || view.ViewType == ViewType.Detail)
                            e.SetBox(view.CropBox, true);
                        else e._none = true;
                        break;
                }
            }
            catch { e._all = true; }
            return e;
        }

        internal bool ShowsModel => !_none;

        /// <summary>Does the axis-aligned host-coordinate box overlap what the view shows?</summary>
        internal bool Contains(XYZ min, XYZ max)
        {
            if (_none) return false;
            if (_all) return true;
            if (max.Z < _zMin || min.Z > _zMax) return false;
            if (_toLocal == null) return true;

            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = _toLocal.OfPoint(new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            if (x1 < _localMin.X || x0 > _localMax.X || y1 < _localMin.Y || y0 > _localMax.Y) return false;
            if (_checkLocalZ && (z1 < _localMin.Z || z0 > _localMax.Z)) return false;
            return true;
        }

        private void SetBox(BoundingBoxXYZ box, bool checkZ)
        {
            if (box == null) { _all = true; return; }
            _toLocal = (box.Transform ?? Transform.Identity).Inverse;
            _localMin = new XYZ(Math.Min(box.Min.X, box.Max.X), Math.Min(box.Min.Y, box.Max.Y), Math.Min(box.Min.Z, box.Max.Z));
            _localMax = new XYZ(Math.Max(box.Min.X, box.Max.X), Math.Max(box.Min.Y, box.Max.Y), Math.Max(box.Min.Z, box.Max.Z));
            _checkLocalZ = checkZ;
        }

        private static double PlaneZ(Document doc, ViewPlan plan, PlanViewRange vr, PlanViewPlane plane)
        {
            ElementId levelId = vr.GetLevelId(plane);
            var level = levelId != null && levelId != ElementId.InvalidElementId ? doc.GetElement(levelId) as Level : null;
            if (level == null)
                // Unlimited / unset: treat as far away in the plane's direction.
                return plane == PlanViewPlane.TopClipPlane ? double.MaxValue / 4 : double.MinValue / 4;
            return level.Elevation + vr.GetOffset(plane);
        }
    }
}
