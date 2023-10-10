using System;
using System.Xml.Serialization;
using AudioPlugSharp;
using AudioPlugSharp.Asio;

namespace AudioPlugSharpHost
{
    public class HostSettings
    {
        public string AsioDeviceName { get; set; }
    }

    public class AudioPlugSharpHost<T> : IAudioHost where T : IAudioPlugin, IAudioPluginProcessor, IAudioPluginEditor
    {
        public T Plugin { get; private set; }

        public HostSettings HostSettings { get; private set; } = new HostSettings();
        public double SampleRate { get; private set; }
        public uint MaxAudioBufferSize { get; private set; }
        public uint CurrentAudioBufferSize { get; private set; }
        public EAudioBitsPerSample BitsPerSample { get; private set; }
        public double BPM { get; private set; }
        public long CurrentProjectSample { get; private set; }
        public bool IsPlaying { get; private set; }

        public AsioDriver AsioDriver { get; private set; } = null;

        string saveFolder;
        string hostSettingsFile;

        public AudioPlugSharpHost(T plugin)
        {
            this.Plugin = plugin;

            (plugin as IAudioPlugin).Host = this;

            plugin.Initialize();

            saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Plugin.PluginName);

            hostSettingsFile = Path.Combine(saveFolder, "HostSettings.xml");

            if (File.Exists(hostSettingsFile))
            {
                try
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(HostSettings));

                    using (Stream inputStream = File.OpenRead(hostSettingsFile))
                    {
                        HostSettings = serializer.Deserialize(inputStream) as HostSettings;
                    }

                    using (Stream saveStream = File.OpenRead(Path.Combine(saveFolder, "SaveData")))
                    {
                        byte[] data = new byte[saveStream.Length];

                        saveStream.Read(data);

                        plugin.RestoreState(data);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("AudioPlugSharpHost - error loading HostSettings.xml: " + ex.ToString());
                }
            }

            if (!String.IsNullOrEmpty(HostSettings.AsioDeviceName))
            {
                SetAsioDriver(HostSettings.AsioDeviceName);
            }
        }

        public void Exit()
        {
            byte[] data = Plugin.SaveState();

            try
            {
                if (!Directory.Exists(saveFolder))
                {
                    Directory.CreateDirectory(saveFolder);
                }

                XmlSerializer serializer = new XmlSerializer(typeof(HostSettings));

                using (Stream hostStream = File.Create(hostSettingsFile))
                {
                    serializer.Serialize(hostStream, HostSettings);
                }

                using (Stream saveStream = File.Create(Path.Combine(saveFolder, "SaveData")))
                {
                    saveStream.Write(data);
                }
            }
            catch (Exception ex)
            {

            }

            Plugin.HideEditor();

            if (AsioDriver != null)
            {
                AsioDriver.Stop();
                AsioDriver.Release();
            }
        }

        public void SetAsioDriver(string asioDeviceName)
        {
            HostSettings.AsioDeviceName = asioDeviceName;

            try
            {
                if (AsioDriver != null)
                {
                    AsioDriver.Stop();
                }

                this.AsioDriver = new AsioDriver(asioDeviceName);

                AsioDriver.ProcessAction = AsioProcess;

                SampleRate = AsioDriver.SampleRate;
                MaxAudioBufferSize = CurrentAudioBufferSize = (uint)AsioDriver.PreferredBufferSize();
                BitsPerSample = EAudioBitsPerSample.Bits32;

                Plugin.InitializeProcessing();

                Plugin.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);

                AsioDriver.Start();
            }
            catch (Exception ex)
            {
                Logger.Log("AudioPlugSharpHost - error initializing Asio device [" + asioDeviceName + "]: " + ex.ToString());

                HostSettings.AsioDeviceName = null;
            }
        }

        unsafe void AsioProcess(IntPtr[] inputBuffers, IntPtr[] outputBuffers)
        {
            int inputCount = 0;

            for (int input = 0; input < Plugin.InputPorts.Length; input++)
            {
                AudioIOPort port = Plugin.InputPorts[input];

                for (int channel = 0; channel < port.NumChannels; channel++)
                {
                    port.SetCurrentBufferSize(CurrentAudioBufferSize);

                    double* inputBuf = ((double **)port.GetAudioBufferPtrs())[channel];
                    int* asioPtr = (int*)inputBuffers[inputCount % AsioDriver.NumInputChannels];    // recyle inputs if we don't have enough

                    for (int i = 0; i < CurrentAudioBufferSize; i++)
                    {
                        inputBuf[i] = (double)asioPtr[i] / (double)Int32.MaxValue;
                    }

                    inputCount++;
                }
            }

            Plugin.PreProcess();
            Plugin.Process();
            Plugin.PostProcess();

            int outputCount = 0;

            while (outputCount < AsioDriver.NumOutputChannels)
            {
                for (int output = 0; output < Plugin.OutputPorts.Length; output++)
                {
                    if (outputCount >= AsioDriver.NumOutputChannels)
                        break;

                    AudioIOPort port = Plugin.OutputPorts[output];

                    for (int channel = 0; channel < port.NumChannels; channel++)
                    {
                        if (outputCount >= AsioDriver.NumOutputChannels)
                            break;

                        port.SetCurrentBufferSize(CurrentAudioBufferSize);

                        double* outputBuf = ((double**)port.GetAudioBufferPtrs())[channel];
                        int* asioPtr = (int*)outputBuffers[outputCount];

                        for (int i = 0; i < CurrentAudioBufferSize; i++)
                        {
                            asioPtr[i] = (int)(outputBuf[i] * Int32.MaxValue);
                        }

                        outputCount++;
                    }
                }
            }
        }

        public void BeginEdit(int parameter)
        {
        }

        public void EndEdit(int parameter)
        {
        }

        public void PerformEdit(int parameter, double normalizedValue)
        {
        }

        public void ProcessAllEvents()
        {
        }

        public int ProcessEvents()
        {
            return 0;
        }

        public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset)
        {
        }

        public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset)
        {
        }
    }
}
