﻿using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public class AudioPluginSaveParameter
    {
        public string ID { get; set; }
        public double Value { get; set; }
    }

    public class AudioPluginSaveState
    {
        public List<AudioPluginSaveParameter> ParameterValues { get; set; }
        public uint EditorWidth { get; set; } = 800;
        public uint EditorHeight { get; set; } = 600;

        public void SaveParameterValues(IReadOnlyList<AudioPluginParameter> parameters)
        {
            ParameterValues = new List<AudioPluginSaveParameter>();

            foreach (AudioPluginParameter parameter in parameters)
            {
                ParameterValues.Add(new AudioPluginSaveParameter { ID = parameter.ID, Value = parameter.ProcessValue });
            }
        }

        public void RestoreParameterValues(IReadOnlyList<AudioPluginParameter> parameters)
        {
            foreach (AudioPluginParameter parameter in parameters)
            {
                foreach (AudioPluginSaveParameter saveParam in ParameterValues)
                {
                    if (saveParam.ID == parameter.ID)
                    {
                        parameter.EditValue = saveParam.Value;
                        parameter.ProcessValue = saveParam.Value;

                        break;
                    }
                }
            }
        }
    }
}
