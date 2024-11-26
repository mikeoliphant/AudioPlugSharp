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

            (plugin as IAudioPlugin).Host = this;

            plugin.Initialize();

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
            Console.WriteLine("Exiting");

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
            SampleRate = 48000;
            MaxAudioBufferSize = 512;
            BitsPerSample = EAudioBitsPerSample.Bits32;

            jackProcessor = new(Plugin.PluginName, 1, 2, 0, 0, autoconnect: true);

            jackProcessor.ProcessFunc = Process;

            Plugin.InitializeProcessing();

            Plugin.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);

            if (jackProcessor.Start())
            {
            }

            Plugin.ShowEditor(IntPtr.Zero);
            Exit();
        }

        void Process(ProcessBuffer buffer)
        {
            AudioIOPort input = Plugin.InputPorts[0];
            input.SetCurrentBufferSize((uint)buffer.Frames);

            AudioIOPort output = Plugin.OutputPorts[0];
            output.SetCurrentBufferSize((uint)buffer.Frames);

            float[] jackIn = buffer.AudioIn[0].Audio;

            input.CopyFrom(jackIn, 0);

            Plugin.PreProcess();
            Plugin.Process();
            Plugin.PostProcess();

            for (int channel = 0; channel < 2; channel++)
            {
                float[] jackOut = buffer.AudioOut[channel].Audio;

                output.CopyFrom(jackOut, channel);
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
