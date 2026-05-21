using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ProjectPerseus.forms
{
    public class QueueWebForm : Form
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int HTTOP = 12;
        private const int HTBOTTOM = 15;
        private const int RESIZE_GRIP = 6;

        private readonly WebView2 _webView;
        private readonly string _url;

        public QueueWebForm(string url)
        {
            _url = url;

            int screenHeight = Screen.FromControl(this).WorkingArea.Height;
            this.Size = new Size(500, screenHeight);
            this.MinimumSize = new Size(500, 300);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;

            _webView = new WebView2 { Dock = DockStyle.Fill };
            this.Controls.Add(_webView);

            this.Load += OnLoad;
            this.Shown += (s, e) => { this.Activate(); SetForegroundWindow(this.Handle); };
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProjectPerseus", "WebView2Cache");

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                // Inject drag bridge — persists across React router navigations.
                // Any element with data-drag on it becomes a drag handle.
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    document.addEventListener('mousedown', function(e) {
                        if (e.button === 0 && e.target.closest('[data-drag]')) {
                            window.chrome.webview.postMessage('drag');
                        }
                    });
                ");

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.Navigate(_url);
            }
            catch (Exception ex)
            {
                Utl.WriteLog($"WebView2 unavailable ({ex.Message}). Falling back to system browser.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _url,
                    UseShellExecute = true
                });
                this.Close();
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "drag":
                    ReleaseCapture();
                    SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                    break;
                case "syncboat:close":
                    this.Close();
                    break;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                var cursor = PointToClient(Cursor.Position);
                if (cursor.Y >= ClientSize.Height - RESIZE_GRIP)
                    m.Result = (IntPtr)HTBOTTOM;
                else if (cursor.Y <= RESIZE_GRIP)
                    m.Result = (IntPtr)HTTOP;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _webView?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
