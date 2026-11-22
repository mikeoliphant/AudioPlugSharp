using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AudioPlugSharp
{
    public class AudioPluginParameter : INotifyPropertyChanged
    {
        public IAudioPluginEditor Editor { get; internal set; } 

        public event PropertyChangedEventHandler PropertyChanged;

        public int ParameterIndex { get; internal set; }
        public string ID { get; set; }
        public string Name { get; set; }
        public virtual double MinValue { get; set; }
        public virtual double MaxValue { get; set; }
        public double RangePower { get; set; } = 0;
        public double DefaultValue { get; set; }

        /// <summary>
        /// The number of steps between MinValue and MaxValue: 0 (the default) for a continuous parameter, 1 for on/off,
        /// N for N + 1 values spaced evenly from MinValue to MaxValue. A stepped parameter maps linearly (RangePower is
        /// ignored), its values snap to the steps, and it changes at its change points in Process instead of ramping.
        /// Hosts and generated editors show it as stepped (a switch for on/off).
        /// </summary>
        public int StepCount { get; set; }

        /// <summary>
        /// Names for a stepped parameter's values, from MinValue up (StepCount + 1 of them), shown instead of
        /// numbers. Hosts and generated editors show the parameter as a list of choices. Null (the default): no
        /// names. See <see cref="ChoiceParameter"/>.
        /// </summary>
        public string[] ValueNames { get; set; }

        /// <summary>Whether the parameter has steps (StepCount above 0).</summary>
        public bool IsStepped
        {
            get { return StepCount > 0; }
        }

        /// <summary>
        /// The step (0 to StepCount) of a normalized value: the 0-1 range in StepCount + 1 equal parts (as VST3
        /// divides it), so an on/off parameter is on from 0.5.
        /// </summary>
        public int GetStep(double normalizedValue)
        {
            return (int)Math.Min(StepCount, Math.Floor(Math.Clamp(normalizedValue, 0, 1) * (StepCount + 1)));
        }
        public string ValueFormat
        {
            get { return valueFormat; }

            set
            {
                valueFormat = value;
                valueFormatParsed = false;
                compositeValueFormat = null;
            }
        }

        string valueFormat;

        // ValueFormat split once (for AppendValue) into the text before and after its one number and the number's
        // format ("{0:0.0}dB": "", "0.0", "dB"), so the number is formatted into a buffer on the stack. Other shapes
        // (several or aligned items, escaped braces) use CompositeFormat, which can box the value.
        bool valueFormatParsed;
        bool simpleValueFormat;
        string valueFormatPrefix;
        string valueFormatNumber;
        string valueFormatSuffix;
        CompositeFormat compositeValueFormat;

        double processValue;
        double editValue;

        public double ProcessValue
        {
            get { return processValue; }
            set
            {
                if (this.processValue != value)
                {
                    this.processValue = value;
                }
            }
        }

        /// <summary>
        /// The value shown and edited by UI controls (plain units). Setting it reports the edit to the host: as one
        /// value inside a BeginEdit()/EndEdit() gesture, or as a gesture of its own outside one. Set it on the UI
        /// thread.
        /// </summary>
        public double EditValue
        {
            get
            {
                return editValue;
            }
            set
            {
                SetEditValue(value, reportToHost: true);
            }
        }

        public virtual string DisplayValue { get { return FormatValue(editValue); } }

        /// <summary>
        /// Formats a (plain, not normalized) parameter value for display
        /// </summary>
        public virtual string FormatValue(double value)
        {
            string name = GetValueName(value);

            if (name != null)
                return name;

            return String.Format(ValueFormat, value);
        }

        // The name of a (plain) value's step, or null without names
        string GetValueName(double value)
        {
            if ((ValueNames == null) || !IsStepped || (MaxValue == MinValue))
                return null;

            int step = (int)Math.Round(Math.Clamp((value - MinValue) / (MaxValue - MinValue), 0, 1) * StepCount);

            return (step < ValueNames.Length) ? ValueNames[step] : null;
        }

        /// <summary>
        /// Formats a value into text (replacing what it holds), without allocating once the builder has grown: for
        /// controls that show the value while it changes. A parameter that overrides <see cref="FormatValue(double)"/>
        /// (and not <see cref="AppendValue"/>) gets that text, which allocates.
        /// </summary>
        public void FormatValue(double value, StringBuilder text)
        {
            text.Clear();

            if (UsesStringFormat)
                text.Append(FormatValue(value));
            else
                AppendValue(value, text);
        }

        /// <summary>
        /// Appends a formatted value (as <see cref="FormatValue(double)"/> formats it) without allocating. Override it
        /// along with FormatValue(double) for text of your own.
        /// </summary>
        protected virtual void AppendValue(double value, StringBuilder text)
        {
            string name = GetValueName(value);

            if (name != null)
            {
                text.Append(name);

                return;
            }

            if (!valueFormatParsed)
                ParseValueFormat();

            if (simpleValueFormat)
            {
                Span<char> number = stackalloc char[64];

                if (value.TryFormat(number, out int length, valueFormatNumber))
                {
                    text.Append(valueFormatPrefix).Append(number.Slice(0, length)).Append(valueFormatSuffix);

                    return;
                }
            }

            if (compositeValueFormat == null)
                compositeValueFormat = CompositeFormat.Parse(ValueFormat);

            text.AppendFormat(null, compositeValueFormat, value);
        }

        void ParseValueFormat()
        {
            valueFormatParsed = true;
            simpleValueFormat = false;

            string format = ValueFormat;

            if (format == null)
                return;

            int open = format.IndexOf('{');
            int close = format.IndexOf('}');

            // One item, "{0}" or "{0:number format}", and no other braces.
            if ((open < 0) || (close < open))
                return;

            if ((format.IndexOf('{', open + 1) >= 0) || (format.IndexOf('}', close + 1) >= 0))
                return;

            string item = format.Substring(open + 1, close - open - 1);

            if ((item != "0") && !item.StartsWith("0:"))
                return;

            valueFormatPrefix = format.Substring(0, open);
            valueFormatNumber = (item == "0") ? null : item.Substring(2);
            valueFormatSuffix = format.Substring(close + 1);
            simpleValueFormat = true;
        }

        // Whether FormatValue(double) is overridden in a class below the one that overrides AppendValue: then the
        // StringBuilder formatting uses its text. Found once.
        bool UsesStringFormat
        {
            get
            {
                if (usesStringFormat == null)
                {
                    Type type = GetType();
                    Type stringFormatter = type.GetMethod(nameof(FormatValue), new[] { typeof(double) }).DeclaringType;
                    Type appender = type.GetMethod(nameof(AppendValue), System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).DeclaringType;

                    usesStringFormat = stringFormatter.IsSubclassOf(appender);
                }

                return usesStringFormat.Value;
            }
        }

        bool? usesStringFormat;

        public double NormalizedProcessValue
        {
            get { return GetValueNormalized(processValue); }
            set { processValue = GetValueDenormalized(value); }
        }

        /// <summary>
        /// The edit value, normalized (0-1). Setting it reports the edit to the host, like EditValue. UI controls
        /// that work in 0-1 (knobs, sliders) bind to this one: it has no infinite ends (a DecibelParameter's -inf dB
        /// minimum is 0) and follows RangePower.
        /// </summary>
        public double NormalizedEditValue
        {
            get { return GetValueNormalized(editValue); }
            set { SetEditValue(GetValueDenormalized(value), reportToHost: true); }
        }

        /// <summary>
        /// The default value, normalized (0-1).
        /// </summary>
        public double NormalizedDefaultValue
        {
            get { return GetValueNormalized(DefaultValue); }
        }

        /// <summary>
        /// True between BeginEdit() and EndEdit().
        /// </summary>
        public bool IsEditing
        {
            get { return isEditing; }

            private set
            {
                isEditing = value;

                MarkEdited();
            }
        }

        volatile bool isEditing;

        // When the editor last changed the value or began or ended a gesture (Stopwatch ticks; 0: never). The audio
        // thread smooths change points while the user edits and for a moment after: a wheel notch or a double-click is
        // a whole gesture on the UI thread, over before its change point reaches the audio thread.
        long lastEdited;

        static readonly long EditedWindow = Stopwatch.Frequency / 4;

        void MarkEdited() => Volatile.Write(ref lastEdited, Stopwatch.GetTimestamp());

        // Whether the user is editing the parameter from the editor, or just did (the audio thread)
        bool BeingEdited
        {
            get
            {
                if (isEditing)
                    return true;

                long edited = Volatile.Read(ref lastEdited);

                return (edited != 0) && ((Stopwatch.GetTimestamp() - edited) < EditedWindow);
            }
        }

        /// <summary>
        /// Starts an edit gesture (for example on mouse down on a knob). The edits until EndEdit() reach the host as
        /// one gesture, so host automation in touch or latch mode follows the control while it is held. Call it on
        /// the UI thread; a second call before EndEdit() is ignored.
        /// </summary>
        public void BeginEdit()
        {
            if (IsEditing)
                return;

            IsEditing = true;

            Editor?.Host?.BeginEdit(ParameterIndex);
        }

        /// <summary>
        /// Ends the edit gesture started by BeginEdit() (for example on mouse up).
        /// </summary>
        public void EndEdit()
        {
            if (!IsEditing)
                return;

            IsEditing = false;

            Editor?.Host?.EndEdit(ParameterIndex);
        }

        /// <summary>
        /// For hosts and format adapters: a value from the host (automation, the host's parameter UI, a preset).
        /// Updates the edit value and bound UI without reporting an edit back to the host. Call it on the UI thread.
        /// </summary>
        public void SetNormalizedValueFromHost(double normalizedValue)
        {
            SetEditValue(GetValueDenormalized(normalizedValue), reportToHost: false);
        }

        internal void SetEditValue(double value, bool reportToHost)
        {
            if (editValue == value)
                return;

            editValue = value;

            if (reportToHost && (Editor?.Host != null)) // Editor is null before the parameter has been added
            {
                IAudioHost host = Editor.Host;

                MarkEdited();

                if (IsEditing)
                {
                    host.PerformEdit(ParameterIndex, GetValueNormalized(value));
                }
                else
                {
                    host.BeginEdit(ParameterIndex);
                    host.PerformEdit(ParameterIndex, GetValueNormalized(value));
                    host.EndEdit(ParameterIndex);
                }
            }

            OnPropertyChanged("EditValue");
            OnPropertyChanged("NormalizedEditValue");
            OnPropertyChanged("DisplayValue");
        }

        public AudioPluginParameter()
        {
            MinValue = 0;
            MaxValue = 1;
            DefaultValue = 0.5;
            ValueFormat = "{0:0.0}";
        }

        public static double DBToLinear(double db)
        {
            return Math.Pow(10.0, 0.05 * db);
        }

        public static double LinearToDB(double linear)
        {
            return 20.0 * Math.Log10(linear);
        }

        public virtual double GetValueNormalized(double value)
        {
            double rangeVal = (value - MinValue) / (MaxValue - MinValue);

            if (IsStepped)
                return Math.Round(Math.Clamp(rangeVal, 0, 1) * StepCount) / StepCount;

            return (RangePower > 0) ? Math.Pow(rangeVal, RangePower) : rangeVal;
        }

        public virtual double GetValueDenormalized(double value)
        {
            if (IsStepped)
                return MinValue + ((MaxValue - MinValue) * GetStep(value) / StepCount);

            double rangeVal = (RangePower > 0) ? Math.Pow(value, 1 / RangePower) : value;

            return MinValue + ((MaxValue - MinValue) * rangeVal);
        }

        // ---- Interpolation (the audio thread) ------------------------------------------------------------------------
        //
        // A change point ramps the value used in processing from where it is to the new value, reaching it exactly on
        // the point's sample: host automation is sample-accurate. While the user edits the parameter from the plugin's
        // own editor (a drag, a wheel turn, a double-click; and a moment after), points glide over SmoothingTime
        // instead: the editor sends one edit per frame, which would otherwise jump within one block and hold until the
        // next (heard as stepping). Times
        // are samples of the parameter's own clock, which the host advances at every block's end
        // (ResetParameterChange(int)), so a glide carries on across blocks.

        long clock;                 // The current block's first sample
        long lastPointTime = -1;    // The latest change point's sample (where the next ramp can start)
        long rampStart, rampEnd;    // The ramp's samples (the value is rampFrom up to rampStart, rampTo from rampEnd)
        double rampFrom, rampTo;    // Plain values, or normalized ones for a ramp with an infinite end (-inf dB)
        bool rampNormalized;
        bool ramping;

        // A stepped parameter's switch, in the current block: the value before it, and its sample
        double steppedFrom;
        long switchTime;

        bool needInterpolationUpdate = false;

        /// <summary>
        /// How long the value takes to glide to a new value while the user edits it from the plugin's editor, in
        /// seconds (default 0.02), so a control's edits (one per editor frame) are smooth. Host automation isn't
        /// smoothed: it stays sample-accurate. 0: edits ramp within their block, like automation.
        /// </summary>
        public double SmoothingTime { get; set; } = 0.02;

        /// <summary>
        /// For hosts: a change point at a sample of the current block (from the host, or an edit), which the value used
        /// in processing ramps to (or, for a stepped parameter, switches to there). <see cref="ProcessValue"/> is the new
        /// value at once; <see cref="GetInterpolatedProcessValue"/> gives the ramp.
        /// </summary>
        public void AddParameterChangePoint(double newNormalizedValue, int sampleOffset)
        {
            long time = clock + sampleOffset;

            // The ramp starts where the value is now: at the previous point, or this block's start if that was earlier
            // (the samples before it are processed already)
            long start = Math.Max(lastPointTime, clock - 1);
            double from = ValueAt(start);

            lastPointTime = time;

            NormalizedProcessValue = newNormalizedValue;

            if (IsStepped)
            {
                ramping = false;
                steppedFrom = from;
                switchTime = time;
                needInterpolationUpdate = from != processValue;

                return;
            }

            if (from == processValue)
            {
                // Not changing: whatever ramp there was ends here
                ramping = false;
                needInterpolationUpdate = false;

                return;
            }

            // On the point's sample; while the user edits, no sooner than SmoothingTime from where it starts
            long smoothing = BeingEdited ? (long)(SmoothingTime * SampleRateForRamps) : 0;

            rampStart = start;
            rampEnd = Math.Max(time, start + smoothing);
            rampNormalized = !double.IsFinite(from) || !double.IsFinite(processValue);
            rampFrom = rampNormalized ? GetValueNormalized(from) : from;
            rampTo = rampNormalized ? newNormalizedValue : processValue;
            ramping = true;
            needInterpolationUpdate = true;
        }

        // The host's rate, for SmoothingTime (48 kHz before the parameter is added)
        double SampleRateForRamps
        {
            get
            {
                double rate = Editor?.Host?.SampleRate ?? 0;

                return (rate > 0) ? rate : 48000;
            }
        }

        /// <summary>
        /// For hosts, at the end of every block: moves the parameter's clock on by the block's samples, carrying an
        /// unfinished ramp into the next block.
        /// </summary>
        public void ResetParameterChange(int blockFrames)
        {
            clock += blockFrames;

            if (IsStepped || !ramping || (rampEnd < clock))
            {
                ramping = false;
                needInterpolationUpdate = false;
            }
        }

        /// <summary>
        /// Ends the parameter's interpolation at a block's end, for hosts that don't say how long their blocks are: each
        /// block's points ramp within that block only (see <see cref="ResetParameterChange(int)"/>).
        /// </summary>
        public void ResetParameterChange()
        {
            clock = 0;
            lastPointTime = -1;
            ramping = false;
            needInterpolationUpdate = false;
        }

        /// <summary>Whether the value used in processing is changing in this block: then read it per sample with
        /// <see cref="GetInterpolatedProcessValue"/>.</summary>
        public bool NeedInterpolationUpdate
        {
            get { return needInterpolationUpdate; }
        }

        /// <summary>The value used in processing at a sample of the current block, ramping between change points.</summary>
        public double GetInterpolatedProcessValue(int sampleOffset)
        {
            long time = clock + sampleOffset;

            // Stepped: the previous value until the change point, then the new one (values between steps don't exist)
            if (IsStepped)
                return (needInterpolationUpdate && (time < switchTime)) ? steppedFrom : processValue;

            return ValueAt(time);
        }

        // The value used in processing at a sample of the parameter's clock
        double ValueAt(long time)
        {
            if (!ramping || (time >= rampEnd))
                return processValue;

            if (time <= rampStart)
                return rampNormalized ? GetValueDenormalized(rampFrom) : rampFrom;

            double value = rampFrom + ((rampTo - rampFrom) * (time - rampStart) / (rampEnd - rampStart));

            return rampNormalized ? GetValueDenormalized(value) : value;
        }

        public void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public class DecibelParameter : AudioPluginParameter
    {
        public override double MinValue
        {
            get => base.MinValue;
            set
            {
                base.MinValue = value;

                linearMin = DBToLinear(value);
            }
        }

        public override double MaxValue
        {
            get => base.MaxValue;

            set
            {
                base.MaxValue = value;

                linearMax = DBToLinear(value);
            }
        }

        double linearMin;
        double linearMax;

        public DecibelParameter()
        {
            MinValue = double.NegativeInfinity;
            MaxValue = 0;
            DefaultValue = 0;
            RangePower = 3.0;
            ValueFormat = "{0:0.0}dB";
        }

        protected override void AppendValue(double value, StringBuilder text)
        {
            if (double.IsNegativeInfinity(value))
                text.Append("-inf dB");
            else
                base.AppendValue(value, text);
        }

        public override string FormatValue(double value)
        {
            if (double.IsNegativeInfinity(value))
                return "-inf dB";

            return base.FormatValue(value);
        }

        public static double LinearToLog(double linear)
        {
            return Math.Pow(linear, .25);
        }

        public static double LogToLinear(double log)
        {
            return Math.Pow(log, 4);
        }

        public override double GetValueNormalized(double value)
        {
            double rangeVal = (DBToLinear(value) - linearMin) / (linearMax - linearMin);

            return (RangePower > 0) ? Math.Pow(rangeVal, 1 / RangePower) : rangeVal;
        }

        public override double GetValueDenormalized(double value)
        {
            double rangeVal = (RangePower > 0) ? Math.Pow(value, RangePower) : value;

            return LinearToDB(linearMin + ((linearMax - linearMin) * rangeVal));
        }
    }

    /// <summary>
    /// An on/off parameter (0 or 1, off by default), shown as "Off" and "On". Hosts and generated editors show it as
    /// a switch.
    /// </summary>
    public class BoolParameter : AudioPluginParameter
    {
        public BoolParameter()
        {
            MinValue = 0;
            MaxValue = 1;
            DefaultValue = 0;
            StepCount = 1;
            ValueNames = new[] { "Off", "On" };
        }

        /// <summary>Whether the value used in processing is on.</summary>
        public bool IsOn
        {
            get { return ProcessValue >= 0.5; }
        }
    }

    /// <summary>
    /// A parameter that is one of a list of choices: its value is the choice's index (0 to the number of choices - 1;
    /// the first by default), shown as the choice's name. Hosts and generated editors show it as a list.
    /// </summary>
    public class ChoiceParameter : AudioPluginParameter
    {
        public ChoiceParameter()
        {
            DefaultValue = 0;
        }

        /// <summary>The choices' names. Setting them sets the range and steps.</summary>
        public string[] Choices
        {
            get { return ValueNames; }

            set
            {
                ValueNames = value;
                MinValue = 0;
                MaxValue = Math.Max(value.Length - 1, 1);
                StepCount = Math.Max(value.Length - 1, 1);
            }
        }

        /// <summary>The index of the choice used in processing.</summary>
        public int ChoiceIndex
        {
            get { return (int)Math.Round(ProcessValue); }
        }
    }

    /// <summary>
    /// A list of choices from an enum's members, in the order of their values (declaration order for a plain enum).
    /// A member's name is its [Description] if it has one, otherwise its name with words split ("HardClip": "Hard
    /// Clip"). <see cref="Value"/> gives the member used in processing, without allocating. Saved state holds the
    /// choice's position, so add members at the end: reordering them, or inserting one, changes what saved projects
    /// load.
    /// </summary>
    public class EnumParameter<TEnum> : ChoiceParameter where TEnum : struct, Enum
    {
        static readonly TEnum[] values = Enum.GetValues<TEnum>();
        static readonly string[] names = GetNames();

        public EnumParameter()
        {
            Choices = names;
        }

        /// <summary>The member used in processing.</summary>
        public TEnum Value
        {
            get { return values[Math.Clamp(ChoiceIndex, 0, values.Length - 1)]; }
        }

        /// <summary>The default member (the first by default).</summary>
        public TEnum DefaultChoice
        {
            get { return values[Math.Clamp((int)Math.Round(DefaultValue), 0, values.Length - 1)]; }

            set { DefaultValue = Math.Max(Array.IndexOf(values, value), 0); }
        }

        static string[] GetNames()
        {
            string[] memberNames = Enum.GetNames<TEnum>();
            string[] shown = new string[memberNames.Length];

            for (int i = 0; i < memberNames.Length; i++)
            {
                DescriptionAttribute description = (DescriptionAttribute)Attribute.GetCustomAttribute(
                    typeof(TEnum).GetField(memberNames[i]), typeof(DescriptionAttribute));

                shown[i] = description?.Description ?? SplitWords(memberNames[i]);
            }

            return shown;
        }

        // "HardClip": "Hard Clip", "LFOSpeed": "LFO Speed", "Mode2": "Mode 2"
        static string SplitWords(string name)
        {
            StringBuilder text = new StringBuilder(name.Length + 4);

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                if (i > 0)
                {
                    char previous = name[i - 1];
                    bool nextIsLower = (i + 1 < name.Length) && char.IsLower(name[i + 1]);

                    bool wordStart = (char.IsUpper(c) && (char.IsLower(previous) || (char.IsUpper(previous) && nextIsLower))) ||
                        (char.IsDigit(c) && !char.IsDigit(previous));

                    if (wordStart && (previous != '_'))
                        text.Append(' ');
                }

                text.Append((c == '_') ? ' ' : c);
            }

            return text.ToString();
        }
    }
}
