using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ProjectPerseus.ui
{
    // Topmost, click-through WPF overlay covering the Revit canvas.
    // Two independent layers:
    //   (1) _blockingLabel  — dark pill shown when sync-blocking violations exist
    //   (2) _markerCanvas   — rings drawn around each violating element (projected bounding box)
    // All methods must be called via this window's own Dispatcher (see ViolationOverlayController).
    //
    // Marker positioning note: NormX/NormY/NormW/NormH are normalised coordinates within the Revit VIEW
    // viewport (from UIView.GetZoomCorners projection), NOT the Revit main window.  The overlay
    // covers the full main window client rect, so SyncPosition() walks Win32 child windows to
    // find the actual view panel and computes _vpLeft/_vpTop/_vpWidth/_vpHeight within the canvas.
    //
    // Navigation: marker positions are recomputed by Revit only every ~200 ms, so while the user pans
    // or zooms they would lag behind the model. Instead the markers are hidden as soon as navigation
    // starts and faded back in once a frame computed on the settled view arrives. Two signals:
    //   - a low-level mouse hook (installed only while markers exist) sees middle-button drags and
    //     wheel zooms over the viewport the instant they happen;
    //   - Revit reports SetViewMoving() when the zoom corners changed between ticks (ViewCube,
    //     navigation bar, keyboard, view switch... anything the hook can't see).
    internal sealed class ViolationOverlay : Window
    {
        // A marker frame must be computed at least this long after the last navigation input
        // to be trusted (Revit may apply a wheel zoom slightly after the wheel message).
        private const int SettleMs = 150;
        private const double MinRingDiameter = 28;   // px — small/point elements still get a visible ring
        private const double RingPadding     = 8;    // px added around the element's projected diagonal
        private const double MaxRingDiameter = 6000; // px — beyond this the ring is just noise off-screen

        private readonly IntPtr _revitHwnd;
        private readonly uint   _revitProcessId;
        private IntPtr _hwnd;

        private bool _hasBlockingViolations;
        private bool _hasMarkers;
        private List<OverlayMarker> _currentMarkers = new List<OverlayMarker>();

        // Viewport sub-rect within the overlay canvas (WPF units).  Updated each SyncPosition tick.
        private double _vpLeft, _vpTop, _vpWidth, _vpHeight;
        // Same viewport in physical screen pixels, for the mouse hook's hit test.
        private NativeMethods.RECT _vpScreen;
        private volatile bool _revitForeground;

        // Navigation suppression state (all touched on this window's dispatcher thread only).
        private bool _suppressed;
        private bool _middleDown;
        private int  _lastNavTicks;

        private IntPtr _mouseHook = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _mouseProc; // field keeps the delegate alive while hooked

        private readonly TextBlock       _statusText;
        private readonly Border          _blockingLabel;
        private readonly Canvas          _markerCanvas;
        private readonly DispatcherTimer _positionTimer;

        internal ViolationOverlay(IntPtr revitHwnd)
        {
            _revitHwnd = revitHwnd;
            NativeMethods.GetWindowThreadProcessId(revitHwnd, out _revitProcessId);

            WindowStyle        = WindowStyle.None;
            AllowsTransparency = true;
            Background         = Brushes.Transparent;
            ShowInTaskbar      = false;
            ShowActivated      = false;
            Topmost            = true;
            ResizeMode         = ResizeMode.NoResize;
            SizeToContent      = SizeToContent.Manual;
            Width              = 1;
            Height             = 1;

            _statusText = new TextBlock
            {
                Foreground          = Brushes.White,
                FontSize            = 14,
                FontWeight          = FontWeights.SemiBold,
                TextWrapping        = TextWrapping.Wrap,
                Margin              = new Thickness(12, 6, 12, 6),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            _blockingLabel = new Border
            {
                Background          = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x18)),
                CornerRadius        = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(16),
                Child               = _statusText,
                Visibility          = Visibility.Collapsed,
            };

            _markerCanvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };

            var grid = new Grid();
            grid.Children.Add(_markerCanvas);   // bottom layer: element markers
            grid.Children.Add(_blockingLabel);  // top layer: blocking count pill
            Content = grid;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _positionTimer.Tick += (s, e) =>
            {
                // Unhandled exceptions on a WPF dispatcher thread bring Revit down; keep the overlay's failures here.
                try
                {
                    CheckMiddleButtonReleased();
                    SyncPosition();
                }
                catch (Exception ex) { ProjectPerseus.logging.Log.Warn($"[ViolationOverlay] position sync failed: {ex.Message}"); }
            };

            SourceInitialized += OnSourceInitialized;
            Closed += (s, e) => UninstallMouseHook();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE);
        }

        // ── Public API (called from ViolationOverlayController via BeginInvoke) ──

        internal void SetBlockingCount(int count)
        {
            _hasBlockingViolations = count > 0;
            Background = _hasBlockingViolations
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x30, 0x30, 0x30))
                : Brushes.Transparent;

            _blockingLabel.Visibility = _hasBlockingViolations ? Visibility.Visible : Visibility.Collapsed;
            if (_hasBlockingViolations)
                _statusText.Text = count == 1
                    ? "⚠  1 sync-blocking violation — sync is blocked"
                    : $"⚠  {count} sync-blocking violations — sync is blocked";

            ManageTimer();
        }

        // computedAt = Environment.TickCount when Revit projected this frame.
        internal void SetMarkers(List<OverlayMarker> markers, int computedAt)
        {
            var next = markers ?? new List<OverlayMarker>();
            bool changed = !SameMarkers(_currentMarkers, next);
            _currentMarkers = next;
            _hasMarkers     = next.Count > 0;

            if (!_hasMarkers)
            {
                // Cleared (layer off / mode switched / nothing visible): reset suppression too.
                _suppressed = false;
                _markerCanvas.Children.Clear();
                ShowMarkerLayer(animate: false);
            }
            else
            {
                if (changed) RebuildMarkerCanvas();
                if (_suppressed && !_middleDown && unchecked(computedAt - _lastNavTicks) >= SettleMs)
                {
                    _suppressed = false;
                    PositionMarkers();
                    ShowMarkerLayer(animate: true);
                }
            }
            ManageTimer();
        }

        internal void OnViewMoving() => BeginNavigation();

        // ── Navigation suppression ──

        private void BeginNavigation()
        {
            _lastNavTicks = Environment.TickCount;
            if (_suppressed) return;
            _suppressed = true;
            _markerCanvas.BeginAnimation(OpacityProperty, null);
            _markerCanvas.Opacity    = 0;
            _markerCanvas.Visibility = Visibility.Collapsed;
        }

        private void ShowMarkerLayer(bool animate)
        {
            _markerCanvas.Visibility = Visibility.Visible;
            if (!animate)
            {
                _markerCanvas.BeginAnimation(OpacityProperty, null);
                _markerCanvas.Opacity = 1;
                return;
            }
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            _markerCanvas.BeginAnimation(OpacityProperty, fade);
        }

        // Safety net in case the hook missed WM_MBUTTONUP (e.g. released over another window).
        private void CheckMiddleButtonReleased()
        {
            if (!_middleDown) return;
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_MBUTTON) & 0x8000) != 0) return;
            _middleDown   = false;
            _lastNavTicks = Environment.TickCount;
        }

        private void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            try
            {
                if (_mouseProc == null) _mouseProc = MouseHookCallback;
                IntPtr hMod;
                using (var module = Process.GetCurrentProcess().MainModule)
                    hMod = NativeMethods.GetModuleHandle(module.ModuleName);
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hMod, 0);
                if (_mouseHook == IntPtr.Zero)
                    ProjectPerseus.logging.Log.Warn(
                        $"[ViolationOverlay] mouse hook not installed (Win32 error {Marshal.GetLastWin32Error()}); " +
                        "markers will hide on view change only.");
            }
            catch (Exception ex)
            {
                _mouseHook = IntPtr.Zero;
                ProjectPerseus.logging.Log.Warn($"[ViolationOverlay] mouse hook install failed: {ex.Message}");
            }
        }

        private void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero) return;
            try { NativeMethods.UnhookWindowsHookEx(_mouseHook); } catch { }
            _mouseHook  = IntPtr.Zero;
            _middleDown = false;
        }

        // Runs on this window's thread for every mouse event system-wide while installed: keep it tiny,
        // never throw, and always pass the event on. Windows silently removes slow LL hooks.
        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && _revitForeground)
                {
                    int msg = wParam.ToInt32();
                    if (msg == NativeMethods.WM_MBUTTONUP)
                    {
                        if (_middleDown)
                        {
                            _middleDown   = false;
                            _lastNavTicks = Environment.TickCount;
                        }
                    }
                    else if (msg == NativeMethods.WM_MBUTTONDOWN
                          || msg == NativeMethods.WM_MOUSEWHEEL
                          || msg == NativeMethods.WM_MOUSEHWHEEL)
                    {
                        var info = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                        if (InViewport(info.pt.X, info.pt.Y))
                        {
                            if (msg == NativeMethods.WM_MBUTTONDOWN) _middleDown = true;
                            BeginNavigation();
                        }
                    }
                }
            }
            catch { /* never let anything escape a hook */ }
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private bool InViewport(int x, int y) =>
            x >= _vpScreen.Left && x < _vpScreen.Right && y >= _vpScreen.Top && y < _vpScreen.Bottom;

        // ── Private helpers ──

        private void ManageTimer()
        {
            if (_hasMarkers) InstallMouseHook(); else UninstallMouseHook();

            if (!_hasBlockingViolations && !_hasMarkers)
            {
                _positionTimer.Stop();
                if (IsVisible) Hide();
                return;
            }
            if (!_positionTimer.IsEnabled) _positionTimer.Start();
            SyncPosition();
        }

        private static bool SameMarkers(List<OverlayMarker> a, List<OverlayMarker> b)
        {
            if (a.Count != b.Count) return false;
            const double eps = 1e-5;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i]; var y = b[i];
                if (x.R != y.R || x.G != y.G || x.B != y.B) return false;
                if (Math.Abs(x.NormX - y.NormX) > eps || Math.Abs(x.NormY - y.NormY) > eps) return false;
                if (Math.Abs(x.NormW - y.NormW) > eps || Math.Abs(x.NormH - y.NormH) > eps) return false;
            }
            return true;
        }

        // Each marker is a Grid holding a dark halo ring (contrast on any background), a faint tinted
        // fill, and the severity-coloured ring on top. Size/position are set in PositionMarkers.
        private void RebuildMarkerCanvas()
        {
            _markerCanvas.Children.Clear();
            var halo = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            halo.Freeze();
            foreach (var m in _currentMarkers)
            {
                var ringBrush = new SolidColorBrush(Color.FromRgb(m.R, m.G, m.B));
                ringBrush.Freeze();
                var fillBrush = new SolidColorBrush(Color.FromArgb(0x1F, m.R, m.G, m.B));
                fillBrush.Freeze();

                var marker = new Grid { IsHitTestVisible = false, Tag = m };
                marker.Children.Add(new Ellipse
                {
                    Stroke          = halo,
                    StrokeThickness = 5,
                    Fill            = fillBrush,
                });
                marker.Children.Add(new Ellipse
                {
                    Stroke          = ringBrush,
                    StrokeThickness = 2.5,
                    Margin          = new Thickness(1.25), // centres the coloured ring on the halo
                });
                _markerCanvas.Children.Add(marker);
            }
            PositionMarkers();
        }

        private void PositionMarkers()
        {
            if (!_hasMarkers) return;
            if (_vpWidth <= 0 || _vpHeight <= 0) return;

            foreach (UIElement child in _markerCanvas.Children)
            {
                if (!(child is Grid g) || !(g.Tag is OverlayMarker m)) continue;

                double wPx = m.NormW * _vpWidth;
                double hPx = m.NormH * _vpHeight;
                double d   = Math.Sqrt(wPx * wPx + hPx * hPx) + 2 * RingPadding; // ring encloses the bbox
                if (d < MinRingDiameter) d = MinRingDiameter;
                if (d > MaxRingDiameter)
                {
                    g.Visibility = Visibility.Collapsed;
                    continue;
                }
                g.Visibility = Visibility.Visible;
                g.Width  = d;
                g.Height = d;
                Canvas.SetLeft(g, _vpLeft + m.NormX * _vpWidth  - d / 2);
                Canvas.SetTop( g, _vpTop  + m.NormY * _vpHeight - d / 2);
            }
        }

        private void SyncPosition()
        {
            if (!_hasBlockingViolations && !_hasMarkers) return;
            if (_revitHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgPid;
            NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid != _revitProcessId)
            {
                _revitForeground = false;
                if (IsVisible) Hide();
                return;
            }
            _revitForeground = true;

            NativeMethods.RECT cr;
            if (!NativeMethods.GetClientRect(_revitHwnd, out cr)) return;
            var pt = new NativeMethods.POINT { X = 0, Y = 0 };
            if (!NativeMethods.ClientToScreen(_revitHwnd, ref pt)) return;

            int physW = cr.Right  - cr.Left;
            int physH = cr.Bottom - cr.Top;
            if (physW <= 0 || physH <= 0) return;

            double scaleX = 1.0, scaleY = 1.0;
            var hsrc = HwndSource.FromHwnd(_hwnd);
            if (hsrc?.CompositionTarget != null)
            {
                var mat = hsrc.CompositionTarget.TransformFromDevice;
                scaleX = mat.M11;
                scaleY = mat.M22;
            }

            Left   = pt.X  * scaleX;
            Top    = pt.Y  * scaleY;
            Width  = physW * scaleX;
            Height = physH * scaleY;

            // Find the actual Revit view viewport within the main window by walking
            // Win32 child windows from the centre of the client area.
            int screenCx = pt.X + physW / 2;
            int screenCy = pt.Y + physH / 2;
            NativeMethods.RECT vp = FindViewportScreenRect(_revitHwnd, screenCx, screenCy,
                                                            pt.X, pt.Y, physW, physH);

            _vpLeft   = (vp.Left   - pt.X) * scaleX;
            _vpTop    = (vp.Top    - pt.Y) * scaleY;
            _vpWidth  = (vp.Right  - vp.Left) * scaleX;
            _vpHeight = (vp.Bottom - vp.Top)  * scaleY;
            _vpScreen = vp;

            // Sanity-clamp: if lookup failed, fall back to full overlay area.
            if (_vpWidth  < 100) { _vpLeft = 0; _vpWidth  = Width;  _vpScreen.Left = pt.X; _vpScreen.Right  = pt.X + physW; }
            if (_vpHeight < 100) { _vpTop  = 0; _vpHeight = Height; _vpScreen.Top  = pt.Y; _vpScreen.Bottom = pt.Y + physH; }

            if (!_suppressed) PositionMarkers();

            if (!IsVisible) Show();
        }

        // Walk the Win32 child window tree from (screenCx, screenCy) down to the deepest
        // visible child — that's the Revit view render panel.  Falls back to the full main
        // window rect if nothing useful is found.
        private static NativeMethods.RECT FindViewportScreenRect(
            IntPtr revitHwnd, int screenCx, int screenCy,
            int fallbackLeft, int fallbackTop, int fallbackW, int fallbackH)
        {
            IntPtr current = revitHwnd;

            for (int depth = 0; depth < 12; depth++)
            {
                var clientPt = new NativeMethods.POINT { X = screenCx, Y = screenCy };
                if (!NativeMethods.ScreenToClient(current, ref clientPt)) break;

                IntPtr child = NativeMethods.ChildWindowFromPointEx(
                    current, clientPt,
                    NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPDISABLED);

                if (child == IntPtr.Zero || child == current) break;
                current = child;
            }

            if (current == revitHwnd)
            {
                return new NativeMethods.RECT
                {
                    Left   = fallbackLeft,
                    Top    = fallbackTop,
                    Right  = fallbackLeft + fallbackW,
                    Bottom = fallbackTop  + fallbackH,
                };
            }

            NativeMethods.RECT rect;
            NativeMethods.GetWindowRect(current, out rect);
            return rect;
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE        = -20;
            public const int WS_EX_TRANSPARENT  = 0x00000020;
            public const int WS_EX_LAYERED      = 0x00080000;
            public const int WS_EX_NOACTIVATE   = 0x08000000;
            public const int CWP_SKIPINVISIBLE  = 0x0001;
            public const int CWP_SKIPDISABLED   = 0x0002;

            public const int WH_MOUSE_LL        = 14;
            public const int WM_MBUTTONDOWN     = 0x0207;
            public const int WM_MBUTTONUP       = 0x0208;
            public const int WM_MOUSEWHEEL      = 0x020A;
            public const int WM_MOUSEHWHEEL     = 0x020E;
            public const int VK_MBUTTON         = 0x04;

            public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr hwnd, int nIndex);
            [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll")] public static extern bool   GetClientRect(IntPtr hwnd, out RECT lpRect);
            [DllImport("user32.dll")] public static extern bool   GetWindowRect(IntPtr hwnd, out RECT lpRect);
            [DllImport("user32.dll")] public static extern bool   ClientToScreen(IntPtr hwnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern bool   ScreenToClient(IntPtr hwnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr hwnd, POINT pt, int uFlags);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern uint   GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);
            [DllImport("user32.dll")] public static extern short  GetAsyncKeyState(int vKey);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
            [DllImport("user32.dll")] public static extern bool   UnhookWindowsHookEx(IntPtr hhk);
            [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string lpModuleName);

            [StructLayout(LayoutKind.Sequential)] public struct RECT  { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MSLLHOOKSTRUCT
            {
                public POINT  pt;
                public uint   mouseData;
                public uint   flags;
                public uint   time;
                public IntPtr dwExtraInfo;
            }
        }
    }
}
