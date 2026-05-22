using System;
using System.Drawing;
using System.Windows.Forms;

namespace ProjectPerseus.forms
{
    public class JwtLoginForm : Form
    {
        private readonly TextBox _usernameBox;
        private readonly TextBox _passwordBox;

        public string Username => _usernameBox.Text.Trim();
        public string Password => _passwordBox.Text;

        public JwtLoginForm(string serverUrl)
        {
            int width = 320;
            int padding = 14;
            int labelHeight = 18;
            int fieldHeight = 26;
            int gap = 8;

            this.Text = "Perseus — Server Login";
            this.Size = new Size(width, 240);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.TopMost = true;

            int y = padding;

            var lblServer = new Label
            {
                Text = $"Connecting to: {serverUrl}",
                Location = new Point(padding, y),
                Size = new Size(width - padding * 2, labelHeight),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8f)
            };
            this.Controls.Add(lblServer);
            y += labelHeight + gap + 4;

            var lblUser = new Label { Text = "Username", Location = new Point(padding, y), Size = new Size(width - padding * 2, labelHeight), Font = new Font("Segoe UI", 9f) };
            this.Controls.Add(lblUser);
            y += labelHeight + 2;

            _usernameBox = new TextBox { Location = new Point(padding, y), Size = new Size(width - padding * 2, fieldHeight) };
            this.Controls.Add(_usernameBox);
            y += fieldHeight + gap;

            var lblPass = new Label { Text = "Password", Location = new Point(padding, y), Size = new Size(width - padding * 2, labelHeight), Font = new Font("Segoe UI", 9f) };
            this.Controls.Add(lblPass);
            y += labelHeight + 2;

            _passwordBox = new TextBox { Location = new Point(padding, y), Size = new Size(width - padding * 2, fieldHeight), UseSystemPasswordChar = true };
            this.Controls.Add(_passwordBox);
            y += fieldHeight + gap + 4;

            var btnLogin = new Button
            {
                Text = "Login",
                DialogResult = DialogResult.OK,
                Location = new Point(padding, y),
                Size = new Size((width - padding * 2 - gap) / 2, 30)
            };
            btnLogin.Click += (s, e) => this.Close();
            this.Controls.Add(btnLogin);

            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(padding + btnLogin.Width + gap, y),
                Size = btnLogin.Size
            };
            btnCancel.Click += (s, e) => this.Close();
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnLogin;
            this.CancelButton = btnCancel;
        }
    }
}
