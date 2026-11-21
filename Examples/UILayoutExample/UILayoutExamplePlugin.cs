using System;
using System.Threading;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using UILayout;

namespace UILayoutExample
{
    /// <summary>
    /// Stereo gain and mute with an embedded UILayout editor showing AudioPlugSharpUILayout's controls: the gain as a
    /// knob, a vertical slider and a horizontal slider, the mute as a switch (each control sends edit gestures to the
    /// host and follows the parameter's edit value, so the gain controls follow each other and host automation), the
    /// gain's value as text, and an output level meter fed from the audio thread.
    /// </summary>
    public class UILayoutExamplePlugin : AudioPluginUILayout
    {
        FloatAudioIOPort input;
        FloatAudioIOPort output;
        AudioPluginParameter gain;
        AudioPluginParameter mute;

        // Output peak, written by the audio thread (as float bits) and read by the editor
        int peakBits;

        ParameterKnob knob;
        ParameterSlider verticalSlider;
        ParameterSlider slider;
        ParameterSwitch muteSwitch;
        ParameterValueText gainText;
        LevelMeter meter;

        public UILayoutExamplePlugin()
        {
            Company = "AudioPlugSharp";
            Website = "github.com/mikeoliphant/AudioPlugSharp";
            Contact = "";
            PluginName = "UILayout Gain";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0x4A1D3C2B9E8F7061;

            SampleFormatsSupported = EAudioBitsPerSample.Bits32;

            EditorWidth = 320;
            EditorHeight = 176;
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { input = new FloatAudioIOPort("Stereo Input", EAudioChannelConfiguration.Stereo) };
            OutputPorts = new AudioIOPort[] { output = new FloatAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            AddParameter(gain = new DecibelParameter
            {
                ID = "gain",
                Name = "Gain",
                MaxValue = 12,
                ValueFormat = "{0:0.0}dB",
            });

            // On or off: on at a normalized value of at least 0.5 (ParameterSwitch sets 1 or 0)
            AddParameter(mute = new AudioPluginParameter
            {
                ID = "mute",
                Name = "Mute",
                MinValue = 0,
                MaxValue = 1,
                DefaultValue = 0,
                ValueFormat = "{0:0}",
            });

            // MIDI volume (CC 7) controls the gain
            AddMidiControllerMapping(gain, 7);
        }

        // ---- audio thread ----

        public override void Process()
        {
            base.Process();

            // Sample-accurate gain: ramp between the host's change points (one value per block would step at every
            // block boundary while the control moves - audible as crackle)
            ReadOnlySpan<float> inLeft = input.GetAudioBuffer(0);
            ReadOnlySpan<float> inRight = input.GetAudioBuffer(1);
            Span<float> outLeft = output.GetAudioBuffer(0);
            Span<float> outRight = output.GetAudioBuffer(1);

            float linearGain = (float)AudioPluginParameter.DBToLinear(gain.ProcessValue);
            float muteGain = (mute.ProcessValue >= 0.5) ? 0 : 1;
            float peak = 0;

            int currentSample = 0;
            int nextSample;

            do
            {
                nextSample = Host.ProcessEvents();

                bool ramp = gain.NeedInterpolationUpdate;

                for (int i = currentSample; i < nextSample; i++)
                {
                    if (ramp)
                        linearGain = (float)AudioPluginParameter.DBToLinear(gain.GetInterpolatedProcessValue(i));

                    float left = inLeft[i] * linearGain * muteGain;
                    float right = inRight[i] * linearGain * muteGain;

                    outLeft[i] = left;
                    outRight[i] = right;

                    peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                }

                currentSample = nextSample;
            }
            while (nextSample < inLeft.Length);

            // Keep the highest peak until the editor takes it
            int bits = BitConverter.SingleToInt32Bits(peak);
            int seen;

            while ((peak > BitConverter.Int32BitsToSingle(seen = Volatile.Read(ref peakBits))) &&
                (Interlocked.CompareExchange(ref peakBits, bits, seen) != seen))
            {
            }
        }

        // ---- editor (UI thread) ----

        protected override UIElement CreateEditorUI()
        {
            // Colors from the layout's theme, as the AudioPlugSharpUILayout controls take theirs
            UITheme theme = Layout.Current.Theme;

            VerticalStack root = new VerticalStack
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                BackgroundColor = theme.Background,
                Padding = new LayoutPadding(16),
                ChildSpacing = 12,
            };

            // Fixed row heights: header 26 (the font), controls 72 (the vertical slider's length), meter 16
            Dock header = new Dock { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = 26 };
            header.Children.Add(new TextBlock("Gain") { TextColor = theme.Text, HorizontalAlignment = EHorizontalAlignment.Left });
            // As wide as the widest value it can show, with the text right-aligned in it: the header's layout doesn't change
            header.Children.Add(gainText = new ParameterValueText(gain) { HorizontalAlignment = EHorizontalAlignment.Right });
            root.Children.Add(header);

            HorizontalStack controls = new HorizontalStack
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                DesiredHeight = 72,
                ChildSpacing = 12,
            };
            root.Children.Add(controls);

            controls.Children.Add(knob = new ParameterKnob(gain));
            controls.Children.Add(verticalSlider = new ParameterSlider(gain, vertical: true));
            controls.Children.Add(slider = new ParameterSlider(gain));

            // The mute switch, labelled
            VerticalStack muteStack = new VerticalStack
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                VerticalAlignment = EVerticalAlignment.Center,
            };
            muteStack.Children.Add(muteSwitch = new ParameterSwitch(mute));
            muteStack.Children.Add(new TextBlock("Mute")
            {
                TextColor = theme.MutedText,
                HorizontalAlignment = EHorizontalAlignment.Center,
            });
            controls.Children.Add(muteStack);

            root.Children.Add(meter = new LevelMeter
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                DesiredHeight = 16,
            });

            SyncControls();

            return root;
        }

        protected override void EditorUpdate(float secondsElapsed)
        {
            SyncControls();

            float peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peakBits, 0));

            // Rise immediately, fall with a quarter-second time constant
            if (meter != null)
                meter.Level = Math.Max(meter.Level * MathF.Exp(-secondsElapsed / 0.25f), peak);
        }

        protected override void EditorClosing()
        {
            knob = null;
            verticalSlider = null;
            slider = null;
            muteSwitch = null;
            gainText = null;
            meter = null;
        }

        /// <summary>
        /// Host -> editor: the controls follow their parameters' edit values (the host may automate them), and show
        /// hover
        /// </summary>
        void SyncControls()
        {
            if (knob == null)
                return;

            knob.Update();
            verticalSlider.Update();
            slider.Update();
            muteSwitch.Update();
            gainText.Update();
        }
    }
}
