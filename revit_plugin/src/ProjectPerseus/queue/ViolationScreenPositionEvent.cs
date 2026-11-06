using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.markers;
using ProjectPerseus.models;
using ProjectPerseus.ui;
using ProjectPerseus.violations;

namespace ProjectPerseus.queue
{
    // Runs on the Revit main thread every ~200 ms.
    // Projects each violating element's (and each parameter marker's, see markers/MarkerEngine) bounding box from model space to a normalised screen
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
                bool violations = ViolationHighlightController.IsEnabled
                    && ViolationHighlightServer.Instance.CurrentMode == ViolationDisplayMode.Symbol;
                var markerHits = MarkerEngine.GetHitsSnapshot();
                if (!violations && markerHits.Count == 0)
                {
                    ViolationOverlayController.UpdateMarkers(null);
                    return;
                }

                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) { ViolationOverlayController.UpdateMarkers(null); return; }

                var activeView = uidoc.ActiveView;
                if (activeView == null) { ViolationOverlayController.UpdateMarkers(null); return; }

                // Only model views whose screen is a plain orthographic projection of the model.
                // Not 3D (dot-product projection ignores the camera, rings land in wrong places),
                // and not schedules, sheets, legends, drafting views etc. (no model geometry).
                if (!IsProjectableView(activeView.ViewType))
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

                var proj = new Projection
                {
                    Right = right,
                    Up    = up,
                    MinU  = corners[0].DotProduct(right),
                    MinV  = corners[0].DotProduct(up),
                };
                proj.RangeU = corners[1].DotProduct(right) - proj.MinU;
                proj.RangeV = corners[1].DotProduct(up)    - proj.MinV;
                if (Math.Abs(proj.RangeU) < 1e-9 || Math.Abs(proj.RangeV) < 1e-9)
                {
                    ViolationOverlayController.UpdateMarkers(null);
                    return;
                }

                var markers = new List<OverlayMarker>();

                // Parameter markers: MarkerEngine already collected only elements in this view, with a ring
                // index per element. Violation rings on the same element go outside them.
                // The view is still here (moving views returned above), so the parameter markers only need
                // projecting again when the hits or the zoom changed. Reusing the list also keeps the outline
                // arrays identical, which lets the overlay skip redrawing.
                Dictionary<string, int> ringsUsed;
                if (_cacheVersion == MarkerEngine.Version && _cacheViewId == activeView.Id
                    && _cacheCorner0 != null && _cacheCorner0.IsAlmostEqualTo(corners[0], 1e-6)
                    && _cacheCorner1.IsAlmostEqualTo(corners[1], 1e-6))
                {
                    ringsUsed = _cacheRings;
                    markers.AddRange(_cacheMarkers);
                }
                else
                {
                    ringsUsed = ProjectMarkerHits(markerHits, proj, markers);
                    _cacheVersion = MarkerEngine.Version;
                    _cacheViewId  = activeView.Id;
                    _cacheCorner0 = corners[0];
                    _cacheCorner1 = corners[1];
                    _cacheRings   = ringsUsed;
                    _cacheMarkers = new List<OverlayMarker>(markers);
                }

                if (violations)
                {
                    // Build the depth filter for this view type (plan Z-range or section crop depth).
                    DepthFilter depth = BuildDepthFilter(activeView);
                    foreach (var h in ViolationHighlightServer.Instance.GetHighlightsSnapshot())
                    {
                        // Depth test point: Location point, else BBox centre.
                        XYZ pt = h.Location;
                        if (pt == null && h.BBox != null)
                            pt = (h.BBox.Min + h.BBox.Max) * 0.5;
                        if (pt == null) continue;

                        // Skip elements outside the view's visible depth range.
                        if (!depth.Passes(h, pt)) continue;

                        OverlayMarker m;
                        if (!proj.TryProject(h.BBox, pt, out m)) continue;
                        m.R = h.Color.Red; m.G = h.Color.Green; m.B = h.Color.Blue;
                        if (h.Id != null && ringsUsed.TryGetValue(MarkerEngine.HostKey(h.Id), out int ring)) m.Ring = ring;
                        markers.Add(m);
                    }
                }

                ViolationOverlayController.UpdateMarkers(markers);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationScreenPositionEvent] {ex.Message}");
            }
        }

        // Last projection of MarkerEngine's hits (see Execute).
        private static int _cacheVersion = -1;
        private static ElementId _cacheViewId;
        private static XYZ _cacheCorner0, _cacheCorner1;
        private static Dictionary<string, int> _cacheRings;
        private static List<OverlayMarker> _cacheMarkers;

        // Projects the parameter-marker hits. Returns how many stacking rings/squares each element already has
        // (violation rings go outside them). Icon-only marks sit in a row on the element's centre; SlotCount
        // centres that row.
        private static Dictionary<string, int> ProjectMarkerHits(List<MarkerEngine.Hit> hits, Projection proj, List<OverlayMarker> markers)
        {
            var ringsUsed = new Dictionary<string, int>();
            var iconCount = new Dictionary<string, int>();
            foreach (var h in hits)
            {
                if (h.Display == MarkerSet.DisplayKind.IconOnly)
                {
                    iconCount.TryGetValue(h.Key, out int c);
                    if (h.Slot + 1 > c) iconCount[h.Key] = h.Slot + 1;
                }
                else if (h.Display != MarkerSet.DisplayKind.Outline)
                {
                    ringsUsed.TryGetValue(h.Key, out int n);
                    if (h.Ring + 1 > n) ringsUsed[h.Key] = h.Ring + 1;
                }
            }
            // Elements with several outline marks share one projected outline.
            var projected = new Dictionary<List<XYZ[]>, float[][]>();
            foreach (var h in hits)
            {
                OverlayMarker m;
                if (!proj.TryProject(h.BBox, null, out m)) continue;
                m.R = h.R; m.G = h.G; m.B = h.B;
                m.Ring = h.Ring;
                m.Icon = h.Icon;
                m.Display = h.Display;
                m.Slot = h.Slot;
                if (h.Display == MarkerSet.DisplayKind.IconOnly) m.SlotCount = iconCount[h.Key];
                if (h.Display == MarkerSet.DisplayKind.Outline && h.Outline != null)
                {
                    if (!projected.TryGetValue(h.Outline, out var lines))
                    {
                        lines = proj.ProjectPolylines(h.Outline);
                        projected[h.Outline] = lines;
                    }
                    m.Outline = lines;
                }
                markers.Add(m);
            }
            return ringsUsed;
        }

        // Model space -> normalised viewport coordinates for the active view.
        private struct Projection
        {
            internal XYZ Right, Up;
            internal double MinU, MinV, RangeU, RangeV;

            internal float[][] ProjectPolylines(List<XYZ[]> polylines)
            {
                var result = new float[polylines.Count][];
                for (int i = 0; i < polylines.Count; i++)
                {
                    var pts = polylines[i];
                    var arr = new float[pts.Length * 2];
                    for (int k = 0; k < pts.Length; k++)
                    {
                        arr[2 * k]     = (float)((pts[k].DotProduct(Right) - MinU) / RangeU);
                        arr[2 * k + 1] = (float)(1.0 - (pts[k].DotProduct(Up) - MinV) / RangeV);
                    }
                    result[i] = arr;
                }
                return result;
            }

            // Screen rect of the element: its bounding box's 8 corners projected onto the view plane (so the
            // ring is centred on, and encloses, the whole element); a point if there's no box. False when the
            // rect (plus a margin for the ring) misses the visible area.
            internal bool TryProject(BoundingBoxXYZ bbox, XYZ fallbackPoint, out OverlayMarker marker)
            {
                marker = default(OverlayMarker);
                double u0, u1, v0, v1;
                if (bbox != null)
                    ProjectBox(bbox, Right, Up, out u0, out u1, out v0, out v1);
                else if (fallbackPoint != null)
                {
                    u0 = u1 = fallbackPoint.DotProduct(Right);
                    v0 = v1 = fallbackPoint.DotProduct(Up);
                }
                else return false;

                u0 = (u0 - MinU) / RangeU; u1 = (u1 - MinU) / RangeU;
                v0 = (v0 - MinV) / RangeV; v1 = (v1 - MinV) / RangeV;
                if (u0 > u1) { var t = u0; u0 = u1; u1 = t; }
                if (v0 > v1) { var t = v0; v0 = v1; v1 = t; }

                const double margin = 0.05;
                if (u1 < -margin || u0 > 1 + margin || v1 < -margin || v0 > 1 + margin) return false;

                // WPF canvas Y=0 is the top; model up is the bottom of NormY=0.
                marker.NormX = (u0 + u1) * 0.5;
                marker.NormY = 1.0 - (v0 + v1) * 0.5;
                marker.NormW = u1 - u0;
                marker.NormH = v1 - v0;
                return true;
            }
        }

        // Shared with markers/MarkerEngine: the views where rings can be drawn.
        internal static bool IsProjectableView(ViewType vt) =>
               vt == ViewType.FloorPlan
            || vt == ViewType.CeilingPlan
            || vt == ViewType.AreaPlan
            || vt == ViewType.EngineeringPlan
            || vt == ViewType.Section
            || vt == ViewType.Elevation
            || vt == ViewType.Detail;

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
