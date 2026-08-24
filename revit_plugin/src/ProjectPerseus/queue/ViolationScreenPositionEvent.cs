using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.ui;
using ProjectPerseus.violations;

namespace ProjectPerseus.queue
{
    // Runs on the Revit main thread every ~500 ms.
    // Projects element locations from model space to normalised screen coordinates
    // and pushes them to ViolationOverlayController for WPF canvas rendering.
    // Only active in Symbol mode.  Skips 3D views (projection is unreliable).
    // Filters elements outside the view's visible depth range before projecting.
    internal class ViolationScreenPositionEvent : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            try
            {
                if (!ViolationHighlightController.IsEnabled
                    || ViolationHighlightServer.Instance.CurrentMode != ViolationDisplayMode.Symbol)
                {
                    ViolationOverlayController.UpdateMarkers(null);
                    return;
                }

                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) { ViolationOverlayController.UpdateMarkers(null); return; }

                var activeView = uidoc.ActiveView;
                if (activeView == null) { ViolationOverlayController.UpdateMarkers(null); return; }

                // 3D views: orthographic projection via dot-product doesn't account for the
                // camera transform, so symbols land in wrong positions.  Suppress them.
                if (activeView.ViewType == ViewType.ThreeD)
                {
                    ViolationOverlayController.UpdateMarkers(null);
                    return;
                }

                // Find the UIView for the active view so we can get zoom corners.
                UIView uiView = null;
                foreach (UIView uv in uidoc.GetOpenUIViews())
                {
                    if (uv.ViewId == activeView.Id) { uiView = uv; break; }
                }
                if (uiView == null) { ViolationOverlayController.UpdateMarkers(null); return; }

                var corners = uiView.GetZoomCorners();
                if (corners == null || corners.Count < 2) { ViolationOverlayController.UpdateMarkers(null); return; }

                // View basis vectors: RightDirection = screen X, UpDirection = screen Y.
                XYZ right = activeView.RightDirection;
                XYZ up    = activeView.UpDirection;

                double minU = corners[0].DotProduct(right);
                double maxU = corners[1].DotProduct(right);
                double minV = corners[0].DotProduct(up);
                double maxV = corners[1].DotProduct(up);

                double rangeU = maxU - minU;
                double rangeV = maxV - minV;
                if (Math.Abs(rangeU) < 1e-9 || Math.Abs(rangeV) < 1e-9)
                {
                    ViolationOverlayController.UpdateMarkers(null);
                    return;
                }

                // Build the depth filter for this view type (plan Z-range or section crop depth).
                DepthFilter depth = BuildDepthFilter(activeView);

                var snapshot = ViolationHighlightServer.Instance.GetHighlightsSnapshot();
                var markers  = new List<OverlayMarker>(snapshot.Count);

                foreach (var h in snapshot)
                {
                    // Prefer Location point; fall back to BBox centre.
                    XYZ pt = h.Location;
                    if (pt == null && h.BBox != null)
                        pt = (h.BBox.Min + h.BBox.Max) * 0.5;
                    if (pt == null) continue;

                    // Skip elements outside the view's visible depth range.
                    if (!depth.Passes(h, pt)) continue;

                    double u = (pt.DotProduct(right) - minU) / rangeU;
                    double v = (pt.DotProduct(up)    - minV) / rangeV;

                    // Cull markers well outside the visible area.
                    if (u < -0.05 || u > 1.05 || v < -0.05 || v > 1.05) continue;

                    // WPF canvas Y=0 is the top; model up is the bottom of NormY=0.
                    markers.Add(new OverlayMarker
                    {
                        NormX = u,
                        NormY = 1.0 - v,
                        R     = h.Color.Red,
                        G     = h.Color.Green,
                        B     = h.Color.Blue,
                    });
                }

                ViolationOverlayController.UpdateMarkers(markers);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationScreenPositionEvent] {ex.Message}");
            }
        }

        // ── Depth filter ─────────────────────────────────────────────────────────

        private struct DepthFilter
        {
            internal enum Kind { None, PlanZ, SectionDepth }

            internal Kind   FilterKind;
            internal double MinZ, MaxZ;
            internal XYZ    DepthAxis, DepthOrigin; // SectionDepth only

            // Returns true if the element passes the depth visibility test.
            internal bool Passes(ViolationHighlight h, XYZ pt)
            {
                switch (FilterKind)
                {
                    case Kind.PlanZ:
                        // Element BBox must overlap the plan view-range Z interval.
                        double elemMinZ = h.BBox != null ? h.BBox.Min.Z : pt.Z;
                        double elemMaxZ = h.BBox != null ? h.BBox.Max.Z : pt.Z;
                        return elemMaxZ >= MinZ && elemMinZ <= MaxZ;

                    case Kind.SectionDepth:
                        // Element point must fall within the section crop depth.
                        double d = pt.Subtract(DepthOrigin).DotProduct(DepthAxis);
                        return d >= MinZ && d <= MaxZ;

                    default:
                        return true;
                }
            }
        }

        private static DepthFilter BuildDepthFilter(View view)
        {
            var f = new DepthFilter { FilterKind = DepthFilter.Kind.None };
            try
            {
                var vt = view.ViewType;

                if (vt == ViewType.FloorPlan || vt == ViewType.CeilingPlan)
                {
                    var planView = (ViewPlan)view;
                    Level level  = planView.GenLevel;
                    if (level == null) return f;

                    var    vr   = planView.GetViewRange();
                    double lz   = level.Elevation;
                    double topZ = lz + vr.GetOffset(PlanViewPlane.TopClipPlane);
                    double btmZ = lz + vr.GetOffset(PlanViewPlane.BottomClipPlane);

                    f.FilterKind = DepthFilter.Kind.PlanZ;
                    f.MinZ       = Math.Min(topZ, btmZ);
                    f.MaxZ       = Math.Max(topZ, btmZ);
                }
                else if (vt == ViewType.Section || vt == ViewType.Elevation)
                {
                    var cb = view.CropBox;
                    f.FilterKind  = DepthFilter.Kind.SectionDepth;
                    f.DepthAxis   = cb.Transform.BasisZ;
                    f.DepthOrigin = cb.Transform.Origin;
                    f.MinZ        = Math.Min(cb.Min.Z, cb.Max.Z);
                    f.MaxZ        = Math.Max(cb.Min.Z, cb.Max.Z);
                }
            }
            catch { /* fallback: no depth filter */ }
            return f;
        }

        public string GetName() => "Perseus Violation Screen Position";
    }
}
