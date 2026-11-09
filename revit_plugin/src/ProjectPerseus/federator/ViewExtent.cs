using System;
using Autodesk.Revit.DB;

namespace ProjectPerseus.federator
{
    /// <summary>
    /// The part of model space a view shows, for "is this box visible here?": floor plan = view range (top clip down to
    /// view depth), reflected ceiling plan = cut plane up to top / view depth, plus the crop region when cropped; section / elevation = crop box including its far clip depth;
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
                        const double Far = double.MaxValue / 4;
                        if (plan.ViewType == ViewType.CeilingPlan)
                        {
                            // Reflected ceiling plan: looks UP, from the cut plane to the top / view depth above it.
                            double cut = PlaneZ(view.Document, plan, vr, PlanViewPlane.CutPlane, 0);
                            double top = PlaneZ(view.Document, plan, vr, PlanViewPlane.TopClipPlane, Far);
                            double depth = PlaneZ(view.Document, plan, vr, PlanViewPlane.ViewDepthPlane, Far);
                            e._zMin = cut;
                            e._zMax = Math.Max(cut, Math.Max(top, depth));
                        }
                        else
                        {
                            // Floor / engineering / area plan: looks DOWN, from the top clip to the view depth below.
                            double top = PlaneZ(view.Document, plan, vr, PlanViewPlane.TopClipPlane, Far);
                            double bottom = PlaneZ(view.Document, plan, vr, PlanViewPlane.BottomClipPlane, -Far);
                            double depth = PlaneZ(view.Document, plan, vr, PlanViewPlane.ViewDepthPlane, -Far);
                            e._zMin = Math.Min(bottom, depth);
                            e._zMax = Math.Max(top, Math.Max(bottom, depth));
                        }
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

        /// <summary>One-line description for diagnostics (feet): z range and crop / section box.</summary>
        internal string Describe()
        {
            if (_none) return "no model";
            if (_all) return "everything";
            string Z(double d) => Math.Abs(d) > 1e30 ? "unlimited" : d.ToString("F2");
            string s = $"z {Z(_zMin)}..{Z(_zMax)}";
            if (_toLocal != null)
            {
                var o = _toLocal.Inverse;
                s += $", box local ({_localMin.X:F1},{_localMin.Y:F1},{_localMin.Z:F1})..({_localMax.X:F1},{_localMax.Y:F1},{_localMax.Z:F1})" +
                     $" origin ({o.Origin.X:F1},{o.Origin.Y:F1},{o.Origin.Z:F1}) x ({o.BasisX.X:F2},{o.BasisX.Y:F2},{o.BasisX.Z:F2})" +
                     $" z ({o.BasisZ.X:F2},{o.BasisZ.Y:F2},{o.BasisZ.Z:F2}){(_checkLocalZ ? " +depth" : "")}";
            }
            return s;
        }

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

        /// <param name="unlimited">Value for an unlimited / unset plane (far away in the direction the view looks).</param>
        private static double PlaneZ(Document doc, ViewPlan plan, PlanViewRange vr, PlanViewPlane plane, double unlimited)
        {
            ElementId levelId = vr.GetLevelId(plane);
            if (levelId == null || levelId == ElementId.InvalidElementId) return unlimited;
            // Special ids (PlanViewRange.Unlimited / LevelBelow / LevelAbove...) aren't levels.
            var level = doc.GetElement(levelId) as Level;
            if (level == null)
            {
                if (plane == PlanViewPlane.CutPlane && plan.GenLevel != null) return plan.GenLevel.ProjectElevation + vr.GetOffset(plane);
                return unlimited;
            }
            return level.ProjectElevation + vr.GetOffset(plane);
        }
    }
}
