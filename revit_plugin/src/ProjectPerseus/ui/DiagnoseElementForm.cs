using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ProjectPerseus.ui
{
    // Simple input dialog for DiagnoseElementCommand.
    // Built entirely in code — no designer file needed.
    public class DiagnoseElementForm : Form
    {
        private readonly TextBox _idsBox;

        public DiagnoseElementForm()
        {
            Text = "Perseus — Diagnose Elements";
            Width = 420;
            Height = 210;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var label = new Label
            {
                Text = "Element IDs to diagnose (comma or newline separated):",
                Left = 12, Top = 12, Width = 384, Height = 36,
                AutoSize = false
            };

            _idsBox = new TextBox
            {
                Left = 12, Top = 52, Width = 384, Height = 80,
                Multiline = true, ScrollBars = ScrollBars.Vertical
            };

            var okBtn = new Button
            {
                Text = "Run Diagnostic",
                Left = 196, Top = 148, Width = 120, Height = 28,
                DialogResult = DialogResult.OK
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Left = 324, Top = 148, Width = 72, Height = 28,
                DialogResult = DialogResult.Cancel
            };

            Controls.AddRange(new Control[] { label, _idsBox, okBtn, cancelBtn });
            AcceptButton = okBtn;
            CancelButton = cancelBtn;
        }

        public IList<long> GetElementIds()
        {
            var ids = new List<long>();
            var raw = _idsBox.Text ?? "";
            foreach (var part in raw.Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(part.Trim(), out var id))
                    ids.Add(id);
            }
            return ids;
        }

        public void SetElementIds(IEnumerable<long> ids)
        {
            _idsBox.Text = string.Join(System.Environment.NewLine, ids);
        }
    }
}
