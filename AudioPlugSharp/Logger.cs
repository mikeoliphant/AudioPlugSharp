using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AudioPlugSharp
{
    public class Logger
    {
        static ConcurrentQueue<string> logQueue;
        static StreamWriter logWriter;

        static Logger()
        {
            logQueue = new ConcurrentQueue<string>();

            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioPlugSharp");

            if (!Directory.Exists(logPath))
                Directory.CreateDirectory(logPath);

            string logFile = Path.Combine(logPath, "AudioPlugSharp.log");

            if (File.Exists(logFile))
            {
                string oldLogFile = Path.Combine(logPath, "AudioPlugSharp.log.old");

                if (File.Exists(oldLogFile))
                    File.Delete(oldLogFile);

                File.Move(logFile, oldLogFile);
            }

            logWriter = new StreamWriter(logFile);

            Thread logThread = new Thread(() => DoLogging());

            logThread.Priority = ThreadPriority.Lowest;
            logThread.IsBackground = true;
            logThread.Start();

            AppDomain.CurrentDomain.ProcessExit += (s, e) => WriteLogs();
        }

        static void DoLogging()
        {
            do
            {
                Thread.Sleep(1000);

                if (logQueue.Count > 0)
                {
                    WriteLogs();
                }
            }
            while (true);
        }

        static void WriteLogs()
        {
            string logEntry;

            bool wroteEntries = false;

            while (logQueue.TryDequeue(out logEntry))
            {
                logWriter.WriteLine(logEntry);

                wroteEntries = true;
            }

            if (wroteEntries)
                logWriter.Flush();
        }

        public static void Log(string logEntry)
        {
            logQueue.Enqueue(logEntry);
        }
    }
}
