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
            return String.Format(ValueFormat, value);
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

            return (RangePower > 0) ? Math.Pow(rangeVal, RangePower) : rangeVal;
        }

        public virtual double GetValueDenormalized(double value)
        {
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

            if (needInterpolationUpdate)
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
}
