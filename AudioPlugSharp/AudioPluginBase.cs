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

        public IAudioHost Host { get; set; }

        public IAudioPluginProcessor Processor { get { return this; } }
        public IAudioPluginEditor Editor { get { return this; } }


        //
        // IAudioPluginProcessor Properties
        //

        public AudioIOPort[] InputPorts { get; protected set; }
        public AudioIOPort[] OutputPorts { get; protected set; }
        public IReadOnlyList<AudioPluginParameter> Parameters { get; private set; }

        List<AudioPluginParameter> parameterList = new List<AudioPluginParameter>();
        Dictionary<string, AudioPluginParameter> parameterDict = new Dictionary<string, AudioPluginParameter>();

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

            Parameters = parameterList;
        }

        public void AddParameter(AudioPluginParameter parameter)
        {
            parameterList.Add(parameter);
            parameterDict[parameter.ID] = parameter;
        }

        public IReadOnlyCollection<AudioPluginParameter> EnumerateParameters()
        {
            return parameterList.AsReadOnly();
        }

        public AudioPluginParameter GetParameter(string paramID)
        {
            return parameterDict[paramID];
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

        public virtual void Process()
        {
        }


        //
        // IAudioPluginEditor Methods
        //

        public virtual void InitializeEditor()
        {
            Logger.Log("Initialize Editor");
        }

        public virtual bool ShowEditor(IntPtr parentWindow)
        {
            return false;
        }
    }
}
