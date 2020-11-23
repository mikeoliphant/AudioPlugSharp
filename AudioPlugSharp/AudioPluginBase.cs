using System;
using System.Collections.Generic;
using System.Text;

namespace AudioPlugSharp
{
    public class AudioPluginBase : IAudioPlugin, IAudioPluginProcessor, IAudioPluginEditor
    {
        // IAudioPlugin Properties
        public string Company { get; protected set; }
        public string Website { get; protected set; }
        public string Contact { get; protected set; }
        public string PluginName { get; protected set; }
        public string PluginCategory { get; protected set; }
        public string PluginVersion { get; protected set; }
        public ulong PluginID { get; protected set; }
        public IAudioPluginProcessor Processor { get { return this; } }
        public IAudioPluginEditor Editor { get { return this; } }

        // IAudioPluginProcessor Properties
        public AudioIOPort[] InputPorts { get; protected set; }
        public AudioIOPort[] OutputPorts { get; protected set; }
        public IReadOnlyList<AudioPluginParameter> Parameters { get; private set; }

        List<AudioPluginParameter> parameterList = new List<AudioPluginParameter>();
        Dictionary<string, AudioPluginParameter> parameterDict = new Dictionary<string, AudioPluginParameter>();

        public AudioPluginBase()
        {
        }

        // IAudioPluginProcessor Methods

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

        // IAudioPluginEditor Methods

        public virtual void InitializeEditor()
        {
        }
    }
}
