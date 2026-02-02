using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Windows.Media.Media3D;

public class SyncWarningForm : Form
{
    // Define the options clearly
    public enum SyncAction { SyncAnyway, JoinQueue, Cancel }

    // This property holds the user's decision
    public SyncAction SelectedAction { get; private set; } = SyncAction.Cancel;

    public SyncWarningForm(int count, string userList)
    {

        int width = 250;
        int height = 450;
        int padding = 10;
        int buttonHeight = 30;

        // 1. Form Settings
        this.Text = "Sync Queue Alert";
        this.Size = new Size(width, height);
        this.StartPosition = FormStartPosition.CenterScreen; // Fixes position
        this.TopMost = true; // Fixes "appearing under windows"
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MinimizeBox = false;
        this.MaximizeBox = false;

        // 2. Main Label
        Label lblMessage = new Label();
        lblMessage.Text = $"{count} user(s) are currently in the sync queue:\n\n{userList}\n\nDo you want to Sync Anyway or Cancel?";
        lblMessage.Location = new Point(padding, padding);
        lblMessage.Size = new Size(this.ClientSize.Width - padding, 180);
        lblMessage.Font = new Font("Segoe UI", 10);
        this.Controls.Add(lblMessage);

        // 3. Sync Anyway Button
        Button btnSync = new Button();
        btnSync.Text = "Sync Anyway";
        btnSync.DialogResult = DialogResult.Yes;
        btnSync.Location = new Point(padding, this.ClientSize.Height - (3 * (buttonHeight + padding)));
        btnSync.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
        btnSync.Click += (s, e) => { SelectedAction = SyncAction.SyncAnyway; this.Close(); };
        this.Controls.Add(btnSync);

        // 4. Join Button
        Button btnJoin = new Button();
        btnJoin.Text = "Join Queue";
        btnJoin.DialogResult = DialogResult.Cancel;
        btnJoin.Location = new Point(padding, this.ClientSize.Height - (2* (buttonHeight + padding)));
        btnJoin.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
        btnJoin.Click += (s, e) => { SelectedAction = SyncAction.JoinQueue; this.Close(); };
        this.Controls.Add(btnJoin);

        // 5. Cancel Button
        Button btnCancel = new Button();
        btnCancel.Text = "Cancel Sync";
        btnCancel.DialogResult = DialogResult.Cancel;
        btnCancel.Location = new Point(padding, this.ClientSize.Height - (1 * (buttonHeight + padding)));
        btnCancel.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
        btnCancel.Click += (s, e) => { SelectedAction = SyncAction.Cancel; this.Close(); };
        this.Controls.Add(btnCancel);

        // Default accept button (Enter key triggers Sync)
        this.AcceptButton = btnSync;
        this.CancelButton = btnCancel;
    }
}