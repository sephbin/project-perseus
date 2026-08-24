using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ProjectPerseus.ui
{
    // Topmost, click-through WPF overlay covering the Revit canvas.
    // Two independent layers:
    //   (1) _blockingLabel  — dark pill shown when sync-blocking violations exist
    //   (2) _markerCanvas   — coloured circles at projected element positions (always in front)
    // All methods must be called via this window's own Dispatcher (see ViolationOverlayController).
    //
    // Marker positioning note: NormX/NormY are normalised coordinates within the Revit VIEW
    // viewport (from UIView.GetZoomCorners projection), NOT the Revit main window.  The overlay
    // covers the full main window client rect, so SyncPosition() walks Win32 child windows to
    // find the actual view panel and computes _vpLeft/_vpTop/_vpWidth/_vpHeight within the canvas.
    internal sealed class ViolationOverlay : Window
    {
        private readonly IntPtr _revitHwnd;
        private readonly uint   _revitProcessId;
        private IntPtr _hwnd;

        private bool _hasBlockingViolations;
        private bool _hasMarkers;
        private List<OverlayMarker> _currentMarkers = new List<OverlayMarker>();

        // Viewport sub-rect within the overlay canvas (WPF units).  Updated each SyncPosition tick.
        private double _vpLeft, _vpTop, _vpWidth, _vpHeight;

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

            _markerCanvas = new Canvas { IsHitTestVisible = false };

            var grid = new Grid();
            grid.Children.Add(_markerCanvas);   // bottom layer: element markers
            grid.Children.Add(_blockingLabel);  // top layer: blocking count pill
            Content = grid;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _positionTimer.Tick += (s, e) => SyncPosition();

            SourceInitialized += OnSourceInitialized;
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

        internal void SetMarkers(List<OverlayMarker> markers)
        {
            _currentMarkers = markers ?? new List<OverlayMarker>();
            _hasMarkers     = _currentMarkers.Count > 0;
            RebuildMarkerCanvas();
            ManageTimer();
        }

        // ── Private helpers ──

        private void ManageTimer()
        {
            if (!_hasBlockingViolations && !_hasMarkers)
            {
                _positionTimer.Stop();
                if (IsVisible) Hide();
                return;
            }
            if (!_positionTimer.IsEnabled) _positionTimer.Start();
            SyncPosition();
        }

        private void RebuildMarkerCanvas()
        {
            _markerCanvas.Children.Clear();
            const double diameter = 20;
            foreach (var m in _currentMarkers)
            {
                var ellipse = new Ellipse
                {
                    Width            = diameter,
                    Height           = diameter,
                    Fill             = new SolidColorBrush(Color.FromRgb(m.R, m.G, m.B)),
                    Stroke           = Brushes.White,
                    StrokeThickness  = 2,
                    IsHitTestVisible = false,
                    Tag              = m,
                };
                _markerCanvas.Children.Add(ellipse);
            }
            PositionMarkers();
        }

        private void PositionMarkers()
        {
            if (!_hasMarkers) return;
            if (_vpWidth <= 0 || _vpHeight <= 0) return;

            const double radius = 10;
            foreach (UIElement child in _markerCanvas.Children)
            {
                if (child is Ellipse e && e.Tag is OverlayMarker m)
                {
                    Canvas.SetLeft(e, _vpLeft + m.NormX * _vpWidth  - radius);
                    Canvas.SetTop( e, _vpTop  + m.NormY * _vpHeight - radius);
                }
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
                if (IsVisible) Hide();
                return;
            }

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

            // Sanity-clamp: if lookup failed, fall back to full overlay area.
            if (_vpWidth  < 100) { _vpLeft = 0; _vpWidth  = Width;  }
            if (_vpHeight < 100) { _vpTop  = 0; _vpHeight = Height; }

            PositionMarkers();

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

            [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr hwnd, int nIndex);
            [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll")] public static extern bool   GetClientRect(IntPtr hwnd, out RECT lpRect);
            [DllImport("user32.dll")] public static extern bool   GetWindowRect(IntPtr hwnd, out RECT lpRect);
            [DllImport("user32.dll")] public static extern bool   ClientToScreen(IntPtr hwnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern bool   ScreenToClient(IntPtr hwnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr hwnd, POINT pt, int uFlags);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern uint   GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

            [StructLayout(LayoutKind.Sequential)] public struct RECT  { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        }
    }
}
