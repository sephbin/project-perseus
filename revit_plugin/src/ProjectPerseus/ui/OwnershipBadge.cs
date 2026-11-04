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
    /// Small PerseusCyan pill showing how many elements the current user owns. A borderless, non-activating WPF
    /// window owned by the Revit main window (so it stays above Revit only, and hides when Revit is minimised);
    /// OwnershipBadgeController positions it in Revit's status bar. Clicking it opens the ownership panel.
    /// </summary>
    internal class OwnershipBadge : Window
    {
        private static readonly SolidColorBrush Cyan = Frozen(Color.FromRgb(0x00, 0xA0, 0xE0));
        private static readonly SolidColorBrush CyanHover = Frozen(Color.FromRgb(0x00, 0x86, 0xBD));

        private readonly Border _pill;
        private readonly TextBlock _text;

        internal event Action Clicked;

        internal OwnershipBadge(IntPtr revitHwnd)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;

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
                Background = Cyan,
                CornerRadius = new CornerRadius(9),
                Height = 18,
                MinWidth = 18,
                Padding = new Thickness(6, 0, 6, 0),
                Cursor = Cursors.Hand,
                Child = _text,
            };
            Content = _pill;

            _pill.MouseEnter += (s, e) => _pill.Background = CyanHover;
            _pill.MouseLeave += (s, e) => _pill.Background = Cyan;
            _pill.MouseLeftButtonUp += (s, e) => Clicked?.Invoke();

            new WindowInteropHelper(this).Owner = revitHwnd;
            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };
        }

        internal IntPtr Handle => new WindowInteropHelper(this).Handle;

        internal void SetCount(int elements, int worksets, string docTitle)
        {
            _text.Text = elements.ToString("N0");
            ToolTip = $"You own {elements:N0} element(s)" +
                      (worksets > 0 ? $" and {worksets} workset(s)" : "") +
                      $" in {docTitle}.\nOther users can't edit them until you sync or relinquish.\nClick to see who owns what.";
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
