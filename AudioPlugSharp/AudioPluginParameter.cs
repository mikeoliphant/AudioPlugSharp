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
        public string ValueFormat { get; set; }

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

        // To be used by UI controls to access the value
        public double EditValue
        {
            get
            {
                return editValue;
            }
            set
            {
                if (editValue != value)
                {
                    editValue = value;

                    if (Editor != null) // Just in case EditValue is set before parameter has been added
                    {
                        // This should really be exposed to UI controls so multiple edits can be inside a begin/end edit
                        Editor.Host.BeginEdit(ParameterIndex);
                        Editor.Host.PerformEdit(ParameterIndex, GetValueNormalized(value));
                        Editor.Host.EndEdit(ParameterIndex);
                    }

                    OnPropertyChanged("EditValue");
                    OnPropertyChanged("DisplayValue");
                }
            }
        }

        public virtual string DisplayValue { get { return String.Format(ValueFormat, editValue); } }

        public double NormalizedProcessValue
        {
            get { return GetValueNormalized(processValue); }
            set { processValue = GetValueDenormalized(value); }
        }

        public double NormalizedEditValue
        {
            get { return GetValueNormalized(editValue); }
            set { editValue = GetValueDenormalized(value); }
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

        public void AddParameterChangePoint(double newNormalizedValue, int sampleOffset)
        {
            prevParamChangeSample = nextParamChangeSample;
            prevParamValue = processValue;

            nextParamChangeSample = sampleOffset;
            NormalizedProcessValue = newNormalizedValue;

            needInterpolationUpdate = true;

            if (needInterpolationUpdate)
            {
                slope = (double)(processValue - prevParamValue) / (double)(nextParamChangeSample - prevParamChangeSample);

                // No need to update if the parameter isn't changing
                if (slope == 0)
                    needInterpolationUpdate = false;
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

            // If we go past the last control point, the value stays the same
            if (sampleOffset > nextParamChangeSample)
            {
                needInterpolationUpdate = false;

                return prevParamValue;
            }

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
            RangePower = 1.0 / 4.0;
            ValueFormat = "{0:0.0}dB";
        }

        public override string DisplayValue
        {
            get
            {
                if (double.IsNegativeInfinity(EditValue))
                    return "-inf dB";

                return base.DisplayValue;
            }
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

            return (RangePower > 0) ? Math.Pow(rangeVal, RangePower) : rangeVal;
        }

        public override double GetValueDenormalized(double value)
        {
            double rangeVal = (RangePower > 0) ? Math.Pow(value, 1 / RangePower) : value;

            return LinearToDB(linearMin + ((linearMax - linearMin) * rangeVal));
        }
    }
}
