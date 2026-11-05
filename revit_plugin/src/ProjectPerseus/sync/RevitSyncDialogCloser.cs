using System;
using System.Runtime.InteropServices;
using System.Text;

using ProjectPerseus.logging;
namespace ProjectPerseus.sync
{
    // Closes Revit's "Synchronize with Central" progress dialog, which can stay on screen after Perseus cancels a
    // sync in DocumentSynchronizingWithCentral (violation block, pending web edits, queue, ID writes).
    //
    // Only windows owned by Revit's UI thread are looked at (not every window in the process, as before), and only
    // titles that are Revit's sync dialog. Never use it on its own: every cancel path must also tell the user what
    // happened (a TaskDialog / form of its own, or ui/SyncStatusToast), or the sync just appears to vanish.
    internal static class RevitSyncDialogCloser
    {
        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        /// <summary>Call on Revit's UI thread right after e.Cancel(); closes the dialog shortly after, off-thread.</summary>
        public static void CloseSoon()
        {
            uint uiThread = GetCurrentThreadId();
            System.Threading.Tasks.Task.Run(() => TryClose(uiThread));
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static bool IsSyncDialog(string title) =>
            title.IndexOf("Sync With Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronize with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronise with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronising with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
            title.IndexOf("Synchronizing with Central", StringComparison.OrdinalIgnoreCase) >= 0;

        // The dialog can appear (or come back) only after the sync handler returns, e.g. while a browser opens for
        // the queue page, so one look at 500 ms often missed it. Keep looking for a few seconds; close each sync
        // dialog once. If none turns up, log the visible UI-thread window titles so the right one can be matched.
        private static void TryClose(uint uiThread)
        {
            try
            {
                var sb = new StringBuilder(256);
                var closed = new System.Collections.Generic.HashSet<IntPtr>();
                var seen = new System.Collections.Generic.HashSet<string>();
                var deadline = DateTime.UtcNow.AddSeconds(6);

                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(200);
                    EnumThreadWindows(uiThread, (hwnd, _) =>
                    {
                        if (!IsWindowVisible(hwnd)) return true;
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
