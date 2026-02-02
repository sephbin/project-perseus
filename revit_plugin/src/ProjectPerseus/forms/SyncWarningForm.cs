using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

public class SyncWarningForm : Form
{
    public bool ShouldSync { get; private set; } = false;

    public SyncWarningForm(int count, string userList)
    {
        // 1. Form Settings
        this.Text = "Sync Queue Alert";
        this.Size = new Size(400, 350);
        this.StartPosition = FormStartPosition.CenterScreen; // Fixes position
        this.TopMost = true; // Fixes "appearing under windows"
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MinimizeBox = false;
        this.MaximizeBox = false;

        // 2. Main Label
        Label lblMessage = new Label();
        lblMessage.Text = $"{count} user(s) are currently in the sync queue:\n\n{userList}\n\nDo you want to Sync Anyway or Cancel?";
        lblMessage.Location = new Point(20, 20);
        lblMessage.Size = new Size(340, 180);
        lblMessage.Font = new Font("Segoe UI", 10);
        this.Controls.Add(lblMessage);

        // 3. Sync Anyway Button
        Button btnSync = new Button();
        btnSync.Text = "Sync Anyway";
        btnSync.DialogResult = DialogResult.Yes;
        btnSync.Location = new Point(20, 220);
        btnSync.Size = new Size(160, 40);
        btnSync.Click += (s, e) => { ShouldSync = true; this.Close(); };
        this.Controls.Add(btnSync);

        // 4. Cancel Button
        Button btnCancel = new Button();
        btnCancel.Text = "Cancel Sync";
        btnCancel.DialogResult = DialogResult.Cancel;
        btnCancel.Location = new Point(200, 220);
        btnCancel.Size = new Size(160, 40);
        btnCancel.Click += (s, e) => { ShouldSync = false; this.Close(); };
        this.Controls.Add(btnCancel);

        // Default accept button (Enter key triggers Sync)
        this.AcceptButton = btnSync;
        this.CancelButton = btnCancel;
    }
}