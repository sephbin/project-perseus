using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Owns the status-bar badges and keeps them sitting in Revit's status bar, just left of the status bar's own
    /// controls on the right (worksets, design options, selection filter...):
    ///     [ ⚠ 12 ] [ 345 ]   | worksets | design options | ...
    ///   violations  ownership
    /// Revit gives add-ins no way to add to the status bar, so each badge is a separate owned window placed over it
    /// and re-placed every 250 ms (cheap: only moves when something moved). Runs on the Revit main thread.
    /// The ownership badge is fed by OwnershipTracker; the violation badge by ViolationHighlightController.Update.
    /// Badge state is kept here even before Initialize, so updates that arrive early still show.
    /// </summary>
    internal static class StatusBadgeController
    {
        private const int Gap = 6; // px between the two badges

        private static readonly Color OwnershipFill  = Color.FromRgb(0x00, 0xA0, 0xE0); // PerseusCyan
        private static readonly Color OwnershipHover = Color.FromRgb(0x00, 0x86, 0xBD);

        private static StatusBadge _ownership, _violations;
        private static DispatcherTimer _timer;
        private static IntPtr _revit;
        private static IntPtr _statusBar;
        private static bool _loggedLayout;

        // Wanted state (applied by Place).
        private static bool _ownershipWanted;
        private static string _ownershipText = "0", _ownershipTip;
        private static int _violationCount;
        private static string _violationSeverity, _violationTip, _violationDocKey;
        private static string _activeDocKey;

        internal static Action OnOwnershipClick;
        internal static Action OnViolationsClick;

        /// <summary>Identifies a document across the two feeds (ownership tracker, violation poller).</summary>
        internal static string DocKey(Autodesk.Revit.DB.Document doc) =>
            doc == null ? null : doc.PathName + "|" + doc.Title;

        internal static void Initialize(IntPtr revitHwnd)
        {
            if (_ownership != null || revitHwnd == IntPtr.Zero) return;
            try
            {
                _revit = revitHwnd;
                _ownership = new StatusBadge(revitHwnd, OwnershipFill, OwnershipHover);
                _ownership.Clicked += () => SafeInvoke(OnOwnershipClick, "ownership");
                Color fill, hover;
                SeverityColors(null, out fill, out hover);
                _violations = new StatusBadge(revitHwnd, fill, hover);
                _violations.Clicked += () => SafeInvoke(OnViolationsClick, "violations");

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _timer.Tick += (s, e) => Place();
                _timer.Start();
                Place();
            }
            catch (Exception ex)
            {
                Log.Warn($"[StatusBadge] init failed: {ex.Message}");
                _ownership = _violations = null;
            }
        }

        // ── Ownership badge ──

        internal static void ShowOwnership(int elements, int worksets, string docTitle)
        {
            _ownershipText = elements.ToString("N0");
            _ownershipTip = $"You own {elements:N0} element(s)" +
                            (worksets > 0 ? $" and {worksets} workset(s)" : "") +
                            $" in {docTitle}.\nOther users can't edit them until you sync or relinquish.\nClick to see who owns what.";
            _ownershipWanted = true;
            Place();
        }

        internal static void HideOwnership()
        {
            _ownershipWanted = false;
            Place();
        }

        // ── Violation badge ──

        /// <summary>
        /// Latest violation counts for a document. Hidden when there are none, or while another document is active.
        /// worstSeverity: "error" / "warning" / anything else = info.
        /// </summary>
        internal static void ShowViolations(string docKey, string docTitle, int errors, int warnings, int infos)
        {
            int total = errors + warnings + infos;
            _violationDocKey = docKey;
            _violationCount = total;
            _violationSeverity = errors > 0 ? "error" : warnings > 0 ? "warning" : "info";
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors:N0} error{(errors == 1 ? "" : "s")}");
            if (warnings > 0) parts.Add($"{warnings:N0} warning{(warnings == 1 ? "" : "s")}");
            if (infos > 0) parts.Add($"{infos:N0} info");
            _violationTip = $"{total:N0} rule violation{(total == 1 ? "" : "s")} in {docTitle}" +
                            (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "") +
                            ".\nClick to open the violation list.";
            Place();
        }

        /// <summary>The document now active in Revit (null: none / family / Home). Call from Idling.</summary>
        internal static void SetActiveDocument(string docKey)
        {
            if (_activeDocKey == docKey) return;
            _activeDocKey = docKey;
            Place();
        }

        internal static void Shutdown()
        {
            try { _timer?.Stop(); _ownership?.Close(); _violations?.Close(); } catch { /* shutting down */ }
            _ownership = _violations = null;
        }

        private static void SafeInvoke(Action action, string what)
        {
            try { action?.Invoke(); }
            catch (Exception ex) { Log.Warn($"[StatusBadge] {what} click failed: {ex.Message}"); }
        }

        // Same hues as the overlay rings (ViolationHighlightServer.SeverityColor), darkened a touch so white
        // text stays readable on the small pill.
        private static void SeverityColors(string severity, out Color fill, out Color hover)
        {
            switch (severity)
            {
                case "error":   fill = Color.FromRgb(0xD2, 0x3A, 0x3A); hover = Color.FromRgb(0xB3, 0x2C, 0x2C); break;
                case "warning": fill = Color.FromRgb(0xC9, 0x7A, 0x0E); hover = Color.FromRgb(0xA8, 0x64, 0x08); break;
                default:        fill = Color.FromRgb(0x4A, 0x86, 0xC0); hover = Color.FromRgb(0x3A, 0x70, 0xA6); break;
            }
        }

        // ── Placement ──

        private static void Place()
        {
            if (_ownership == null) return;
            try
            {
                bool revitShown = !IsIconic(_revit) && IsWindowVisible(_revit);
                bool docShown = _activeDocKey != null;
                bool showOwnership = revitShown && docShown && _ownershipWanted;
                bool showViolations = revitShown && docShown && _violationCount > 0 && _violationDocKey == _activeDocKey;

                if (!showOwnership && _ownership.IsVisible) _ownership.Hide();
                if (!showViolations && _violations.IsVisible) _violations.Hide();
                if (!showOwnership && !showViolations) return;

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

                int right = anchorRight;
                if (showOwnership)
                {
                    _ownership.SetContent(_ownershipText, _ownershipTip);
                    right = PlaceBadge(_ownership, right, bar) - Gap;
                }
                if (showViolations)
                {
                    Color fill, hover;
                    SeverityColors(_violationSeverity, out fill, out hover);
                    _violations.SetColors(fill, hover);
                    _violations.SetContent("\u26A0 " + _violationCount.ToString("N0"), _violationTip);
                    PlaceBadge(_violations, right, bar);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[StatusBadge] placement failed: {ex.Message}");
            }
        }

        // Puts the badge's right edge at `right`, vertically centred on the bar, at its exact content size.
        // Returns its left edge.
        private static int PlaceBadge(StatusBadge badge, int right, RECT bar)
        {
            IntPtr hwnd = badge.Handle;
            badge.GetPixelSize(out int w, out int h);
            int x = Math.Max(bar.Left + 4, right - w);
            int y = bar.Top + ((bar.Bottom - bar.Top) - h) / 2;

            // Show first: WPF applies its own placement on a window's first Show, which would undo ours.
            if (!badge.IsVisible) badge.Show();
            GetWindowRect(hwnd, out RECT me);
            if (me.Left != x || me.Top != y || me.Right - me.Left != w || me.Bottom - me.Top != h)
                SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            return x;
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
                Log.Info($"[StatusBadge] status bar [{ClassOf(found)}] {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} " +
                         $"(found by {(byClass != IntPtr.Zero ? "class" : "shape")}); children: {string.Join(", ", kids)}");
            }
            else if (found == IntPtr.Zero && !_loggedLayout)
            {
                _loggedLayout = true;
                Log.Warn("[StatusBadge] Revit status bar not found; placing the badge at the bottom-right of the Revit window.");
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

        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

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
