using AudioPlugSharp.Asio;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AudioPlugSharpHost
{
    public partial class AudioSettingsForm : Form
    {
        public string AsioDeviceName { get; set; }

        public AudioSettingsForm()
        {
            InitializeComponent();

            FormBorderStyle = FormBorderStyle.FixedDialog;

            foreach (var entry in AsioDriver.GetAsioDriverEntries())
            {
                deviceCombo.Items.Add(entry.Name);
            }

            deviceCombo.SelectedItem = AsioDeviceName;

            DialogResult = DialogResult.Cancel;
        }

        private void applyButton_Click(object sender, EventArgs e)
        {
            this.AsioDeviceName = deviceCombo.SelectedItem.ToString();

            DialogResult = DialogResult.OK;

            Close();
        }

        private void cancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;

            Close();
        }
    }
}
