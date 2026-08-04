using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProjectPerseus.ui
{
    // Topmost, click-through WPF overlay that covers the Revit canvas when sync-blocking
    // violations are pending. All methods must be called via this window's own Dispatcher
    // (see ViolationOverlayController). The window is created hidden; Show() is called only
    // when violations exist AND Revit is the foreground application.
    internal sealed class ViolationOverlay : Window
    {
        private readonly IntPtr _revitHwnd;
        private readonly uint   _revitProcessId;
        private IntPtr _hwnd;
        private bool   _hasBlockingViolations;
        private readonly TextBlock _statusText;
        private readonly DispatcherTimer _positionTimer;

        internal ViolationOverlay(IntPtr revitHwnd)
        {
            _revitHwnd = revitHwnd;
            NativeMethods.GetWindowThreadProcessId(revitHwnd, out _revitProcessId);

            WindowStyle        = WindowStyle.None;
            AllowsTransparency = true;
            Background         = new SolidColorBrush(Color.FromArgb(0x55, 0x30, 0x30, 0x30));
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

            // Dark pill behind the text for legibility over any canvas colour.
            var label = new Border
            {
                Background          = new SolidColorBrush(Color.FromArgb(0xCC, 0x18, 0x18, 0x18)),
                CornerRadius        = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Margin              = new Thickness(16),
                Child               = _statusText,
            };

            var grid = new Grid();
            grid.Children.Add(label);
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

        // Called from ViolationOverlayController via BeginInvoke — runs on this window's Dispatcher.
        internal void SetBlockingCount(int count)
        {
            _hasBlockingViolations = count > 0;

            if (!_hasBlockingViolations)
            {
                _positionTimer.Stop();
                if (IsVisible) Hide();
                return;
            }

            _statusText.Text = count == 1
                ? "⚠  1 sync-blocking violation — sync is blocked"
                : $"⚠  {count} sync-blocking violations — sync is blocked";

            if (!_positionTimer.IsEnabled)
                _positionTimer.Start();

            SyncPosition(); // Immediate update before the next timer tick.
        }

        private void SyncPosition()
        {
            if (!_hasBlockingViolations || _revitHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

            // Only render while Revit is the active application. Compare process IDs so that
            // all Revit-owned windows (dialogs, child windows) count as "Revit is active".
            IntPtr fg = NativeMethods.GetForegroundWindow();
            uint fgPid;
            NativeMethods.GetWindowThreadProcessId(fg, out fgPid);
            bool revitIsActive = fgPid == _revitProcessId;

            if (!revitIsActive)
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

            // Convert physical pixels to WPF logical pixels using this window's own DPI transform.
            // Reading TransformFromDevice from the overlay's HwndSource is correct even when
            // Revit and the overlay sit on different monitors with different DPI scaling.
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

            if (!IsVisible) Show();
        }

        private static class NativeMethods
        {
            public const int GWL_EXSTYLE       = -20;
            public const int WS_EX_TRANSPARENT = 0x00000020;
            public const int WS_EX_LAYERED     = 0x00080000;
            public const int WS_EX_NOACTIVATE  = 0x08000000;

            [DllImport("user32.dll")] public static extern int    GetWindowLong(IntPtr hwnd, int nIndex);
            [DllImport("user32.dll")] public static extern int    SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
            [DllImport("user32.dll")] public static extern bool   GetClientRect(IntPtr hwnd, out RECT lpRect);
            [DllImport("user32.dll")] public static extern bool   ClientToScreen(IntPtr hwnd, ref POINT lpPoint);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern uint   GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

            [StructLayout(LayoutKind.Sequential)] public struct RECT  { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        }
    }
}
