using System;
using System.Threading;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using UILayout;

namespace UILayoutExample
{
    /// <summary>
    /// Stereo gain with an embedded UILayout editor: a gain slider (editor -> host as an edit gesture, host ->
    /// editor by following the parameter's edit value) and an output level meter fed from the audio thread.
    /// </summary>
    public class UILayoutExamplePlugin : AudioPluginUILayout
    {
        FloatAudioIOPort input;
        FloatAudioIOPort output;
        AudioPluginParameter gain;

        // Output peak, written by the audio thread (as float bits) and read by the editor
        int peakBits;

        GainSlider slider;
        TextBlock gainText;
        LevelMeter meter;
        double shownGainDb = double.NaN;

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
            header.Children.Add(gainText = new TextBlock("") { TextColor = UIColor.White, HorizontalAlignment = EHorizontalAlignment.Right });
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

            shownGainDb = double.NaN;
            SyncControls();

            return root;
        }

        protected override void EditorUpdate(float secondsElapsed)
        {
            SyncControls();

            float peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref peakBits, 0));

            meter?.Update(peak, secondsElapsed);
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

            double db = Math.Round(gain.EditValue, 1);

            // Only update the text when the shown value changes
            if (db != shownGainDb)
            {
                shownGainDb = db;

                gainText.Text = gain.FormatValue(db);
                gainText.UpdateContentLayout();
            }
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

        /// <summary>
        /// A horizontal bar: the output peak (0 dB at the right end), held and decaying
        /// </summary>
        class LevelMeter : UIElement
        {
            static readonly UIColor Background = new UIColor(16, 18, 22);
            static readonly UIColor Green = new UIColor(64, 200, 96);
            static readonly UIColor Red = new UIColor(230, 64, 48);

            float level; // 0..1, -60 dB .. 0 dB

            public void Update(float peak, float secondsElapsed)
            {
                float db = (peak > 0) ? (20 * MathF.Log10(peak)) : -60;
                float target = Math.Clamp((db + 60) / 60, 0, 1);

                // Rise immediately, fall in about 1.5 seconds
                level = (target >= level) ? target : Math.Max(target, level - (secondsElapsed * 1.5f));
            }

            protected override void DrawContents()
            {
                GraphicsContext2D g = Layout.Current.GraphicsContext;
                RectF bounds = ContentBounds;

                g.DrawRectangle(bounds, Background);

                if (level > 0)
                    g.DrawRectangle(new RectF(bounds.X, bounds.Y, bounds.Width * level, bounds.Height), (level >= 1) ? Red : Green);
            }
        }
    }
}
