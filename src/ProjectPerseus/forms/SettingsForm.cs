using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProjectPerseus
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
            apiTokenTextBox.Text = Config.Instance.ApiToken;
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void saveButton_Click(object sender, EventArgs e)
        {
            Config.Instance.BaseUrl = syncUrltextBox.Text;
            Config.Instance.ApiToken = apiTokenTextBox.Text;
            Close();
        }
    }
}
