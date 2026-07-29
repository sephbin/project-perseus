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
        private int _totalCount;

        // Tracks which project / source headers have already been written to _list.
        private readonly HashSet<string> _shownProjects = new HashSet<string>();
        private readonly HashSet<string> _shownSources  = new HashSet<string>();

        private static readonly Font _fontProject = new Font("Segoe UI", 9f,   FontStyle.Bold);
        private static readonly Font _fontSource  = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Font _fontBody    = new Font("Segoe UI", 9f,   FontStyle.Regular);

        private static readonly Color _colorProject = Color.FromArgb(0x20, 0x20, 0x20);
        private static readonly Color _colorSource  = Color.FromArgb(0x44, 0x44, 0x44);
        private static readonly Color _colorBody    = Color.FromArgb(0x33, 0x33, 0x33);

        internal AlertsReviewForm(IList<AlertDto> alerts)
        {
            Text            = "Perseus Alerts";
            Width           = 620;
            Height          = 440;
            MinimumSize     = new Size(420, 280);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            _totalCount = alerts.Count;

            _header = new Label
            {
                Text      = HeaderText(),
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                Dock      = DockStyle.Top,
                Height    = 40,
                Padding   = new Padding(12, 10, 0, 0),
                ForeColor = Color.FromArgb(0x20, 0x20, 0x20),
            };

            _list = new RichTextBox
            {
                ReadOnly    = true,
                Dock        = DockStyle.Fill,
                Font        = _fontBody,
                BorderStyle = BorderStyle.None,
                BackColor   = SystemColors.Window,
                Padding     = new Padding(8, 4, 8, 4),
                ScrollBars  = RichTextBoxScrollBars.Vertical,
            };

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

            AppendGrouped(alerts);
        }

        // Called on the UI thread via BeginInvoke when new alerts arrive while this dialog is open.
        internal void DrainAndAppend()
        {
            if (IsDisposed) return;
            var newAlerts = AlertPoller.Drain();
            if (newAlerts.Count == 0) return;
            _totalCount += newAlerts.Count;
            _header.Text = HeaderText();
            AppendGrouped(newAlerts);
            _list.SelectionStart = _list.TextLength;
            _list.ScrollToCaret();
        }

        private void AppendGrouped(IList<AlertDto> alerts)
        {
            var byProject = alerts
                .GroupBy(a => a.ProjectName ?? "(No Project)")
                .OrderBy(g => g.Key == "(No Project)" ? 1 : 0).ThenBy(g => g.Key);

            foreach (var projGroup in byProject)
            {
                string projKey = projGroup.Key;
                if (_shownProjects.Add(projKey))
                {
                    // Blank line before every project header except the very first.
                    if (_list.TextLength > 0)
                        Append("\n", _fontBody, _colorBody);
                    Append(projKey.ToUpper() + "\n", _fontProject, _colorProject);
                }

                foreach (var srcGroup in projGroup.GroupBy(a => a.SourceName ?? "Unknown Source").OrderBy(g => g.Key))
                {
                    string srcKey = projKey + "|" + srcGroup.Key;
                    if (_shownSources.Add(srcKey))
                        Append("  " + srcGroup.Key + "\n", _fontSource, _colorSource);

                    foreach (var a in srcGroup)
                        Append("    • " + a.Body + "\n", _fontBody, _colorBody);
                }
            }
        }

        private void Append(string text, Font font, Color color)
        {
            _list.SelectionStart  = _list.TextLength;
            _list.SelectionLength = 0;
            _list.SelectionFont   = font;
            _list.SelectionColor  = color;
            _list.AppendText(text);
        }

        private string HeaderText() =>
            $"Perseus Alerts  ({_totalCount} alert{(_totalCount == 1 ? "" : "s")})";
    }
}
