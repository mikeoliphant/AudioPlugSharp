using System;
using AudioPlugSharp;
using AudioPlugSharpWPF;

namespace WPFExample
{
    public class WPFExamplePlugin : AudioPluginBase
    {
        AudioIOPort monoInput;
        AudioIOPort stereoOutput;
        EditorWindow editorWindow;

        public WPFExamplePlugin()
        {
            Company = "My Company";
            Website = "www.mywebsite.com";
            Contact = "contact@my.email";
            PluginName = "WPF Example Plugin";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0x1E92758E710B4947;

            HasUserInterface = true;
            EditorWidth = 200;
            EditorHeight = 100;
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { monoInput = new AudioIOPort("Mono Input", EAudioChannelConfiguration.Mono) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new AudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            AddParameter(new AudioPluginParameter
            {
                ID = "gain",
                Name = "Gain",
                Type = EAudioPluginParameterType.Float,
                MinValue = -20,
                MaxValue = 20,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}dB"
            });

            AddParameter(new AudioPluginParameter
            {
                ID = "pan",
                Name = "Pan",
                Type = EAudioPluginParameterType.Float,
                MinValue = -1,
                MaxValue = 1,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}"
            });
        }

        public override void ResizeEditor(uint newWidth, uint newHeight)
        {
            base.ResizeEditor(newWidth, newHeight);

            if (editorWindow != null)
            {
                editorWindow.Width = EditorWidth;
                editorWindow.Height = EditorHeight;
            }
        }

        public override bool ShowEditor(IntPtr parentWindow)
        {
            editorWindow = new EditorWindow(this, new EditorView())
            {
                Width = EditorWidth,
                Height = EditorHeight
            };

            editorWindow.Show(parentWindow);

            return true;
        }

        public override void Process()
        {
            base.Process();

            double gain = GetParameter("gain").Value;
            double linearGain = Math.Pow(10.0, 0.05 * gain);

            double pan = GetParameter("pan").Value;

            monoInput.ReadData();

            double[] inSamples = monoInput.GetAudioBuffers()[0];

            double[] outLeftSamples = stereoOutput.GetAudioBuffers()[0];
            double[] outRightSamples = stereoOutput.GetAudioBuffers()[1];

            for (int i = 0; i < inSamples.Length; i++)
            {
                outLeftSamples[i] = inSamples[i] * linearGain * (1 - pan);
                outRightSamples[i] = inSamples[i] * linearGain * (1 + pan);
            }

            stereoOutput.WriteData();
        }
    }
}
