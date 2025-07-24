using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace AudioPlugSharp
{
    public enum EAudioPluginParameterType
    {
        Float,
        Bool,
        Int,
        Enum
    }

    public class AudioPluginParameter : INotifyPropertyChanged
    {
        public IAudioPluginEditor Editor { get; internal set; } 

        public event PropertyChangedEventHandler PropertyChanged;

        public int ParameterIndex { get; internal set; }
        public string ID { get; set; }
        public string Name { get; set; }
        public EAudioPluginParameterType Type { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
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

        public string DisplayValue { get { return String.Format(ValueFormat, editValue); } }

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

        public static double DbToLinear(double db)
        {
            return Math.Pow(10.0, 0.05 * db);
        }

        public double GetValueNormalized(double value)
        {
            return (value - MinValue) / (MaxValue - MinValue);
        }

        public double GetValueDenormalized(double value)
        {
            return MinValue + ((MaxValue - MinValue) * value);
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
}
