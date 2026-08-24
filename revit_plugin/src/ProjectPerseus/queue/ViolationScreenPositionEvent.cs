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

                var snapshot = ViolationHighlightServer.Instance.GetHighlightsSnapshot();
                var markers  = new List<OverlayMarker>(snapshot.Count);

                foreach (var h in snapshot)
                {
                    if (h.Location == null) continue;

                    double u = (h.Location.DotProduct(right) - minU) / rangeU;
                    double v = (h.Location.DotProduct(up)    - minV) / rangeV;

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

        public string GetName() => "Perseus Violation Screen Position";
    }
}
