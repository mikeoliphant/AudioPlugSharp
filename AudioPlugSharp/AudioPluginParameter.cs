using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

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
        public bool IsEditing { get; private set; }

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

        double prevParamValue;
        double slope;
        int prevParamChangeSample;
        int nextParamChangeSample;
        bool needInterpolationUpdate = false;

        // Interpolation of the normalized value, used when a plain value is infinite (a DecibelParameter's -inf dB
        // minimum), where interpolating plain values gives NaN (-inf + k * inf) or an instant jump.
        bool interpolateNormalized;
        double prevNormalizedValue;
        double normalizedSlope;

        public void AddParameterChangePoint(double newNormalizedValue, int sampleOffset)
        {
            prevParamChangeSample = nextParamChangeSample;
            prevParamValue = processValue;

            nextParamChangeSample = sampleOffset;
            NormalizedProcessValue = newNormalizedValue;

            // No need to update if the parameter isn't changing
            needInterpolationUpdate = processValue != prevParamValue;
            interpolateNormalized = false;

            // A stepped parameter switches at the change point (GetInterpolatedProcessValue): no slope
            if (needInterpolationUpdate && !IsStepped)
            {
                double samples = nextParamChangeSample - prevParamChangeSample;

                slope = (processValue - prevParamValue) / samples;

                if (!double.IsFinite(slope))
                {
                    interpolateNormalized = true;
                    prevNormalizedValue = GetValueNormalized(prevParamValue);
                    normalizedSlope = (newNormalizedValue - prevNormalizedValue) / samples;
                }
            }
        }

        public void ResetParameterChange()
        {
            prevParamValue = processValue;
            prevParamChangeSample = -1;
            nextParamChangeSample = -1;
            needInterpolationUpdate = false;
        }

        public bool NeedInterpolationUpdate
        {
            get { return needInterpolationUpdate; }
        }

        public double GetInterpolatedProcessValue(int sampleOffset)
        {
            if (!needInterpolationUpdate)
                return prevParamValue;

            // Past the last control point, the value stays at that point's value
            if (sampleOffset > nextParamChangeSample)
            {
                needInterpolationUpdate = false;
                prevParamValue = processValue;

                return processValue;
            }

            // Stepped: the previous value until the change point, then the new one (values between steps don't exist)
            if (IsStepped)
                return (sampleOffset < nextParamChangeSample) ? prevParamValue : processValue;

            if (interpolateNormalized)
                return GetValueDenormalized(prevNormalizedValue + ((double)(sampleOffset - prevParamChangeSample) * normalizedSlope));

            return prevParamValue + ((double)(sampleOffset - prevParamChangeSample) * slope);
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
