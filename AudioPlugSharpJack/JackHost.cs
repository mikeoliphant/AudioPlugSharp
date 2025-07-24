using AudioPlugSharp;
using JackSharp;
using JackSharp.Processing;

namespace AudioPlugSharpJack
{
    public class JackHost<T> : IAudioHost where T : IAudioPlugin, IAudioPluginProcessor, IAudioPluginEditor
    {
        public T Plugin { get; private set; }

        public double SampleRate { get; private set; }
        public uint MaxAudioBufferSize { get; private set; }
        public uint CurrentAudioBufferSize { get; private set; }
        public EAudioBitsPerSample BitsPerSample { get; private set; }
        public double BPM { get; private set; }
        public long CurrentProjectSample { get; private set; }
        public bool IsPlaying { get; private set; }

        string saveFolder;
        Processor jackProcessor;

        public JackHost(T plugin)
        {
            this.Plugin = plugin;

            Logger.WriteToStdErr = true;

            (plugin as IAudioPlugin).Host = this;

            plugin.Initialize();

            foreach (AudioIOPort input in plugin.InputPorts)
            {
                input.ForceBackingBuffer = true;
            }

            foreach (AudioIOPort output in plugin.OutputPorts)
            {
                output.ForceBackingBuffer = true;
            }

            saveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Plugin.PluginName);

            try
            {
                using (Stream saveStream = File.OpenRead(Path.Combine(saveFolder, "SaveData")))
                {
                    byte[] data = new byte[saveStream.Length];

                    saveStream.Read(data);

                    plugin.RestoreState(data);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Error loading save data: " + ex.ToString());
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

                using (Stream saveStream = File.Create(Path.Combine(saveFolder, "SaveData")))
                {
                    saveStream.Write(data);
                }
            }
            catch (Exception ex)
            {

            }

            Plugin.HideEditor();

            jackProcessor.Stop();
        }

        public void Run()
        {
            MaxAudioBufferSize = 512;
            BitsPerSample = EAudioBitsPerSample.Bits32;

            jackProcessor = new(Plugin.PluginName, 1, 2, 0, 0, autoconnect: true);

            if (!jackProcessor.Start())
            {
                Logger.Log("Unable to connect to Jack");
            }
            else
            {
                SampleRate = jackProcessor.SampleRate;

                Logger.Log("Jack sample rate: " + SampleRate);
                Logger.Log("Jack buffer size: " + jackProcessor.BufferSize);

                Plugin.InitializeProcessing();

                Plugin.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);

                jackProcessor.ProcessFunc = Process;

                if (Plugin.HasUserInterface)
                {
                    try
                    {
                        Plugin.ShowEditor(IntPtr.Zero);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Plugin failed with: " + ex.ToString());
                    }

                    Exit();
                }
                else
                {
                    Thread.Sleep(Timeout.Infinite);
                }

                Logger.FlushAndShutdown();
            }
        }

        void Process(ProcessBuffer buffer)
        {
            CurrentAudioBufferSize = (uint)jackProcessor.BufferSize;

            AudioIOPort input = Plugin.InputPorts[0];
            input.SetCurrentBufferSize((uint)buffer.Frames);

            AudioIOPort output = Plugin.OutputPorts[0];
            output.SetCurrentBufferSize((uint)buffer.Frames);

            float[] jackIn = buffer.AudioIn[0].Audio;

            input.CopyFrom(jackIn, 0);

            Plugin.PreProcess();
            Plugin.Process();
            Plugin.PostProcess();

            for (int channel = 0; channel < output.NumChannels; channel++)
            {
                float[] jackOut = buffer.AudioOut[channel].Audio;

                output.CopyTo(jackOut, channel);
            }
        }

        public void BeginEdit(int parameter)
        {
        }

        public void EndEdit(int parameter)
        {
            PerformEdit(parameter, (Plugin as IAudioPluginEditor).Parameters[parameter].NormalizedEditValue);
        }

        public void PerformEdit(int parameter, double normalizedValue)
        {
            (Plugin as IAudioPluginProcessor).Parameters[parameter].NormalizedProcessValue = normalizedValue;
        }

        public void ProcessAllEvents()
        {
        }

        public int ProcessEvents()
        {
            return (int)CurrentAudioBufferSize;
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
