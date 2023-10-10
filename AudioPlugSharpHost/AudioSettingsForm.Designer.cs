namespace AudioPlugSharpHost
{
    partial class AudioSettingsForm
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
            deviceCombo = new ComboBox();
            label1 = new Label();
            applyButton = new Button();
            cancelButton = new Button();
            SuspendLayout();
            // 
            // deviceCombo
            // 
            deviceCombo.FormattingEnabled = true;
            deviceCombo.Location = new Point(86, 12);
            deviceCombo.Name = "deviceCombo";
            deviceCombo.Size = new Size(163, 23);
            deviceCombo.TabIndex = 0;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(12, 15);
            label1.Name = "label1";
            label1.Size = new Size(68, 15);
            label1.TabIndex = 1;
            label1.Text = "Asio Device";
            // 
            // applyButton
            // 
            applyButton.Location = new Point(86, 85);
            applyButton.Name = "applyButton";
            applyButton.Size = new Size(75, 23);
            applyButton.TabIndex = 2;
            applyButton.Text = "Apply";
            applyButton.UseVisualStyleBackColor = true;
            applyButton.Click += applyButton_Click;
            // 
            // cancelButton
            // 
            cancelButton.Location = new Point(174, 85);
            cancelButton.Name = "cancelButton";
            cancelButton.Size = new Size(75, 23);
            cancelButton.TabIndex = 3;
            cancelButton.Text = "Cancel";
            cancelButton.UseVisualStyleBackColor = true;
            cancelButton.Click += cancelButton_Click;
            // 
            // AudioSettingsForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(261, 120);
            Controls.Add(cancelButton);
            Controls.Add(applyButton);
            Controls.Add(label1);
            Controls.Add(deviceCombo);
            Name = "AudioSettingsForm";
            Text = "Audio Settings";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ComboBox deviceCombo;
        private Label label1;
        private Button applyButton;
        private Button cancelButton;
    }
}