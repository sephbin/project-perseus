using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Owns the OwnershipBadge and keeps it sitting in Revit's status bar, just left of the status bar's own
    /// controls on the right (worksets, design options, selection filter...). Revit gives add-ins no way to add to
    /// the status bar, so the badge is a separate owned window placed over it and re-placed every 250 ms (cheap:
    /// only moves when the status bar moved). Runs on the Revit main thread; created lazily by OwnershipTracker.
    /// </summary>
    internal static class OwnershipBadgeController
    {
        private static OwnershipBadge _badge;
        private static DispatcherTimer _timer;
        private static IntPtr _revit;
        private static IntPtr _statusBar;
        private static bool _wanted;
        private static bool _loggedLayout;

        internal static Action OnClick;

        internal static void Initialize(IntPtr revitHwnd)
        {
            if (_badge != null || revitHwnd == IntPtr.Zero) return;
            try
            {
                _revit = revitHwnd;
                _badge = new OwnershipBadge(revitHwnd);
                _badge.Clicked += () =>
                {
                    try { OnClick?.Invoke(); }
                    catch (Exception ex) { Log.Warn($"[OwnershipBadge] click failed: {ex.Message}"); }
                };
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _timer.Tick += (s, e) => Place();
                _timer.Start();
            }
            catch (Exception ex)
            {
                Log.Warn($"[OwnershipBadge] init failed: {ex.Message}");
                _badge = null;
            }
        }

        internal static void Show(int elements, int worksets, string docTitle)
        {
            if (_badge == null) return;
            _badge.SetCount(elements, worksets, docTitle);
            _wanted = true;
            Place();
        }

        internal static void Hide()
        {
            _wanted = false;
            if (_badge != null && _badge.IsVisible) _badge.Hide();
        }

        internal static void Shutdown()
        {
            try { _timer?.Stop(); _badge?.Close(); } catch { /* shutting down */ }
            _badge = null;
        }

        private static void Place()
        {
            if (_badge == null) return;
            try
            {
                if (!_wanted || IsIconic(_revit) || !IsWindowVisible(_revit))
                {
                    if (_badge.IsVisible) _badge.Hide();
                    return;
                }

                if (_statusBar == IntPtr.Zero || !IsWindow(_statusBar) || !IsWindowVisible(_statusBar))
                    _statusBar = FindStatusBar(_revit);

                RECT bar;
                int anchorRight;
                if (_statusBar != IntPtr.Zero && GetWindowRect(_statusBar, out bar))
                {
                    anchorRight = RightClusterLeft(_statusBar, bar) - 8;
                }
                else
                {
                    // No status bar found: use the bottom strip of Revit's client area.
                    GetClientRect(_revit, out RECT client);
                    var origin = new POINT();
                    ClientToScreen(_revit, ref origin);
                    bar = new RECT { Left = origin.X, Top = origin.Y + client.Bottom - 26, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
                    anchorRight = bar.Right - 420;
                }

                if (!_badge.IsVisible) _badge.Show();
                IntPtr hwnd = _badge.Handle;
                GetWindowRect(hwnd, out RECT me);
                int w = me.Right - me.Left, h = me.Bottom - me.Top;
                int x = Math.Max(bar.Left + 4, anchorRight - w);
                int y = bar.Top + ((bar.Bottom - bar.Top) - h) / 2;

                if (me.Left != x || me.Top != y)
                    SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Log.Warn($"[OwnershipBadge] placement failed: {ex.Message}");
            }
        }

        // Revit's status bar: a "msctls_statusbar32" child of the main window, or failing that the widest visible
        // child strip sitting on the bottom edge of the main window's client area.
        private static IntPtr FindStatusBar(IntPtr main)
        {
            IntPtr byClass = IntPtr.Zero, byShape = IntPtr.Zero;
            int bestWidth = 0;
            GetClientRect(main, out RECT client);
            var origin = new POINT { X = 0, Y = client.Bottom };
            ClientToScreen(main, ref origin);
            int clientBottom = origin.Y;
            int clientWidth = client.Right;

            EnumChildWindows(main, (h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                string cls = ClassOf(h);
                if (cls.IndexOf("statusbar", StringComparison.OrdinalIgnoreCase) >= 0) { byClass = h; return false; }
                if (GetWindowRect(h, out RECT r))
                {
                    int width = r.Right - r.Left, height = r.Bottom - r.Top;
                    if (Math.Abs(r.Bottom - clientBottom) <= 4 && height >= 16 && height <= 48 &&
                        width >= clientWidth * 0.6 && width > bestWidth)
                    {
                        bestWidth = width;
                        byShape = h;
                    }
                }
                return true;
            }, IntPtr.Zero);

            IntPtr found = byClass != IntPtr.Zero ? byClass : byShape;
            if (found != IntPtr.Zero && !_loggedLayout)
            {
                _loggedLayout = true;
                GetWindowRect(found, out RECT r);
                var kids = new List<string>();
                EnumChildWindows(found, (h, l) =>
                {
                    if (IsWindowVisible(h) && GetWindowRect(h, out RECT k)) kids.Add($"{ClassOf(h)}@{k.Left - r.Left}..{k.Right - r.Left}");
                    return true;
                }, IntPtr.Zero);
                Log.Info($"[OwnershipBadge] status bar [{ClassOf(found)}] {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} " +
                         $"(found by {(byClass != IntPtr.Zero ? "class" : "shape")}); children: {string.Join(", ", kids)}");
            }
            else if (found == IntPtr.Zero && !_loggedLayout)
            {
                _loggedLayout = true;
                Log.Warn("[OwnershipBadge] Revit status bar not found; placing the badge at the bottom-right of the Revit window.");
            }
            return found;
        }

        // Left edge of the controls grouped at the right of the status bar (children in its right 60%), so the
        // badge sits just before them. Falls back to the bar's right edge.
        private static int RightClusterLeft(IntPtr bar, RECT barRect)
        {
            int threshold = barRect.Left + (int)((barRect.Right - barRect.Left) * 0.4);
            int left = barRect.Right;
            EnumChildWindows(bar, (h, l) =>
            {
                if (IsWindowVisible(h) && GetWindowRect(h, out RECT r) && r.Left >= threshold && r.Left < left) left = r.Left;
                return true;
            }, IntPtr.Zero);
            return left;
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
