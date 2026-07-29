using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    internal class AlertsReviewForm : Form
    {
        internal AlertsReviewForm(IList<AlertDto> alerts)
        {
            Text            = "Perseus Alerts";
            Width           = 600;
            Height          = 420;
            MinimumSize     = new Size(400, 260);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            bool multiSource = alerts.Select(a => a.Title).Distinct().Count() > 1;
            string headerText = multiSource
                ? $"Perseus Alerts  ({alerts.Count} alert{(alerts.Count == 1 ? "" : "s")})"
                : $"{(alerts.Count > 0 ? alerts[0].Title ?? "Perseus" : "Perseus")}  ({alerts.Count} alert{(alerts.Count == 1 ? "" : "s")})";

            var header = new Label
            {
                Text      = headerText,
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                Dock      = DockStyle.Top,
                Height    = 40,
                Padding   = new Padding(10, 10, 0, 0),
                ForeColor = Color.FromArgb(0x30, 0x30, 0x30),
            };

            var list = new RichTextBox
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
            {
                string prefix = multiSource && !string.IsNullOrEmpty(a.Title) ? $"{a.Title}: " : "";
                list.AppendText($"• {prefix}{a.Body}\n");
            }

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

            Controls.Add(list);
            Controls.Add(header);
            Controls.Add(panel);
        }
    }
}
