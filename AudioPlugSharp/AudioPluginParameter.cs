using System;
using System.Collections.Generic;
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

    public class AudioPluginParameter
    {
        public string ID { get; set; }
        public string Name { get; set; }
        public EAudioPluginParameterType Type { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public double DefaultValue { get; set; }
        public string ValueFormat { get; set; }
        public double Value { get; set; }
        public string DisplayValue { get { return String.Format(ValueFormat, Value); } }
        public double NormalizedValue
        {
            get { return GetValueNormalized(Value); }
            set { Value = GetValueDenormalized(value); }
        }

        public AudioPluginParameter()
        {
            MinValue = 0;
            MaxValue = 1;
            DefaultValue = 0.5;
            ValueFormat = "{0:0.0}";
        }

        public double GetValueNormalized(double value)
        {
            return (value - MinValue) / (MaxValue - MinValue);
        }

        public double GetValueDenormalized(double value)
        {
            return MinValue + ((MaxValue - MinValue) * value);
        }
    }
}
