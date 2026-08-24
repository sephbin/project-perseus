using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;

namespace ProjectPerseus.violations
{
    internal static class ViolationHighlightController
    {
        private static List<ViolationHighlightDto> _currentViolations = new List<ViolationHighlightDto>();

        internal static List<ViolationHighlightDto> CurrentViolations => _currentViolations;

        internal static bool IsEnabled => ViolationHighlightServer.Instance.IsEnabled;

        internal static void Initialize()
        {
            try
            {
                var service = ExternalServiceRegistry.GetService(
                    ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                if (service == null)
                {
                    Log.Warn("[ViolationHighlightController] DirectContext3D service not available.");
                    return;
                }
                service.AddServer(ViolationHighlightServer.Instance);
                var activeIds = service.GetActiveServerIds();
                activeIds.Add(ViolationHighlightServer.Instance.GetServerId());
                service.SetActiveServers(activeIds);
                Log.Info("[ViolationHighlightController] DirectContext3D server registered.");
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationHighlightController] Initialize failed: {ex.Message}");
            }
        }

        internal static void Update(List<ViolationHighlightDto> dtos, Document doc)
        {
            _currentViolations = dtos ?? new List<ViolationHighlightDto>();
            var highlights = new List<ViolationHighlight>();

            foreach (var dto in _currentViolations)
            {
                try
                {
                    var el = doc.GetElement(dto.ElementUniqueId);
                    if (el == null) continue;

                    var bbox = el.get_BoundingBox(null);

                    XYZ location = null;
                    if (el.Location is LocationPoint lp)
                        location = lp.Point;
                    else if (el.Location is LocationCurve lc)
                        location = lc.Curve.Evaluate(0.5, true);
                    else if (bbox != null)
                        location = (bbox.Min + bbox.Max) * 0.5;

                    highlights.Add(new ViolationHighlight
                    {
                        BBox     = bbox,
                        Location = location,
                        Color    = ViolationHighlightServer.SeverityColor(dto.Severity),
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ViolationHighlightController] element resolve failed for {dto.ElementUniqueId}: {ex.Message}");
                }
            }

            ViolationHighlightServer.Instance.SetHighlights(highlights);
        }

        internal static void Toggle(UIDocument uidoc)
        {
            ViolationHighlightServer.Instance.IsEnabled = !ViolationHighlightServer.Instance.IsEnabled;
            try { uidoc.RefreshActiveView(); }
            catch (Exception ex) { Log.Warn($"[ViolationHighlightController] Toggle refresh failed: {ex.Message}"); }
        }

        internal static void CycleMode(UIDocument uidoc)
        {
            var server = ViolationHighlightServer.Instance;
            server.CurrentMode = server.CurrentMode == ViolationDisplayMode.BoundingBox
                ? ViolationDisplayMode.Symbol
                : ViolationDisplayMode.BoundingBox;
            try { uidoc.RefreshActiveView(); }
            catch (Exception ex) { Log.Warn($"[ViolationHighlightController] CycleMode refresh failed: {ex.Message}"); }
        }
    }
}
