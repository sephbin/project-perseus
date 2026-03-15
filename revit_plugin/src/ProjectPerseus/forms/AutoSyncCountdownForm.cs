using System;
using System.Windows.Forms;

namespace ProjectPerseus.ui
{
    public class AutoSyncCountdownForm : Form
    {
        private Label _lblMessage;
        private Button _btnCancel;
        private Button _btnSyncNow;
        private Timer _timer;
        private int _timeLeft = 5;

        public AutoSyncCountdownForm()
        {
            // Basic form setup
            this.Text = "Perseus Auto-Sync";
            this.Size = new System.Drawing.Size(350, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ControlBox = false; // Hide the 'X' to force a choice
            this.TopMost = true;

            _lblMessage = new Label()
            {
                Left = 20,
                Top = 20,
                Width = 300,
                Text = $"Auto-syncing in {_timeLeft} seconds...",
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };

            _btnSyncNow = new Button()
            {
                Left = 50,
                Top = 60,
                Width = 100,
                Text = "Sync Now",
                DialogResult = DialogResult.OK
            };

            _btnCancel = new Button()
            {
                Left = 180,
                Top = 60,
                Width = 100,
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(_lblMessage);
            this.Controls.Add(_btnSyncNow);
            this.Controls.Add(_btnCancel);

            // Setup and start the timer
            _timer = new Timer();
            _timer.Interval = 1000; // 1 second ticks
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _timeLeft--;
            if (_timeLeft > 0)
            {
                _lblMessage.Text = $"Auto-syncing in {_timeLeft} seconds...";
            }
            else
            {
                _timer.Stop();
                this.DialogResult = DialogResult.OK; // Timer finished, proceed with sync
                this.Close();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.OnFormClosed(e);
        }
    }
}