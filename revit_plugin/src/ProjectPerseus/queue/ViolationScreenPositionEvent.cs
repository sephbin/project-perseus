using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.ui;
using ProjectPerseus.violations;

namespace ProjectPerseus.queue
{
    // Runs on the Revit main thread every ~200 ms.
    // Projects each violating element's bounding box from model space to a normalised screen
    // rect (centre + extent) and pushes them to ViolationOverlayController, which draws a ring
    // around each one. Only active in Symbol mode.  Skips 3D views (projection is unreliable).
    // Filters elements outside the view's visible depth range before projecting.
    // If the view's zoom corners changed since the previous tick the view is being navigated:
    // the overlay is told to hide (SetViewMoving) and markers are only sent once it is still again.
    internal class ViolationScreenPositionEvent : IExternalEventHandler
    {
        private static ElementId _lastViewId;
        private static XYZ       _lastCorner0, _lastCorner1;

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

                if (ViewMovedSinceLastTick(activeView.Id, corners[0], corners[1]))
                {
                    ViolationOverlayController.SetViewMoving();
                    return;
                }

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
                    // Depth test point: Location point, else BBox centre.
                    XYZ pt = h.Location;
                    if (pt == null && h.BBox != null)
                        pt = (h.BBox.Min + h.BBox.Max) * 0.5;
                    if (pt == null) continue;

                    // Skip elements outside the view's visible depth range.
                    if (!depth.Passes(h, pt)) continue;

                    // Screen rect of the element: its bounding box's 8 corners projected onto the
                    // view plane (so the ring is centred on, and encloses, the whole element).
                    double u0, u1, v0, v1;
                    if (h.BBox != null)
                        ProjectBox(h.BBox, right, up, out u0, out u1, out v0, out v1);
                    else
                    {
                        u0 = u1 = pt.DotProduct(right);
                        v0 = v1 = pt.DotProduct(up);
                    }
                    u0 = (u0 - minU) / rangeU; u1 = (u1 - minU) / rangeU;
                    v0 = (v0 - minV) / rangeV; v1 = (v1 - minV) / rangeV;
                    if (u0 > u1) { var t = u0; u0 = u1; u1 = t; }
                    if (v0 > v1) { var t = v0; v0 = v1; v1 = t; }

                    // Cull elements whose rect (plus a margin for the ring) misses the visible area.
                    const double margin = 0.05;
                    if (u1 < -margin || u0 > 1 + margin || v1 < -margin || v0 > 1 + margin) continue;

                    // WPF canvas Y=0 is the top; model up is the bottom of NormY=0.
                    markers.Add(new OverlayMarker
                    {
                        NormX = (u0 + u1) * 0.5,
                        NormY = 1.0 - (v0 + v1) * 0.5,
                        NormW = u1 - u0,
                        NormH = v1 - v0,
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

        // True when the active view or its zoom corners differ from the previous tick.
        // The first tick after a reset counts as moving, so markers only appear once the view is still.
        private static bool ViewMovedSinceLastTick(ElementId viewId, XYZ c0, XYZ c1)
        {
            const double tol = 1e-6;
            bool moved = _lastViewId == null || _lastViewId != viewId
                      || !_lastCorner0.IsAlmostEqualTo(c0, tol)
                      || !_lastCorner1.IsAlmostEqualTo(c1, tol);
            _lastViewId  = viewId;
            _lastCorner0 = c0;
            _lastCorner1 = c1;
            return moved;
        }

        private static void ProjectBox(BoundingBoxXYZ bb, XYZ right, XYZ up,
                                       out double u0, out double u1, out double v0, out double v1)
        {
            u0 = v0 = double.MaxValue;
            u1 = v1 = double.MinValue;
            var tf = bb.Transform;
            for (int i = 0; i < 8; i++)
            {
                var local = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                    (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                    (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                var p = tf != null ? tf.OfPoint(local) : local;
                double u = p.DotProduct(right), v = p.DotProduct(up);
                if (u < u0) u0 = u;
                if (u > u1) u1 = u;
                if (v < v0) v0 = v;
                if (v > v1) v1 = v;
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
