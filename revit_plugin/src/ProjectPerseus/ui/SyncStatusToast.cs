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
            private readonly ActivityBar _bar;
            private readonly Label _detail;
            private readonly System.Windows.Forms.Timer _hideTimer;
            // Animates the bar and updates the clock on this (the toast's own) thread, so the card visibly keeps going
            // while Revit's UI thread is busy; a long gap since the last update is called out.
            private readonly System.Windows.Forms.Timer _tick;
            private DateTime _shownAt, _lastUpdate;
            private const int StaleSeconds = 15;

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
                _bar = new ActivityBar
                {
                    Width = CardWidth - 30,
                    Height = 8,
                    Margin = new Padding(0, 8, 0, 0),
                    Visible = false,
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

                _tick = new System.Windows.Forms.Timer { Interval = 40 };
                _tick.Tick += (s, e) =>
                {
                    try { Tick(); }
                    catch (Exception ex) { _tick.Stop(); Log.Warn($"[SyncStatusToast] tick failed: {ex.Message}"); }
                };
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
                if (hasBar) _bar.Fraction = fraction < 0 ? -1 : Math.Max(0, Math.Min(1, fraction));
                _detail.Visible = !string.IsNullOrEmpty(detail);
                _detail.Text = detail ?? "";

                _lastUpdate = DateTime.UtcNow;
                if (!Visible) { _shownAt = _lastUpdate; Show(); }
                if (hasBar) { if (!_tick.Enabled) _tick.Start(); }
                else { _tick.Stop(); _title.Text = "Perseus"; }
                UpdateClock();
                PerformLayout();
                Place(revit);
                if (autoHideMs > 0) { _hideTimer.Interval = autoHideMs; _hideTimer.Start(); }
            }

            internal void HideNow()
            {
                _hideTimer.Stop();
                _tick.Stop();
                _title.Text = "Perseus";
                if (Visible) Hide();
            }

            private int _lastClockSecond = -1;

            private void Tick()
            {
                if (!Visible || !_bar.Visible) { _tick.Stop(); return; }
                _bar.Advance();
                UpdateClock();
            }

            private void UpdateClock()
            {
                if (!_bar.Visible) return;
                var now = DateTime.UtcNow;
                int elapsed = (int)(now - _shownAt).TotalSeconds;
                int quiet = (int)(now - _lastUpdate).TotalSeconds;
                int key = elapsed * 2 + (quiet >= StaleSeconds ? 1 : 0);
                if (key == _lastClockSecond) return;
                _lastClockSecond = key;
                string clock = $"Perseus  ·  {elapsed / 60}:{elapsed % 60:00}";
                if (quiet >= StaleSeconds) clock += $"  ·  no update for {quiet} s (Revit is busy)";
                _title.Text = clock;
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

            /// <summary>
            /// Owner-drawn progress bar: a filled part for known progress (or a sweeping segment when unknown) with a
            /// highlight that slides across it on every tick, so it never looks frozen. No visual-styles dependency.
            /// </summary>
            private sealed class ActivityBar : Control
            {
                private static readonly Color Track = Color.FromArgb(0x3A, 0x44, 0x55);
                private double _phase;
                internal double Fraction = -1;   // < 0 = unknown

                internal ActivityBar()
                {
                    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                             | ControlStyles.ResizeRedraw, true);
                    BackColor = Color.FromArgb(0x26, 0x2E, 0x3B);
                }

                internal void Advance()
                {
                    _phase = (_phase + 0.018) % 1.0;
                    Invalidate();
                }

                protected override void OnPaint(PaintEventArgs e)
                {
                    var g = e.Graphics;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    int w = Width, h = Height;
                    using (var track = new SolidBrush(Track)) g.FillRectangle(track, 0, 0, w, h);
                    if (Fraction < 0)
                    {
                        // Unknown: a cyan segment sweeping across the track.
                        int seg = Math.Max(24, w / 4);
                        int x = (int)(_phase * (w + seg)) - seg;
                        using (var b = new SolidBrush(Cyan)) g.FillRectangle(b, x, 0, seg, h);
                        return;
                    }
                    int fill = (int)Math.Round(w * Fraction);
                    using (var b = new SolidBrush(Cyan)) g.FillRectangle(b, 0, 0, fill, h);
                    // Highlight sliding over the filled part.
                    if (fill > 6)
                    {
                        int band = Math.Max(18, w / 6);
                        int x = (int)(_phase * (fill + band)) - band;
                        var rect = new Rectangle(x, 0, band, h);
                        using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.FromArgb(0, 255, 255, 255),
                                                                                          Color.FromArgb(0, 255, 255, 255), 0f))
                        {
                            var blend = new System.Drawing.Drawing2D.ColorBlend
                            {
                                Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(140, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                                Positions = new[] { 0f, 0.5f, 1f },
                            };
                            lg.InterpolationColors = blend;
                            g.SetClip(new Rectangle(0, 0, fill, h));
                            g.FillRectangle(lg, rect);
                            g.ResetClip();
                        }
                    }
                }
            }
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        }
    }
}
