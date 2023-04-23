using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace AudioPlugSharp
{
    public class AudioPluginBase : IAudioPlugin, IAudioPluginProcessor, IAudioPluginEditor
    {
        //
        // IAudioPlugin Properties
        //

        public string Company { get; protected set; }
        public string Website { get; protected set; }
        public string Contact { get; protected set; }
        public string PluginName { get; protected set; }
        public string PluginCategory { get; protected set; }
        public string PluginVersion { get; protected set; }
        public ulong PluginID { get; protected set; }

        public bool CacheLoadContext { get; protected set; } = false;

        public IAudioHost Host { get; set; }

        public IAudioPluginProcessor Processor { get { return this; } }
        public IAudioPluginEditor Editor { get { return this; } }


        //
        // IAudioPluginProcessor Properties
        //

        public AudioIOPort[] InputPorts { get; protected set; }
        public AudioIOPort[] OutputPorts { get; protected set; }
        public EAudioBitsPerSample SampleFormatsSupported { get; protected set; }
        public IReadOnlyList<AudioPluginParameter> Parameters { get; private set; }

        List<AudioPluginParameter> parameterList = new List<AudioPluginParameter>();
        Dictionary<string, AudioPluginParameter> parameterDict = new Dictionary<string, AudioPluginParameter>();
        Dictionary<uint, AudioPluginParameter> parameterCCDict = new Dictionary<uint, AudioPluginParameter>();

        public AudioPluginSaveState SaveStateData { get; protected set; }

        //
        // IAudioPluginEditor Properties
        //

        public bool HasUserInterface { get; protected set; }
        public uint EditorWidth { get; protected set; }
        public uint EditorHeight { get; protected set; }

        public AudioPluginBase()
        {
            SaveStateData = new AudioPluginSaveState();

            InputPorts = new AudioIOPort[0];
            OutputPorts = new AudioIOPort[0];

            SampleFormatsSupported = EAudioBitsPerSample.Bits32 | EAudioBitsPerSample.Bits64;

            HasUserInterface = false;
            EditorWidth = 400;
            EditorHeight = 200;
        }

        //
        // IAudioPluginProcessor Methods
        //

        public virtual void Initialize()
        {
            Logger.Log("Initializing processor");            

            Parameters = parameterList.AsReadOnly();
        }

        public void AddParameter(AudioPluginParameter parameter)
        {
            parameter.Editor = this;
            parameter.ParameterIndex = parameterList.Count;

            parameterList.Add(parameter);
            parameterDict[parameter.ID] = parameter;

            parameter.ProcessValue = parameter.DefaultValue;
            parameter.EditValue = parameter.DefaultValue;
        }

        public AudioPluginParameter GetParameter(string paramID)
        {
            return parameterDict[paramID];
        }

        public void AddMidiControllerMapping(AudioPluginParameter parameter, uint ccNumber)
        {
            parameterCCDict[ccNumber] = parameter;
        }

        public AudioPluginParameter GetParameterByMidiController(uint ccNumber)
        {
            if (!parameterCCDict.ContainsKey(ccNumber))
                return null;

            return parameterCCDict[ccNumber];
        }


        public virtual byte[] SaveState()
        {
            SaveStateData.SaveParameterValues(Parameters);

            XmlSerializer serializer = new XmlSerializer(SaveStateData.GetType());

            try
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    serializer.Serialize(memoryStream, SaveStateData);

                    return memoryStream.ToArray();
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Save state serialization failed with: " + ex.ToString());
            }

            return null;
        }

        public virtual void RestoreState(byte[] stateData)
        {
            if (stateData != null)
            {
                XmlSerializer serializer = new XmlSerializer(SaveStateData.GetType());

                try
                {
                    using (MemoryStream memoryStream = new MemoryStream(stateData))
                    {
                        SaveStateData = serializer.Deserialize(memoryStream) as AudioPluginSaveState;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Save state deserialization failed with: " + ex.ToString());
                }

                SaveStateData.RestoreParameterValues(Parameters);
            }
        }


        public virtual void InitializeProcessing()
        {
            Logger.Log("Initialize Processing");
        }

        public virtual void Start()
        {
            Logger.Log("Start Processor");
        }

        public virtual void Stop()
        {
            Logger.Log("Stop Processor");
        }

        public virtual void HandleParameterChange(AudioPluginParameter parameter, double newNormalizedValue, int sampleOffset)
        {
            parameter.AddParameterChangePoint(newNormalizedValue, sampleOffset);
        }

        public virtual void HandleNoteOn(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public virtual void HandleNoteOff(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public virtual void HandlePolyPressure(int channel, int noteNumber, float pressure, int sampleOffset)
        {
        }

        public virtual void Process()
        {
        }


        //
        // IAudioPluginEditor Methods
        //

        public virtual double GetDpiScale()
        {
            return 1.0;
        }

        public virtual void InitializeEditor()
        {
            Logger.Log("Initialize Editor");
        }

        public virtual void ResizeEditor(uint newWidth, uint newHeight)
        {
            EditorWidth = newWidth;
            EditorHeight = newHeight;
        }

        public virtual void ShowEditor(IntPtr parentWindow)
        {
        }

        public virtual void HideEditor()
        {
        }
    }
}
