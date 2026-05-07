using System;
using System.Drawing;
using System.Windows.Forms;

namespace ProjectPerseus.ui
{
    public class BatchProgressForm : Form
    {
        private Label _lblStatus;
        private Button _btnCancel;
        private Timer _timer;
        private int _timeLeft = 5; // Set to 5 for testing so you don't wait a minute!
        public bool AbortRequested { get; private set; } = false;

        public BatchProgressForm(bool isCountdownMode)
        {
            Utl.WriteLog($"[BatchProgressForm] Constructor started. isCountdownMode = {isCountdownMode}");

            this.Text = "Perseus Nightly Batch";
            this.Size = new Size(400, 150);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ControlBox = false;
            this.TopMost = true;

            _lblStatus = new Label()
            {
                Left = 20,
                Top = 20,
                Width = 350,
                Font = new Font("Arial", 10, FontStyle.Bold)
            };

            _btnCancel = new Button()
            {
                Left = 140,
                Top = 60,
                Width = 100,
                Text = "Cancel Batch"
            };
            _btnCancel.Click += BtnCancel_Click;

            this.Controls.Add(_lblStatus);
            this.Controls.Add(_btnCancel);

            // Add lifecycle event logs to see if Windows is actually drawing it
            this.Load += (s, e) => Utl.WriteLog("[BatchProgressForm] Form 'Load' event fired.");
            this.Shown += (s, e) => Utl.WriteLog("[BatchProgressForm] Form 'Shown' event fired (Should be visible to OS now).");

            if (isCountdownMode)
            {
                _lblStatus.Text = $"Batch sync starting in {_timeLeft} seconds...\nClick Cancel to abort.";
                _timer = new Timer();
                _timer.Interval = 1000;
                _timer.Tick += Timer_Tick;

                Utl.WriteLog("[BatchProgressForm] Starting countdown timer...");
                _timer.Start();
            }
            else
            {
                _lblStatus.Text = "Batch processing in progress...\nDo not touch Revit.";
            }

            Utl.WriteLog("[BatchProgressForm] Constructor finished.");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _timeLeft--;
            Utl.WriteLog($"[BatchProgressForm] Timer Tick. Time left: {_timeLeft}s");

            if (_timeLeft > 0)
            {
                _lblStatus.Text = $"Batch sync starting in {_timeLeft} seconds...\nClick Cancel to abort.";
            }
            else
            {
                Utl.WriteLog("[BatchProgressForm] Timer reached 0. Closing form with OK result.");
                _timer.Stop();
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            Utl.WriteLog("[BatchProgressForm] User clicked Cancel button.");
            if (_timer != null) _timer.Stop();
            AbortRequested = true;
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        public void UpdateStatus(string message)
        {
            _lblStatus.Text = message;
            Application.DoEvents();
        }
    }
}