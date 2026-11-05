using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using ProjectPerseus.logging;
namespace ProjectPerseus.sync
{
    // Closes Revit's "Sync With Central" progress window, which stays on screen after Perseus cancels a sync in
    // DocumentSynchronizingWithCentral (violation block, pending web edits, queue, ID writes).
    //
    // Looks at every top-level window of the Revit process, matching only sync-dialog titles. It must NOT be limited
    // to Revit's UI thread: on cloud models the window is the CloudWorksharing add-in's WPF "CollaborateSWCView"
    // ("Sync With Central"), which lives on its own thread (journal, 2026-11-05) — the UI-thread-only version never
    // found it. Never use this on its own: every cancel path must also tell the user what happened (a TaskDialog /
    // form of its own, or ui/SyncStatusToast), or the sync just appears to vanish.
    internal static class RevitSyncDialogCloser
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        /// <summary>Call right after e.Cancel(); closes the sync dialog shortly after, off-thread.</summary>
        public static void CloseSoon()
        {
            System.Threading.Tasks.Task.Run(() => TryClose());
        }

        private static bool IsSyncDialog(string title) =>
            title.IndexOf("Sync With Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronize with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronise with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronising with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronizing with Central", StringComparison.OrdinalIgnoreCase) >= 0;

        // The window can appear (or come back) only after the sync handler returns, e.g. while a browser opens for
        // the queue page, so keep looking for a few seconds and close each sync window once. If none turns up, log the
        // visible Revit window titles so the right one can be matched.
        private static void TryClose()
        {
            try
            {
                uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                var sb = new StringBuilder(256);
                var closed = new HashSet<IntPtr>();
                var seen = new HashSet<string>();
                var deadline = DateTime.UtcNow.AddSeconds(6);

                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(200);
                    EnumWindows((hwnd, _) =>
                    {
                        GetWindowThreadProcessId(hwnd, out uint owner);
                        if (owner != pid || !IsWindowVisible(hwnd)) return true;
                        sb.Clear();
                        GetWindowText(hwnd, sb, sb.Capacity);
                        string title = sb.ToString();
                        if (title.Length > 0) seen.Add(title);

                        if (IsSyncDialog(title) && closed.Add(hwnd))
                        {
                            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            Log.Info($"Auto-closed Revit sync dialog: '{title}'");
                        }
                        return true;
                    }, IntPtr.Zero);
                    // Once one is closed, give Revit a moment in case it reopens it, then stop.
                    if (closed.Count > 0 && deadline > DateTime.UtcNow.AddSeconds(1.5))
                        deadline = DateTime.UtcNow.AddSeconds(1.5);
                }

                if (closed.Count == 0)
                    Log.Info($"No Revit sync dialog found to close; visible Revit windows: {string.Join(" | ", seen)}");
            }
            catch (Exception ex)
            {
                Log.Info($"Could not auto-close Revit sync dialog: {ex.Message}");
            }
        }
    }
}
