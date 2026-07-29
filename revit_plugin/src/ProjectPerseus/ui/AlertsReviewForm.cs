using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using ProjectPerseus.models;

namespace ProjectPerseus.ui
{
    internal class AlertsReviewForm : Form
    {
        internal AlertsReviewForm(string sourceTitle, IList<AlertDto> alerts)
        {
            Text            = "Perseus Alerts";
            Width           = 600;
            Height          = 420;
            MinimumSize     = new Size(400, 260);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            var header = new Label
            {
                Text      = $"{sourceTitle}  ({alerts.Count} alert{(alerts.Count == 1 ? "" : "s")})",
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
                list.AppendText($"• {a.Body}\n");

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
