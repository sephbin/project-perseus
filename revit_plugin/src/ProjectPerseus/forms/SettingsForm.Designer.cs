namespace ProjectPerseus
{
    partial class SettingsForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.syncUrltextBox = new System.Windows.Forms.TextBox();
            this.syncUrlLabel = new System.Windows.Forms.Label();
            this.apiTokenLabel = new System.Windows.Forms.Label();
            this.apiTokenTextBox = new System.Windows.Forms.TextBox();
            this.saveButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // syncUrltextBox
            // 
            this.syncUrltextBox.Location = new System.Drawing.Point(75, 15);
            this.syncUrltextBox.Name = "syncUrltextBox";
            this.syncUrltextBox.Size = new System.Drawing.Size(466, 20);
            this.syncUrltextBox.TabIndex = 0;
            // 
            // syncUrlLabel
            // 
            this.syncUrlLabel.AutoSize = true;
            this.syncUrlLabel.Location = new System.Drawing.Point(10, 18);
            this.syncUrlLabel.Name = "syncUrlLabel";
            this.syncUrlLabel.Size = new System.Drawing.Size(59, 13);
            this.syncUrlLabel.TabIndex = 1;
            this.syncUrlLabel.Text = "Sync URL:";
            // 
            // apiTokenLabel
            // 
            this.apiTokenLabel.AutoSize = true;
            this.apiTokenLabel.Location = new System.Drawing.Point(10, 44);
            this.apiTokenLabel.Name = "apiTokenLabel";
            this.apiTokenLabel.Size = new System.Drawing.Size(61, 13);
            this.apiTokenLabel.TabIndex = 3;
            this.apiTokenLabel.Text = "API Token:";
            // 
            // apiTokenTextBox
            // 
            this.apiTokenTextBox.Location = new System.Drawing.Point(77, 41);
            this.apiTokenTextBox.Name = "apiTokenTextBox";
            this.apiTokenTextBox.Size = new System.Drawing.Size(464, 20);
            this.apiTokenTextBox.TabIndex = 2;
            // 
            // saveButton
            // 
            this.saveButton.Location = new System.Drawing.Point(383, 83);
            this.saveButton.Name = "saveButton";
            this.saveButton.Size = new System.Drawing.Size(75, 23);
            this.saveButton.TabIndex = 4;
            this.saveButton.Text = "Save";
            this.saveButton.UseVisualStyleBackColor = true;
            this.saveButton.Click += new System.EventHandler(this.saveButton_Click);
            // 
            // cancelButton
            // 
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Location = new System.Drawing.Point(464, 83);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(75, 23);
            this.cancelButton.TabIndex = 5;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.cancelButton_Click);
            // 
            // SettingsForm
            // 
            this.AcceptButton = this.saveButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(553, 118);
            this.ControlBox = false;
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.saveButton);
            this.Controls.Add(this.apiTokenLabel);
            this.Controls.Add(this.apiTokenTextBox);
            this.Controls.Add(this.syncUrlLabel);
            this.Controls.Add(this.syncUrltextBox);
            this.Name = "SettingsForm";
            this.RightToLeft = System.Windows.Forms.RightToLeft.No;
            this.ShowInTaskbar = false;
            this.Text = "Project Perseus Settings";
            this.TopMost = true;
            this.Load += new System.EventHandler(this.Settings_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox syncUrltextBox;
        private System.Windows.Forms.Label syncUrlLabel;
        private System.Windows.Forms.Label apiTokenLabel;
        private System.Windows.Forms.TextBox apiTokenTextBox;
        private System.Windows.Forms.Button saveButton;
        private System.Windows.Forms.Button cancelButton;
    }
}