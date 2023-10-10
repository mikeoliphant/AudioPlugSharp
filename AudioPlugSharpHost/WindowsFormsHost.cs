using AudioPlugSharp;
using System.Reflection;

namespace AudioPlugSharpHost
{
    public class WindowsFormsHost<T> where T : IAudioPlugin, IAudioPluginProcessor, IAudioPluginEditor
    {
        public T Plugin { get; private set; }

        AudioSettingsForm settingsForm;
        AudioPlugSharpHost<T> audioHost;
        NotifyIcon notifyIcon;

        public WindowsFormsHost(T plugin)
        {
            this.Plugin = plugin;

            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
            notifyIcon.Text = plugin.PluginName;
            notifyIcon.Click += NotifyIcon_Click;

            settingsForm = new AudioSettingsForm();

            audioHost = new AudioPlugSharpHost<T>(Plugin);
        }

        public void Run()
        {
            notifyIcon.Visible = true;

            if (audioHost.AsioDriver == null)
                ShowAudioSettings();

            audioHost.Plugin.ShowEditor(IntPtr.Zero);
            audioHost.Exit();

            notifyIcon.Visible = false;
        }

        void ShowAudioSettings()
        {
            if (!settingsForm.Visible)
            {
                if (settingsForm.ShowDialog() == DialogResult.OK)
                {
                    audioHost.SetAsioDriver(settingsForm.AsioDeviceName);
                }
            }
        }

        void NotifyIcon_Click(object sender, EventArgs e)
        {
            ShowAudioSettings();
        }
    }
}
