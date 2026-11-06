using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    internal struct OverlayMarker
    {
        internal double NormX;  // centre: 0 = left edge, 1 = right edge of Revit viewport
        internal double NormY;  // centre: 0 = top edge,  1 = bottom edge
        internal double NormW;  // projected element extent as a fraction of viewport width (0 = point)
        internal double NormH;  // projected element extent as a fraction of viewport height
        internal byte R, G, B;
        internal int Ring;      // 0 = innermost; each further ring on the same element is drawn a step larger
        internal string Icon;   // ui/MarkerIcons key drawn on the ring (null = none)
    }

    // One line of the on-canvas marker legend (markers/MarkerEngine builds these).
    internal class OverlayLegendRow
    {
        internal bool IsHeader;  // set name
        internal string Label;
        internal int Count;
        internal byte R, G, B;
        internal string Icon;
    }

    // Owns the dedicated STA thread and Dispatcher for ViolationOverlay.
    // Thread safety: _overlay is written before _dispatcher (volatile publish pattern),
    // so a non-null _dispatcher read on any thread implies _overlay is also non-null.
    internal static class ViolationOverlayController
    {
        private static ViolationOverlay          _overlay;
        private static volatile Dispatcher       _dispatcher;

        internal static void Start(IntPtr revitHwnd)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    _overlay = new ViolationOverlay(revitHwnd);
                    // EnsureHandle creates the HWND (fires SourceInitialized / sets click-through
                    // extended styles) without making the window visible.
                    new WindowInteropHelper(_overlay).EnsureHandle();
                    // Publish after HWND is ready so Update() callers see a fully initialised overlay.
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    Log.Warn($"[ViolationOverlay] STA thread failed: {ex.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "Perseus-ViolationOverlay";
            thread.Start();
        }

        internal static void Update(int blockingCount)
        {
            var disp = _dispatcher;
            var ov   = _overlay;
            if (disp == null || ov == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { ov.SetBlockingCount(blockingCount); }
                    catch (Exception ex) { Log.Warn($"[ViolationOverlay] SetBlockingCount failed: {ex.Message}"); }
                }));
            }
            catch (Exception ex)
            {
                Log.Warn($"[ViolationOverlay] BeginInvoke failed: {ex.Message}");
            }
        }

        // markers == null clears them. The frame is stamped here (Revit thread, at projection time) so the
        // overlay can tell whether it was computed before or after the user's last pan/zoom input.
        internal static void UpdateMarkers(List<OverlayMarker> markers)
        {
            var disp = _dispatcher;
            var ov   = _overlay;
            if (disp == null || ov == null) return;
            int computedAt = Environment.TickCount;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { ov.SetMarkers(markers, computedAt); }
                    catch (Exception ex) { Log.Warn($"[ViolationOverlay] SetMarkers failed: {ex.Message}"); }
                }));
            }
            catch { /* dispatcher shut down */ }
        }

        // The Revit view's zoom corners changed since the last tick (pan/zoom/view switch): hide the
        // markers until a frame computed on a settled view arrives.
        internal static void SetViewMoving()
        {
            var disp = _dispatcher;
            var ov   = _overlay;
            if (disp == null || ov == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { ov.OnViewMoving(); }
                    catch (Exception ex) { Log.Warn($"[ViolationOverlay] OnViewMoving failed: {ex.Message}"); }
                }));
            }
            catch { /* dispatcher shut down */ }
        }

        // Marker legend card (null hides it).
        internal static void UpdateLegend(List<OverlayLegendRow> rows)
        {
            var disp = _dispatcher;
            var ov   = _overlay;
            if (disp == null || ov == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { ov.SetLegend(rows); }
                    catch (Exception ex) { Log.Warn($"[ViolationOverlay] SetLegend failed: {ex.Message}"); }
                }));
            }
            catch { /* dispatcher shut down */ }
        }

        internal static void Stop()
        {
            var disp = _dispatcher;
            if (disp == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { _overlay?.Close(); }
                    catch { /* window already disposed */ }
                    disp.InvokeShutdown();
                }));
            }
            catch { /* dispatcher already shut down */ }
        }
    }
}
