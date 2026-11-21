using System;
using System.Threading;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using UILayout;

namespace UILayoutExample
{
    /// <summary>
    /// Stereo gain with an embedded UILayout editor: a gain slider (editor -> host as an edit gesture, host ->
    /// editor by following the parameter's edit value) and an output level meter (AudioPlugSharpUILayout's
    /// LevelMeter) fed from the audio thread.
    /// </summary>
    public class UILayoutExamplePlugin : AudioPluginUILayout
    {
        FloatAudioIOPort input;
        FloatAudioIOPort output;
        AudioPluginParameter gain;

        // Output peak, written by the audio thread (as float bits) and read by the editor
        int peakBits;

        GainSlider slider;
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
            EditorHeight = 160;
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

                    float left = inLeft[i] * linearGain;
                    float right = inRight[i] * linearGain;

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
            VerticalStack root = new VerticalStack
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                VerticalAlignment = EVerticalAlignment.Stretch,
                BackgroundColor = new UIColor(32, 36, 44),
                Padding = new LayoutPadding(16),
                ChildSpacing = 12,
            };

            // Fixed row heights: header 26 (the font), slider 32, meter 16
            Dock header = new Dock { HorizontalAlignment = EHorizontalAlignment.Stretch, DesiredHeight = 26 };
            header.Children.Add(new TextBlock("Gain") { TextColor = UIColor.White, HorizontalAlignment = EHorizontalAlignment.Left });
            // As wide as the widest value it can show, with the text right-aligned in it: the header's layout doesn't change
            header.Children.Add(gainText = new ParameterValueText(gain) { TextColor = UIColor.White, HorizontalAlignment = EHorizontalAlignment.Right });
            root.Children.Add(header);

            root.Children.Add(slider = new GainSlider(gain)
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                DesiredHeight = 32,
            });

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
            slider = null;
            gainText = null;
            meter = null;
        }

        /// <summary>
        /// Host -> editor: follow the parameter's edit value (the host may automate it)
        /// </summary>
        void SyncControls()
        {
            if ((slider == null) || (gainText == null))
                return;

            if (!slider.IsDragging)
                slider.SetLevel((float)gain.NormalizedEditValue);

            gainText.Update();
        }

        /// <summary>
        /// Editor -> host: one edit gesture per drag (begin on press, values while moving, end on release)
        /// </summary>
        class GainSlider : HorizontalSlider
        {
            AudioPluginParameter parameter;

            public bool IsDragging { get; private set; }

            public GainSlider(AudioPluginParameter parameter)
                : base("HorizontalSlider")
            {
                this.parameter = parameter;

                // A value inside the drag's gesture
                ChangeAction = delegate (float level)
                {
                    parameter.NormalizedEditValue = level;
                };
            }

            public override bool HandleTouch(in Touch touch)
            {
                if (touch.TouchState == ETouchState.Pressed)
                {
                    IsDragging = true;

                    parameter.BeginEdit();
                }

                bool handled = base.HandleTouch(touch);

                if ((touch.TouchState == ETouchState.Released) && IsDragging)
                {
                    IsDragging = false;

                    parameter.EndEdit();
                }

                return handled;
            }
        }
    }
}
