using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AudioPlugSharp;

namespace AudioPlugSharpWPF;

public partial class PluginView : UserControl
{
    IAudioPluginEditor plugin;

    public string AsioDeviceName { get; set; }

    public PluginView()
    {
        InitializeComponent();
    }

    public void SetPlugin(IAudioPluginEditor plugin)
    {
        this.plugin = plugin;

        IntPtr windowHandle = new WindowInteropHelper(Application.Current.MainWindow).Handle;

        plugin.ShowEditor(windowHandle);

        plugin.ResizeEditor((uint)ActualWidth, (uint)ActualHeight);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (plugin != null)
            plugin.ResizeEditor((uint)ActualWidth, (uint)ActualHeight);
    }

    public bool ShowAudioSettingsWindow()
    {
        AudioSettingsWindow settingsWindow = new AudioSettingsWindow();

        settingsWindow.Owner = Application.Current.MainWindow;

        bool? dialogResult = settingsWindow.ShowDialog();

        switch (dialogResult)
        {
            case true:
                AsioDeviceName = settingsWindow.AsioDeviceName;

                return true;
                break;
        }

        return false;
    }
}
