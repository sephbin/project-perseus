using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProjectPerseus.config;

namespace ProjectPerseus.ui
{
    public partial class SettingsForm : Form
    {
        public SettingsForm()
        {
            InitializeComponent();
        }

        private void Settings_Load(object sender, EventArgs e)
        {
            syncUrltextBox.Text = Config.Instance.BaseUrl;
            UpdateTokenStatus();
        }

        private void UpdateTokenStatus()
        {
            tokenStatusLabel.Text = ProjectPerseus.auth.AuthService.HasPersonalApiToken()
                ? "Token stored."
                : "No token stored.";
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void saveButton_Click(object sender, EventArgs e)
        {
            Config.Instance.BaseUrl = syncUrltextBox.Text;
            ProjectPerseus.auth.AuthService.Reset();

            string newToken = apiTokenTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(newToken))
                ProjectPerseus.auth.AuthService.StorePersonalApiToken(newToken);

            Close();
        }

        private void clearTokenButton_Click(object sender, EventArgs e)
        {
            ProjectPerseus.auth.AuthService.ClearPersonalApiToken();
            apiTokenTextBox.Text = "";
            UpdateTokenStatus();
        }
    }
}
