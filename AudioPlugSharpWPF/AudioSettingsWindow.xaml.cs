using System.Windows;
using AudioPlugSharp.Asio;

namespace AudioPlugSharpWPF
{
    /// <summary>
    /// Interaction logic for AudioSettingsWindow.xaml
    /// </summary>
    public partial class AudioSettingsWindow : Window
    {
        public string AsioDeviceName { get; set; }

        public AudioSettingsWindow()
        {
            InitializeComponent();

            foreach (var entry in AsioDriver.GetAsioDriverEntries())
            {
                AsioCombo.Items.Add(entry.Name);
            }

            AsioCombo.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            this.AsioDeviceName = AsioCombo.SelectedValue.ToString();

            DialogResult = true;

            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;

            Close();
        }
    }
}
