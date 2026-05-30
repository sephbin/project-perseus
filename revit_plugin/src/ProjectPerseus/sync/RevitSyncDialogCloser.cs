using System;
using System.Runtime.InteropServices;
using System.Text;

using ProjectPerseus.logging;
namespace ProjectPerseus.sync
{
    // Win32 helpers for closing Revit's native "Sync With Central" progress dialog.
    // Used when Perseus cancels a sync via SyncWarningForm or hands off to AutoSync.
    internal static class RevitSyncDialogCloser
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        public static void TryClose()
        {
            try
            {
                System.Threading.Thread.Sleep(500);
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var sb = new StringBuilder(256);

                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint windowPid);
                    if (windowPid != (uint)pid) return true;

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
