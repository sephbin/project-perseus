using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Invisible click targets over the federator's change badges in the active Revit view (DirectContext3D graphics
    /// can't be picked). A borderless, per-pixel transparent, non-activating window owned by Revit covers the view;
    /// only the targets (alpha 1/255 discs) take clicks, everything else falls through to Revit. Wheel / middle / right
    /// button over a target hide the overlay and are re-posted to the Revit view under the cursor, so zoom, pan and the
    /// context menu still work there. The panel (FederatorWindow) positions it from an API-context tick.
    /// </summary>
    internal class BadgeHitOverlay : Window
    {
        internal struct Target
        {
            internal string Key;
            internal double X, Y, R;   // screen pixels (device)
        }

        private static readonly Brush HitFill = MakeFill();
        private readonly Canvas _canvas = new Canvas();
        private readonly Action<string, bool> _clicked;
        private readonly Func<string, string> _tip;
        private readonly Func<string, ContextMenu> _menu;
        private IntPtr _hwnd;
        private string _signature;

        internal BadgeHitOverlay(IntPtr revitMain, Action<string, bool> clicked, Func<string, string> tip, Func<string, ContextMenu> menu)
        {
            _clicked = clicked;
            _menu = menu;
            _tip = tip;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            Title = "Perseus Federator badges";
            Content = _canvas;
            if (revitMain != IntPtr.Zero) new WindowInteropHelper(this).Owner = revitMain;
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };
            PreviewMouseWheel += (s, e) => Safe(() => { e.Handled = true; Forward(WM_MOUSEWHEEL, e.Delta); });
            PreviewMouseDown += (s, e) => Safe(() =>
            {
                if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; Forward(WM_MBUTTONDOWN, 0); }
                else if (e.ChangedButton == MouseButton.Right)
                {
                    // Right-click on a badge: the panel's review menu for that change (or the selection it belongs to).
                    e.Handled = true;
                    var key = (e.OriginalSource as FrameworkElement)?.Tag as string;
                    var cm = key != null ? _menu?.Invoke(key) : null;
                    if (cm == null) { Forward(WM_RBUTTONDOWN, 0); return; }
                    cm.PlacementTarget = (UIElement)e.OriginalSource;
                    cm.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    cm.IsOpen = true;
                }
            });
        }

        /// <summary>
        /// Covers the view rectangle (screen pixels) with these targets; rebuilds the discs only when they changed.
        /// </summary>
        internal void Place(int left, int top, int right, int bottom, List<Target> targets, string signature)
        {
            if (targets == null || targets.Count == 0 || right <= left || bottom <= top) { HideTargets(); return; }
            if (!IsVisible) Show();
            if (_hwnd == IntPtr.Zero) return;
            SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top, SWP_NOACTIVATE | SWP_NOZORDER | SWP_SHOWWINDOW);
            if (signature == _signature) return;
            _signature = signature;

            double k = 1;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null) k = src.CompositionTarget.TransformFromDevice.M11;
            _canvas.Children.Clear();
            foreach (var t in targets)
            {
                double r = Math.Max(4, t.R);
                var disc = new Ellipse { Width = 2 * r * k, Height = 2 * r * k, Fill = HitFill, Cursor = Cursors.Hand, Tag = t.Key };
                Canvas.SetLeft(disc, (t.X - left - r) * k);
                Canvas.SetTop(disc, (t.Y - top - r) * k);
                disc.MouseLeftButtonDown += OnDiscDown;
                disc.MouseEnter += (s, e) => Safe(() =>
                {
                    var d = (Ellipse)s;
                    if (d.ToolTip == null) d.ToolTip = _tip?.Invoke(d.Tag as string);
                });
                _canvas.Children.Add(disc);
            }
        }

        /// <summary>Small modal text prompt (owned by Revit, so the panel doesn't jump forward). Null when cancelled.</summary>
        internal string AskText(string title, string prompt)
        {
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, Width = 340, Padding = new Thickness(3) };
            var post = new Button { Content = "Post", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(post);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock { Text = "Ctrl+Enter to post", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(buttons);
            var dlg = new Window
            {
                Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var owner = new WindowInteropHelper(this).Owner;
            if (owner != IntPtr.Zero) new WindowInteropHelper(dlg).Owner = owner;
            string result = null;
            void Done() { result = box.Text; dlg.DialogResult = true; }
            post.Click += (s, e) => Done();
            box.KeyDown += (s, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Done(); } };
            dlg.Loaded += (s, e) => box.Focus();
            HideTargets();
            dlg.ShowDialog();
            return result;
        }

        internal void HideTargets()
        {
            _signature = null;
            if (IsVisible) Hide();
        }

        private void OnDiscDown(object sender, MouseButtonEventArgs e)
        {
            Safe(() =>
            {
                e.Handled = true;
                bool add = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                _clicked?.Invoke((sender as FrameworkElement)?.Tag as string, add);
            });
        }

        // Hide, then hand the button / wheel to whatever Revit window is under the cursor.
        private void Forward(int msg, int wheelDelta)
        {
            GetCursorPos(out var pt);
            HideTargets();
            IntPtr target = WindowFromPoint(pt);
            if (target == IntPtr.Zero || target == _hwnd) return;
            if (msg == WM_MOUSEWHEEL)
            {
                PostMessage(target, msg, (IntPtr)((wheelDelta << 16) | (KeyState() & 0xFFFF)), MakeLParam(pt.X, pt.Y));
                return;
            }
            var client = pt;
            ScreenToClient(target, ref client);
            int keys = msg == WM_MBUTTONDOWN ? MK_MBUTTON : MK_RBUTTON;
            PostMessage(target, msg, (IntPtr)(keys | KeyState()), MakeLParam(client.X, client.Y));
        }

        private static int KeyState()
        {
            int k = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) k |= MK_CONTROL;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) k |= MK_SHIFT;
            return k;
        }

        private static IntPtr MakeLParam(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

        private static Brush MakeFill()
        {
            var b = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            b.Freeze();
            return b;
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Warn($"[Federator] badge overlay: {ex.Message}"); }
        }

        /// <summary>True when the foreground window belongs to this process (Revit or one of our panels).</summary>
        internal static bool RevitIsForeground()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        /// <summary>A window's screen rectangle in device pixels.</summary>
        internal static bool TryGetRect(IntPtr hwnd, out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
            left = r.Left; top = r.Top; right = r.Right; bottom = r.Bottom;
            return true;
        }

        // ── Win32 ──
        private const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        private const int WM_MOUSEWHEEL = 0x020A, WM_MBUTTONDOWN = 0x0207, WM_RBUTTONDOWN = 0x0204;
        private const int MK_RBUTTON = 0x0002, MK_SHIFT = 0x0004, MK_CONTROL = 0x0008, MK_MBUTTON = 0x0010;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    }
}
