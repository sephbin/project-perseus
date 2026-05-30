using System.Drawing;
using System.Windows.Forms;

namespace ProjectPerseus.ui
{
    public class SyncWarningForm : Form
    {
        public enum SyncAction { SyncAnyway, JoinQueueAndAutoSync, JoinQueue, Cancel }

        public SyncAction SelectedAction { get; private set; } = SyncAction.Cancel;

        public SyncWarningForm(int count, string userList)
        {
            int width = 250;
            int height = 450;
            int padding = 10;
            int buttonHeight = 30;

            this.Text = "Sync Queue Alert";
            this.Size = new Size(width, height);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = true;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;

            Label lblMessage = new Label();
            lblMessage.Text = $"{count} user(s) are currently in the sync queue:\n\n{userList}\n\nDo you want to Sync Anyway or Cancel?";
            lblMessage.Location = new Point(padding, padding);
            lblMessage.Size = new Size(this.ClientSize.Width - padding, 180);
            lblMessage.Font = new Font("Segoe UI", 10);
            this.Controls.Add(lblMessage);

            Button btnSync = new Button();
            btnSync.Text = "Sync Anyway";
            btnSync.DialogResult = DialogResult.Yes;
            btnSync.Location = new Point(padding, this.ClientSize.Height - (4 * (buttonHeight + padding)));
            btnSync.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
            btnSync.Click += (s, e) => { SelectedAction = SyncAction.SyncAnyway; this.Close(); };
            this.Controls.Add(btnSync);

            Button btnJoinandAuto = new Button();
            btnJoinandAuto.Text = "Join Queue | Auto Sync";
            btnJoinandAuto.DialogResult = DialogResult.Cancel;
            btnJoinandAuto.Location = new Point(padding, this.ClientSize.Height - (3 * (buttonHeight + padding)));
            btnJoinandAuto.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
            btnJoinandAuto.Click += (s, e) => { SelectedAction = SyncAction.JoinQueueAndAutoSync; this.Close(); };
            this.Controls.Add(btnJoinandAuto);

            Button btnJoin = new Button();
            btnJoin.Text = "Join Queue | Manually Sync";
            btnJoin.DialogResult = DialogResult.Cancel;
            btnJoin.Location = new Point(padding, this.ClientSize.Height - (2 * (buttonHeight + padding)));
            btnJoin.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
            btnJoin.Click += (s, e) => { SelectedAction = SyncAction.JoinQueue; this.Close(); };
            this.Controls.Add(btnJoin);

            Button btnCancel = new Button();
            btnCancel.Text = "Cancel Sync";
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Location = new Point(padding, this.ClientSize.Height - (1 * (buttonHeight + padding)));
            btnCancel.Size = new Size(this.ClientSize.Width - (padding * 2), buttonHeight);
            btnCancel.Click += (s, e) => { SelectedAction = SyncAction.Cancel; this.Close(); };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnSync;
            this.CancelButton = btnCancel;
        }
    }
}
