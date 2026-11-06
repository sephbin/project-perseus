using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using ProjectPerseus.logging;
using ProjectPerseus.models;
using ProjectPerseus.ui;

namespace ProjectPerseus.violations
{
    internal static class ViolationHighlightController
    {
        private static List<ViolationHighlightDto> _currentViolations     = new List<ViolationHighlightDto>();
        private static List<ViolationHighlightDto> _currentTypeViolations = new List<ViolationHighlightDto>();

        private static ExternalEvent _positionEvent;
        private static Timer        _positionTimer;

        internal static List<ViolationHighlightDto> CurrentViolations     => _currentViolations;
        internal static List<ViolationHighlightDto> CurrentTypeViolations => _currentTypeViolations;

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
            var all        = dtos ?? new List<ViolationHighlightDto>();
            var instances  = new List<ViolationHighlightDto>();
            var types      = new List<ViolationHighlightDto>();
            var highlights = new List<ViolationHighlight>();

            foreach (var dto in all)
            {
                try
                {
                    var el = doc.GetElement(dto.ElementUniqueId);
                    if (el == null) continue;

                    // Types and families have no placement in any view — keep them in a
                    // separate list and never add geometry for them.
                    if (el is ElementType || el is Family)
                    {
                        types.Add(dto);
                        continue;
                    }

                    instances.Add(dto);

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

            _currentViolations     = instances;
            _currentTypeViolations = types;
            ViolationHighlightServer.Instance.SetHighlights(highlights);

            // Status-bar badge: everything in the violation list (instances + types), by severity.
            try
            {
                int errors = 0, warnings = 0, infos = 0;
                foreach (var dto in instances.Concat(types))
                {
                    switch (dto.Severity?.ToLower())
                    {
                        case "error":   errors++;   break;
                        case "warning": warnings++; break;
                        default:        infos++;    break;
                    }
                }
                StatusBadgeController.ShowViolations(StatusBadgeController.DocKey(doc), doc.Title, errors, warnings, infos);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationHighlightController] badge update failed: {ex.Message}");
            }
        }

        internal static void SetPositionEvent(ExternalEvent ev)
        {
            _positionEvent = ev;
            _positionTimer?.Dispose();
            _positionTimer = new Timer(_ => OnPositionTick(), null, 0, 200); // markers return promptly after a pan
        }

        private static bool _markersWereActive;

        // Runs on a ThreadPool thread. An exception escaping a Timer callback terminates the whole Revit process
        // (no crash dump), so everything is caught. ExternalEvent.Raise throws once Revit is shutting down.
        private static void OnPositionTick()
        {
            try
            {
                bool active = IsEnabled && ViolationHighlightServer.Instance.CurrentMode == ViolationDisplayMode.Symbol;
                if (active || _markersWereActive) _positionEvent?.Raise(); // one extra tick clears the markers
                _markersWereActive = active;
            }
            catch (Exception ex)
            {
                try { Log.Warn($"[ViolationHighlightController] position tick failed: {ex.Message}"); } catch { }
            }
        }

        internal static void Toggle(UIDocument uidoc)
        {
            ViolationHighlightServer.Instance.IsEnabled = !ViolationHighlightServer.Instance.IsEnabled;
            if (!ViolationHighlightServer.Instance.IsEnabled)
                ViolationOverlayController.UpdateMarkers(null);
            try { uidoc.RefreshActiveView(); }
            catch (Exception ex) { Log.Warn($"[ViolationHighlightController] Toggle refresh failed: {ex.Message}"); }
        }

        internal static void CycleMode(UIDocument uidoc)
        {
            var server = ViolationHighlightServer.Instance;
            server.CurrentMode = server.CurrentMode == ViolationDisplayMode.BoundingBox
                ? ViolationDisplayMode.Symbol
                : ViolationDisplayMode.BoundingBox;
            if (server.CurrentMode == ViolationDisplayMode.BoundingBox)
                ViolationOverlayController.UpdateMarkers(null);
            try { uidoc.RefreshActiveView(); }
            catch (Exception ex) { Log.Warn($"[ViolationHighlightController] CycleMode refresh failed: {ex.Message}"); }
        }
    }
}
