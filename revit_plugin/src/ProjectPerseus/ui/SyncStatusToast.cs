using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ProjectPerseus.logging;

namespace ProjectPerseus.ui
{
    /// <summary>
    /// Small status card shown at the bottom-right of the Revit window while Perseus works around a sync: "checking
    /// before sync", "paused this sync to write IDs, restarting", and the post-sync collection/upload with a progress
    /// bar. Users used to see Revit's sync dialog vanish, or Revit go unresponsive after a sync, with no explanation.
    ///
    /// It runs on its own STA thread with its own message loop, so it paints even while Revit's UI thread is blocked
    /// inside the sync event or collecting element data. Non-activating (never steals focus). Every callback is
    /// guarded: an exception on this thread would otherwise terminate Revit. Callable from any thread; progress
    /// updates are throttled so tight loops can report every element.
    /// </summary>
    internal static class SyncStatusToast
    {
        private static readonly object _lock = new object();
        private static Thread _thread;
        private static volatile ToastForm _form;
        private static IntPtr _revit;
        private static readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
        private const int ProgressIntervalMs = 100;
        private static string _lastText;

        /// <summary>Shows (or updates) the card. <paramref name="autoHideMs"/> 0 keeps it until Hide or the next Show.</summary>
        internal static void Show(string text, int autoHideMs = 0) => Post(text, null, double.NaN, autoHideMs, force: true);

        /// <summary>
        /// Shows the card with a progress bar. <paramref name="fraction"/> 0..1 fills the bar; a negative value shows a
        /// moving bar for steps that can't measure themselves (e.g. waiting for the server). Throttled to 10/s.
        /// </summary>
        internal static void Progress(string text, double fraction, string detail = null)
        {
            bool last = fraction >= 1.0;
            Post(text, detail, fraction, 0, force: last);
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

        private static void Post(string text, string detail, double fraction, int autoHideMs, bool force)
        {
            try
            {
                lock (_lock)
                {
                    // Only repeats of the same step are throttled (just the bar moving); a new step always shows, or
                    // the card could sit on a stale step for the whole of a slow one.
                    bool sameStep = text == _lastText;
                    if (!force && !double.IsNaN(fraction) && sameStep && _sinceProgress.ElapsedMilliseconds < ProgressIntervalMs) return;
                    _sinceProgress.Restart();
                    _lastText = text;
                }
                EnsureStarted();
                var f = _form;
                if (f == null || f.IsDisposed) return;
                IntPtr revit = _revit;
                f.BeginInvoke(new Action(() =>
                {
                    try { f.ShowText(text, detail, fraction, autoHideMs, revit); }
                    catch (Exception ex) { Log.Warn($"[SyncStatusToast] show failed: {ex.Message}"); }
                }));
                if (double.IsNaN(fraction)) Log.Info($"[SyncStatusToast] {text}");
            }
            catch (Exception ex) { Log.Warn($"[SyncStatusToast] {ex.Message}"); }
        }

        private static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_thread != null) return;
                _revit = Process.GetCurrentProcess().MainWindowHandle;
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
            private const int CardWidth = 340;
            private readonly Label _title;
            private readonly Label _text;
            private readonly ProgressBar _bar;
            private readonly Label _detail;
            private readonly System.Windows.Forms.Timer _hideTimer;

            internal ToastForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(0x26, 0x2E, 0x3B);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                Font = new Font("Segoe UI", 9.5f);

                var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Cyan };
                var body = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ColumnCount = 1,
                    Padding = new Padding(12, 9, 14, 10),
                    BackColor = BackColor,
                };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CardWidth - 30));

                _title = new Label { AutoSize = true, Text = "Perseus", ForeColor = Cyan, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
                _text = new Label { AutoSize = true, ForeColor = Color.White, MaximumSize = new Size(CardWidth - 30, 0), Margin = new Padding(0) };
                _bar = new ProgressBar
                {
                    Width = CardWidth - 30,
                    Height = 8,
                    Minimum = 0,
                    Maximum = 1000,
                    Margin = new Padding(0, 8, 0, 0),
                    Visible = false,
                    MarqueeAnimationSpeed = 30,
                };
                _detail = new Label
                {
                    AutoSize = true,
                    ForeColor = Color.FromArgb(0xA9, 0xB4, 0xC4),
                    Font = new Font("Segoe UI", 8.5f),
                    MaximumSize = new Size(CardWidth - 30, 0),
                    Margin = new Padding(0, 4, 0, 0),
                    Visible = false,
                };
                body.Controls.Add(_title);
                body.Controls.Add(_text);
                body.Controls.Add(_bar);
                body.Controls.Add(_detail);
                Controls.Add(body);
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

            // fraction: NaN = no bar, < 0 = moving (marquee) bar, 0..1 = filled bar.
            internal void ShowText(string text, string detail, double fraction, int autoHideMs, IntPtr revit)
            {
                _hideTimer.Stop();
                _text.Text = text;

                bool hasBar = !double.IsNaN(fraction);
                _bar.Visible = hasBar;
                if (hasBar)
                {
                    if (fraction < 0)
                    {
                        if (_bar.Style != ProgressBarStyle.Marquee) _bar.Style = ProgressBarStyle.Marquee;
                    }
                    else
                    {
                        if (_bar.Style != ProgressBarStyle.Continuous) _bar.Style = ProgressBarStyle.Continuous;
                        _bar.Value = (int)Math.Round(Math.Max(0, Math.Min(1, fraction)) * _bar.Maximum);
                    }
                }
                _detail.Visible = !string.IsNullOrEmpty(detail);
                _detail.Text = detail ?? "";

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
