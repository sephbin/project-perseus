using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Small status card shown at the bottom-right of the Revit window while Perseus works around a sync: "checking
    /// before sync", "paused this sync to write IDs, restarting", and so on. Users used to see Revit's sync
    /// dialog vanish with no explanation while Perseus cancelled and re-ran the sync; this says what is happening.
    ///
    /// It runs on its own STA thread with its own message loop, so it paints even while Revit's UI thread is blocked
    /// inside the sync event. Non-activating (never steals focus). Every callback is guarded: an exception on this
    /// thread would otherwise terminate Revit. Callable from any thread.
    /// </summary>
    internal static class SyncStatusToast
    {
        private static readonly object _lock = new object();
        private static Thread _thread;
        private static volatile ToastForm _form;
        private static IntPtr _revit;

        /// <summary>Shows (or updates) the card. <paramref name="autoHideMs"/> 0 keeps it until Hide or the next Show.</summary>
        internal static void Show(string text, int autoHideMs = 0)
        {
            try
            {
                EnsureStarted();
                var f = _form;
                if (f == null || f.IsDisposed) return;
                IntPtr revit = _revit;
                f.BeginInvoke(new Action(() =>
                {
                    try { f.ShowText(text, autoHideMs, revit); }
                    catch (Exception ex) { Log.Warn($"[SyncStatusToast] show failed: {ex.Message}"); }
                }));
                Log.Info($"[SyncStatusToast] {text}");
            }
            catch (Exception ex) { Log.Warn($"[SyncStatusToast] {ex.Message}"); }
        }

        internal static void Hide()
        {
            try
            {
                var f = _form;
                if (f == null || f.IsDisposed) return;
                f.BeginInvoke(new Action(() =>
                {
                    try { f.HideNow(); }
                    catch (Exception ex) { Log.Warn($"[SyncStatusToast] hide failed: {ex.Message}"); }
                }));
            }
            catch { /* best effort */ }
        }

        private static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_thread != null) return;
                _revit = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    try
                    {
                        var form = new ToastForm();
                        IntPtr unused = form.Handle; // create the window (hidden) on this thread
                        _form = form;
                        ready.Set();
                        Application.Run();
                    }
                    catch (Exception ex)
                    {
                        try { Log.Warn($"[SyncStatusToast] UI thread stopped: {ex.Message}"); } catch { }
                    }
                    finally { ready.Set(); }
                })
                {
                    IsBackground = true,
                    Name = "Perseus-SyncStatus",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait(2000);
            }
        }

        private sealed class ToastForm : Form
        {
            private static readonly Color Cyan = Color.FromArgb(0x00, 0xA0, 0xE0);
            private readonly Label _label;
            private readonly System.Windows.Forms.Timer _hideTimer;

            internal ToastForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(0x26, 0x2E, 0x3B);
                Padding = new Padding(14, 10, 14, 10);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;

                var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Cyan };
                _label = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(360, 0),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9.5f),
                    Padding = new Padding(10, 0, 0, 0),
                    Dock = DockStyle.Fill,
                };
                Controls.Add(_label);
                Controls.Add(accent);

                _hideTimer = new System.Windows.Forms.Timer();
                _hideTimer.Tick += (s, e) =>
                {
                    try { HideNow(); }
                    catch (Exception ex) { Log.Warn($"[SyncStatusToast] auto-hide failed: {ex.Message}"); }
                };
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
                    return cp;
                }
            }

            internal void ShowText(string text, int autoHideMs, IntPtr revit)
            {
                _hideTimer.Stop();
                _label.Text = "Perseus\n" + text;
                if (!Visible) Show();
                PerformLayout();
                Place(revit);
                if (autoHideMs > 0) { _hideTimer.Interval = autoHideMs; _hideTimer.Start(); }
            }

            internal void HideNow()
            {
                _hideTimer.Stop();
                if (Visible) Hide();
            }

            // Bottom-right of the Revit window, clear of the status bar.
            private void Place(IntPtr revit)
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                if (revit != IntPtr.Zero && GetWindowRect(revit, out RECT r) && r.Right > r.Left)
                    area = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                Location = new Point(area.Right - Width - 24, area.Bottom - Height - 56);
            }

            [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        }
    }
}
