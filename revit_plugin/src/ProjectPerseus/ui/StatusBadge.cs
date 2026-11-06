using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Small coloured pill in Revit's status bar (ownership count, violation count). A borderless, non-activating
    /// WPF window owned by the Revit main window (so it stays above Revit only, and hides when Revit is minimised);
    /// StatusBadgeController positions it. Clicking it raises <see cref="Clicked"/>.
    ///
    /// Sizing is explicit, not SizeToContent: a hidden, non-activating SizeToContent window reports a stale (often
    /// much wider) size until WPF gets round to a layout pass, which made the pill jump between wide and narrow.
    /// SetContent measures the pill and the controller applies that size in pixels with SetWindowPos.
    /// </summary>
    internal class StatusBadge : Window
    {
        private const double PillHeight = 18;

        private readonly Border _pill;
        private readonly TextBlock _text;
        private SolidColorBrush _fill, _hover;

        internal event Action Clicked;

        internal StatusBadge(IntPtr revitHwnd, Color fill, Color hover)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            Focusable = false;
            Height = PillHeight;
            Width = PillHeight;

            _fill = Frozen(fill);
            _hover = Frozen(hover);
            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "0",
            };
            _pill = new Border
            {
                Background = _fill,
                CornerRadius = new CornerRadius(PillHeight / 2),
                Height = PillHeight,
                MinWidth = PillHeight,
                Padding = new Thickness(7, 0, 7, 0),
                Cursor = Cursors.Hand,
                Child = _text,
            };
            Content = _pill;

            _pill.MouseEnter += (s, e) => _pill.Background = _hover;
            _pill.MouseLeave += (s, e) => _pill.Background = _fill;
            _pill.MouseLeftButtonUp += (s, e) => Clicked?.Invoke();

            new WindowInteropHelper(this).Owner = revitHwnd;
            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };
        }

        internal IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

        internal void SetColors(Color fill, Color hover)
        {
            if (_fill.Color == fill && _hover.Color == hover) return;
            bool hovering = _pill.IsMouseOver;
            _fill = Frozen(fill);
            _hover = Frozen(hover);
            _pill.Background = hovering ? _hover : _fill;
        }

        /// <summary>Sets the text and tooltip and resizes the window to fit the text exactly.</summary>
        internal void SetContent(string text, string tooltip)
        {
            ToolTip = tooltip;
            if (_text.Text == text && Width > PillHeight) return;
            _text.Text = text;
            _pill.Measure(new Size(double.PositiveInfinity, PillHeight));
            Width = Math.Max(PillHeight, Math.Ceiling(_pill.DesiredSize.Width));
            Height = PillHeight;
        }

        /// <summary>The badge's intended size in physical pixels (what SetWindowPos needs).</summary>
        internal void GetPixelSize(out int width, out int height)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            width = (int)Math.Ceiling(Width * dpi.DpiScaleX);
            height = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
