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
        internal double NormX;  // 0 = left edge, 1 = right edge of Revit viewport
        internal double NormY;  // 0 = top edge,  1 = bottom edge
        internal byte R, G, B;
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

        internal static void UpdateMarkers(List<OverlayMarker> markers)
        {
            var disp = _dispatcher;
            var ov   = _overlay;
            if (disp == null || ov == null) return;
            try
            {
                disp.BeginInvoke(new Action(() =>
                {
                    try   { ov.SetMarkers(markers); }
                    catch (Exception ex) { Log.Warn($"[ViolationOverlay] SetMarkers failed: {ex.Message}"); }
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
