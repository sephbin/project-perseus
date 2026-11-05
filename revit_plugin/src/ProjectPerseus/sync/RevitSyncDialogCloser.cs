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

        private static void TryClose(uint uiThread)
        {
            try
            {
                System.Threading.Thread.Sleep(500);
                var sb = new StringBuilder(256);

                EnumThreadWindows(uiThread, (hwnd, _) =>
                {
                    sb.Clear();
                    GetWindowText(hwnd, sb, sb.Capacity);
                    string title = sb.ToString();

                    if (title.IndexOf("Sync With Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf("Synchronize with Central", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf("Synchronising with Central", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Log.Info($"Auto-closed Revit sync dialog: '{title}'");
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Info($"Could not auto-close Revit sync dialog: {ex.Message}");
            }
        }
    }
}
