using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ProjectPerseus.models;
using ProjectPerseus.queue;

namespace ProjectPerseus.ui
{
    internal class AlertsReviewForm : Form
    {
        private readonly Label _header;
        private readonly RichTextBox _list;
        private readonly string _firstTitle;
        private int _totalCount;

        internal AlertsReviewForm(IList<AlertDto> alerts)
        {
            Text            = "Perseus Alerts";
            Width           = 600;
            Height          = 420;
            MinimumSize     = new Size(400, 260);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            _totalCount  = alerts.Count;
            _firstTitle  = alerts.Count > 0 ? alerts[0].Title : null;
            bool multi   = alerts.Select(a => a.Title).Distinct().Count() > 1;

            _header = new Label
            {
                Text      = BuildHeaderText(multi),
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                Dock      = DockStyle.Top,
                Height    = 40,
                Padding   = new Padding(10, 10, 0, 0),
                ForeColor = Color.FromArgb(0x30, 0x30, 0x30),
            };

            _list = new RichTextBox
            {
                ReadOnly    = true,
                Dock        = DockStyle.Fill,
                Font        = new Font("Segoe UI", 9.5f),
                BorderStyle = BorderStyle.None,
                BackColor   = SystemColors.Window,
                Padding     = new Padding(10, 4, 10, 4),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
            };

            foreach (var a in alerts)
                _list.AppendText($"• {TitlePrefix(a.Title, multi)}{a.Body}\n");

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            var ok = new Button
            {
                Text         = "OK",
                DialogResult = DialogResult.OK,
                Width        = 88,
                Height       = 28,
                Anchor       = AnchorStyles.Right | AnchorStyles.Top,
            };
            ok.Location = new Point(panel.Width - ok.Width - 8, 8);
            panel.Controls.Add(ok);
            panel.Resize += (s, e) => ok.Location = new Point(panel.Width - ok.Width - 8, 8);
            AcceptButton = ok;

            Controls.Add(_list);
            Controls.Add(_header);
            Controls.Add(panel);
        }

        // Called on the UI thread via BeginInvoke when new alerts arrive while this dialog is open.
        internal void DrainAndAppend()
        {
            if (IsDisposed) return;
            var newAlerts = AlertPoller.Drain();
            if (newAlerts.Count == 0) return;

            _totalCount += newAlerts.Count;

            foreach (var a in newAlerts)
            {
                // Only prefix when the incoming title differs from what the form was opened with.
                bool needsPrefix = !string.IsNullOrEmpty(a.Title) && a.Title != _firstTitle;
                _list.AppendText($"• {TitlePrefix(a.Title, needsPrefix)}{a.Body}\n");
            }

            _list.ScrollToCaret();
            _header.Text = BuildHeaderText(false);
        }

        private string BuildHeaderText(bool multi)
        {
            string label = (multi || _firstTitle == null) ? "Perseus Alerts" : _firstTitle;
            return $"{label}  ({_totalCount} alert{(_totalCount == 1 ? "" : "s")})";
        }

        private static string TitlePrefix(string title, bool include) =>
            include && !string.IsNullOrEmpty(title) ? $"{title}: " : "";
    }
}
