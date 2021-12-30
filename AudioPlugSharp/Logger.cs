using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AudioPlugSharp
{
    public class Logger
    {
        const int NumLogsToRetain = 10;

        /// <summary>
        /// Write log entries immediately - should not be used in normal operation
        /// </summary>
        public static bool ImmediateMode { get; set; }

        public static bool WriteToStdErr { get; set; }

        static ConcurrentQueue<string> logQueue;
        static StreamWriter logWriter = null;
        static string logPath;
        static string logFileDateFormat = "yyyy-MM-dd-HH-mm-ss-ffff";

        static Logger()
        {
            ImmediateMode = true;

            logQueue = new ConcurrentQueue<string>();

            logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioPlugSharp");

            try
            {
                if (!Directory.Exists(logPath))
                    Directory.CreateDirectory(logPath);

                DeleteOldLogs();
            }
            catch (Exception ex)
            {
                Log("Failed to delete old logfiles: " + ex.ToString());
            }

            try
            {
                string filename = string.Format("{0:" + logFileDateFormat + "}.log", DateTime.Now);

                string logFile = Path.Combine(logPath, filename);

                logWriter = new StreamWriter(logFile);

                Thread logThread = new Thread(() => DoLogging());

                logThread.Priority = ThreadPriority.Lowest;
                logThread.IsBackground = true;
                logThread.Start();
            }
            catch
            {
            }
        }

        public static void Log(string logEntry)
        {
            logEntry = string.Format("[{0:yyyy/MM/dd HH:mm:ss:ffff}] {1}", DateTime.Now, logEntry);

            logQueue.Enqueue(logEntry);

            if (ImmediateMode)
                WriteLogs();

            if (WriteToStdErr)
                Console.Error.WriteLine(logEntry);
        }

        static void DeleteOldLogs()
        {
            string[] logFiles = Directory.GetFiles(logPath, "*.log");

            if (logFiles.Length >= NumLogsToRetain)
            {
                Dictionary<string, long> fileTimes = new Dictionary<string, long>();

                foreach (string logFile in logFiles)
                {
                    DateTime fileTime = DateTime.MinValue;

                    DateTime.TryParseExact(Path.GetFileNameWithoutExtension(logFile), logFileDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out fileTime);

                    fileTimes[logFile] = fileTime.Ticks;
                }

                Array.Sort(logFiles, delegate (string file1, string file2)
                {
                    return fileTimes[file2].CompareTo(fileTimes[file1]);
                });
            }

            for (int i = NumLogsToRetain - 1; i < logFiles.Length; i++)
            {
                try
                {
                    File.Delete(logFiles[i]);
                }
                catch { }
            }
        }

        static void DoLogging()
        {
            do
            {
                Thread.Sleep(250);

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
    }
}
