using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace AudioPlugSharp.Asio
{
    public class AsioDriverEntry
    {
        private string name;
        private string description;
        private Guid clsID;

        public string Name
        {
          get { return name; }
          set { name = value; }
        }

        public string Description
        {
            get { return description; }
            set { description = value; }
        }

        public Guid ClsID
        {
          get { return clsID; }
          set { clsID = value; }
        }

        public AsioDriverEntry(string name, string description, Guid clsID)
        {
            this.name = name;
            this.description = description;
            this.clsID = clsID;
        }
    }

    public unsafe class AsioDriver : IDisposable
    {
        bool isDisposed = false;

        AsioInterop driver = null;
        ASIOCallbacks callbacks;
        private AsioDriverCapability capability;
        private ASIOBufferInfo[] bufferInfos;
        private bool isOutputReadySupported;
        int bufferBits;
        int bufferSize;
        IntPtr[] inputBufferPtrs;
        IntPtr[] outputBufferPtrs;

        public double SampleRate
        {
            get
            {
                return capability.SampleRate;
            }
        }

        public string Name { get; set; }
        public bool Muted { get; set; }

        public int NumChannels { get { return capability.NbInputChannels; } }
        public int NumInputChannels { get { return capability.NbInputChannels; } }
        public int NumOutputChannels { get { return capability.NbOutputChannels; } }

        public Action<IntPtr[], IntPtr[]> ProcessAction { get; set; } = null;

        public AsioDriver(string name)
            : this(AsioInterop.GetASIODriverByName(name))
        {
        }

        public AsioDriver(Guid id)
            : this(AsioInterop.GetASIODriverByGuid(id))
        {
        }

        ~AsioDriver()
        {
            Dispose(false);
        }

        private AsioDriver(AsioInterop driver)
        {
            this.driver = driver;

            Name = driver.getDriverName();

            if (!driver.init(IntPtr.Zero))
            {
                throw new InvalidOperationException(driver.getErrorMessage());
            }

            callbacks = new ASIOCallbacks();
            callbacks.pasioMessage = AsioMessageCallBack;
            callbacks.pbufferSwitch = BufferSwitchCallBack;
            callbacks.pbufferSwitchTimeInfo = BufferSwitchTimeInfoCallBack;
            callbacks.psampleRateDidChange = SampleRateDidChangeCallBack;

            int minSize;
            int maxSize;
            int preferredSize;
            int granularity;

            driver.getBufferSize(out minSize, out maxSize, out preferredSize, out granularity);

            bufferSize = preferredSize;

            BuildCapabilities();

            CreateBuffers(bufferSize);
        }

        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
            }

            if (driver != null)
                driver.disposeBuffers();

            isDisposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public int PreferredBufferSize()
        {
            return bufferSize;
        }

        public static List<AsioDriverEntry> GetAsioDriverEntries()
        {
            List<AsioDriverEntry> entries = new List<AsioDriverEntry>();

            RegistryKey asioKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\ASIO");

            if (asioKey != null)
            {
                string[] names = asioKey.GetSubKeyNames();

                foreach (string name in names)
                {
                    RegistryKey driverKey = asioKey.OpenSubKey(name);

                    if (driverKey != null)
                    {
                        string clsidStr = (string)driverKey.GetValue("CLSID");

                        if (clsidStr != null)
                        {
                            Guid clsid;

                            if (Guid.TryParse(clsidStr, out clsid))
                            {
                                entries.Add(new AsioDriverEntry(name, (string)driverKey.GetValue("Description"), clsid));
                            }
                        }
                    }
                }
            }

            return entries;
        }

        public void SetSampleRate(int sampleRate)
        {
            driver.setSampleRate(sampleRate);
        }

        public void ShowControlPanel()
        {
            driver.controlPanel();
        }

        public void Start()
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;

            bufferBits = (capability.OutputChannelInfos[0].type == AsioSampleType.Int16LSB) ? 16 : 32;

            driver.start();
        }

        public void Stop()
        {
            driver.stop();
        }

        public void Release()
        {
            driver.ReleaseComASIODriver();
        }

        void BuildCapabilities()
        {
            capability = new AsioDriverCapability();

            capability.DriverName = driver.getDriverName();

            // Get nb Input/Output channels
            driver.getChannels(out capability.NbInputChannels, out capability.NbOutputChannels);

            capability.InputChannelInfos = new ASIOChannelInfo[capability.NbInputChannels];
            capability.OutputChannelInfos = new ASIOChannelInfo[capability.NbOutputChannels];

            // Get ChannelInfo for Inputs
            for (int i = 0; i < capability.NbInputChannels; i++)
            {
                capability.InputChannelInfos[i] = driver.getChannelInfo(i, true);
            }

            // Get ChannelInfo for Output
            for (int i = 0; i < capability.NbOutputChannels; i++)
            {
                capability.OutputChannelInfos[i] = driver.getChannelInfo(i, false);
            }

            // Get the current SampleRate
            capability.SampleRate = driver.getSampleRate();

            var error = driver.GetLatencies(out capability.InputLatency, out capability.OutputLatency);
            // focusrite scarlett 2i4 returns ASE_NotPresent here

            if (error != ASIOError.ASE_OK && error != ASIOError.ASE_NotPresent)
            {
                var ex = new ASIOException("ASIOgetLatencies");
                ex.Error = error;
                throw ex;
            }

            // Get BufferSize
            driver.getBufferSize(out capability.BufferMinSize, out capability.BufferMaxSize, out capability.BufferPreferredSize, out capability.BufferGranularity);
        }

        int CreateBuffers(int bufferSize)
        {
            bufferInfos = new ASIOBufferInfo[capability.NbInputChannels + capability.NbOutputChannels];

            for (int index = 0; index < capability.NbInputChannels; index++)
            {
                bufferInfos[index].isInput = true;
                bufferInfos[index].channelNum = index;
                bufferInfos[index].pBuffer0 = IntPtr.Zero;
                bufferInfos[index].pBuffer1 = IntPtr.Zero;
            }

            for (int index = 0; index < capability.NbOutputChannels; index++)
            {
                bufferInfos[index + capability.NbInputChannels].isInput = false;
                bufferInfos[index + capability.NbInputChannels].channelNum = index;
                bufferInfos[index + capability.NbInputChannels].pBuffer0 = IntPtr.Zero;
                bufferInfos[index + capability.NbInputChannels].pBuffer1 = IntPtr.Zero;
            }

            fixed (ASIOBufferInfo* infos = &bufferInfos[0])
            {
                IntPtr pOutputBufferInfos = new IntPtr(infos);

                // Create the ASIO Buffers with the callbacks
                driver.createBuffers(pOutputBufferInfos, capability.NbInputChannels + capability.NbOutputChannels, bufferSize, ref callbacks);
            }

            inputBufferPtrs = new IntPtr[capability.NbInputChannels];
            outputBufferPtrs = new IntPtr[capability.NbOutputChannels];

            // Check if outputReady is supported
            isOutputReadySupported = (driver.outputReady() == ASIOError.ASE_OK);

            return bufferSize;
        }

        private void BufferSwitchCallBack(int doubleBufferIndex, bool directProcess)
        {
            for (int index = 0; index < capability.NbInputChannels; index++)
            {
                inputBufferPtrs[index] = bufferInfos[index].Buffer(doubleBufferIndex);
            }

            for (int index = 0; index < capability.NbOutputChannels; index++)
            {
                outputBufferPtrs[index] = bufferInfos[index + capability.NbInputChannels].Buffer(doubleBufferIndex);
            }

            if (ProcessAction != null)
            {
                ProcessAction(inputBufferPtrs, outputBufferPtrs);
            }

            if (isOutputReadySupported)
            {
                driver.outputReady();
            }
        }

        private void SampleRateDidChangeCallBack(double sRate)
        {
            capability.SampleRate = sRate;
        }

        private int AsioMessageCallBack(ASIOMessageSelector selector, int value, IntPtr message, IntPtr opt)
        {
            switch (selector)
            {
                case ASIOMessageSelector.kAsioSelectorSupported:
                    ASIOMessageSelector subValue = (ASIOMessageSelector)Enum.ToObject(typeof(ASIOMessageSelector), value);
                    switch (subValue)
                    {
                        case ASIOMessageSelector.kAsioEngineVersion:
                            return 1;
                        case ASIOMessageSelector.kAsioResetRequest:
                            return 0;
                        case ASIOMessageSelector.kAsioBufferSizeChange:
                            return 1;
                        case ASIOMessageSelector.kAsioResyncRequest:
                            return 0;
                        case ASIOMessageSelector.kAsioLatenciesChanged:
                            return 0;
                        case ASIOMessageSelector.kAsioSupportsTimeInfo:
                            //                            return 1; DON'T SUPPORT FOR NOW. NEED MORE TESTING.
                            return 0;
                        case ASIOMessageSelector.kAsioSupportsTimeCode:
                            //                            return 1; DON'T SUPPORT FOR NOW. NEED MORE TESTING.
                            return 0;
                    }
                    break;
                case ASIOMessageSelector.kAsioEngineVersion:
                    return 2;
                case ASIOMessageSelector.kAsioResetRequest:
                    //NeedReset = true;
                    return 1;
                case ASIOMessageSelector.kAsioBufferSizeChange:
                    return 0;
                case ASIOMessageSelector.kAsioResyncRequest:
                    return 0;
                case ASIOMessageSelector.kAsioLatenciesChanged:
                    return 0;
                case ASIOMessageSelector.kAsioSupportsTimeInfo:
                    return 0;
                case ASIOMessageSelector.kAsioSupportsTimeCode:
                    return 0;
            }

            return 0;
        }

        private IntPtr BufferSwitchTimeInfoCallBack(IntPtr asioTimeParam, int doubleBufferIndex, bool directProcess)
        {
            return IntPtr.Zero;
        }
    }
}
