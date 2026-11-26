using System;
using System.Threading;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
using UILayout;

namespace UILayoutTestPlugin
{
    /// <summary>
    /// The test hosts' UILayout plugin (ClapTestHost, Vst3TestHost run their detailed editor checks on it): stereo
    /// gain with a gain slider (AudioPlugSharpUILayout's ParameterControl with a ValueSlider: editor -> host as an edit
    /// gesture, host -> editor by following the parameter's edit value), an output level meter (AudioPlugSharpUILayout's LevelMeter) fed from the audio thread, and MIDI CC 7
    /// mapped to the gain.
    ///
    /// Beyond the UILayoutExample it starts from, it exercises what the hosts check: the editor is resizable (resize
    /// and DPI checks), Alt+Enter or F11 toggles fullscreen (editor window services, IAudioHost), and while
    /// fullscreen the mouse cursor hides after a second without movement. A "0 dB" button (an input-style
    /// TextButton) shows hover, for checking mouse-leave by hand. The editor claims only the keys it maps
    /// (AudioPluginUILayout.EditorWantsKey), so the others, such as the space bar, stay host shortcuts.
    ///
    /// The hosts look for the layout as it is (slider row, meter colours, sizes): change both together.
    /// </summary>
    public class UILayoutTestPlugin : AudioPluginUILayout
    {
        FloatAudioIOPort input;
        FloatAudioIOPort output;
        AudioPluginParameter gain;

        // Output peak, written by the audio thread (as float bits) and read by the editor
        int peakBits;

        ParameterControl slider;
        ParameterValueText gainText;
        LevelMeter meter;

        // Fullscreen cursor hiding
        const float CursorHideSeconds = 1;
        float mouseStillSeconds;
        float lastMouseX = float.NaN, lastMouseY = float.NaN;
        bool cursorHidden;

        public UILayoutTestPlugin()
        {
            Company = "AudioPlugSharp";
            Website = "github.com/mikeoliphant/AudioPlugSharp";
            Contact = "";
            PluginName = "UILayout Test";
            PluginCategory = "Fx";
            PluginVersion = "1.0.0";

            // Unique 64bit ID for the plugin
            PluginID = 0x847DD79E40ABF22B;

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

            // A list of choices (unused by the DSP): the test hosts check that it reaches them as stepped, with names
            AddParameter(new ChoiceParameter
            {
                ID = "mode",
                Name = "Mode",
                Choices = new[] { "Clean", "Crunch", "Lead" },
            });
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

        public override bool CanResizeEditor
        {
            get { return true; }
        }

        protected override void EditorOpened()
        {
            EditorLayout.InputManager.AddMapping("ToggleFullscreen", new KeyMapping(InputKey.Enter) { Modifier = InputKey.LeftAlt });
            EditorLayout.InputManager.AddMapping("ToggleFullscreen", new KeyMapping(InputKey.Enter) { Modifier = InputKey.RightAlt });
            EditorLayout.InputManager.AddMapping("ToggleFullscreen", new KeyMapping(InputKey.F11));

            mouseStillSeconds = 0;
            lastMouseX = lastMouseY = float.NaN;
            cursorHidden = false;
        }

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

            // Hover: the border takes the accent color while the mouse is over it, and drops it when the mouse
            // leaves the editor window
            header.Children.Add(new TextButton("0 dB", ResetGain)
            {
                HorizontalAlignment = EHorizontalAlignment.Center,
                DesiredHeight = 26,
            });

            root.Children.Add(header);

            // The hosts find the slider by its black track (a row that is mostly black), so its track and fill are black
            root.Children.Add(slider = new ParameterControl(gain, new ValueSlider
            {
                TrackColor = UIColor.Black,
                FillColor = UIColor.Black,
            })
            {
                HorizontalAlignment = EHorizontalAlignment.Stretch,
                DesiredHeight = 32,
                ShowName = false,
            });

            // AudioPlugSharpUILayout's meter (the standalone's too)
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

            UpdateWindowServices(secondsElapsed);
        }

        /// <summary>
        /// Alt+Enter toggles fullscreen; while fullscreen the cursor hides when the mouse is still. These calls may
        /// come from the editor's render thread: the host applies them on its UI thread.
        /// </summary>
        void UpdateWindowServices(float secondsElapsed)
        {
            MonoGameLayout layout = EditorLayout;

            if (layout == null)
                return;

            InputManager input = layout.InputManager;

            if (input.WasPressed("ToggleFullscreen"))
                Host.SetEditorFullscreen(!Host.IsEditorFullscreen);

            bool moved = (input.MousePosition.X != lastMouseX) || (input.MousePosition.Y != lastMouseY);

            lastMouseX = input.MousePosition.X;
            lastMouseY = input.MousePosition.Y;

            mouseStillSeconds = moved ? 0 : (mouseStillSeconds + secondsElapsed);

            bool hide = Host.IsEditorFullscreen && (mouseStillSeconds >= CursorHideSeconds);

            if (hide != cursorHidden)
            {
                cursorHidden = hide;

                Host.SetEditorCursorVisible(!hide);
            }
        }

        protected override void EditorClosing()
        {
            slider = null;
            gainText = null;
            meter = null;
        }

        /// <summary>
        /// Editor -> host: the gain to its default (0 dB) as one edit gesture
        /// </summary>
        void ResetGain()
        {
            gain.BeginEdit();

            gain.EditValue = gain.DefaultValue;

            gain.EndEdit();
        }

        /// <summary>
        /// Host -> editor: follow the parameter's edit value (the host may automate it)
        /// </summary>
        void SyncControls()
        {
            if ((slider == null) || (gainText == null))
                return;

            slider.Update();
            gainText.Update();
        }
    }
}
