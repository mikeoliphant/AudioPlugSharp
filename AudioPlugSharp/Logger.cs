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
        static Thread logThread;
        static bool exit = false;

        static Logger()
        {
            //ImmediateMode = true;

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

                logThread = new Thread(() => DoLogging());

                logThread.Priority = ThreadPriority.Lowest;
                logThread.IsBackground = true;
                logThread.Start();
            }
            catch
            {
                // No log file: at least stderr.
                WriteToStdErr = true;
            }
        }

        /// <summary>
        /// Writes what's queued now, on the calling thread (instead of within a quarter second): for errors, which may
        /// come just before the process ends.
        /// </summary>
        public static void Flush()
        {
            WriteLogs();
        }

        /// <summary>
        /// Logs unhandled exceptions (and writes them at once, before the process ends). Once per process; the DAW
        /// bridge and the standalone host call it.
        /// </summary>
        public static void LogUnhandledExceptions()
        {
            if (Interlocked.Exchange(ref loggingUnhandled, 1) == 1)
                return;

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log("Unhandled exception" + (e.IsTerminating ? " (the process ends)" : "") + ": " + e.ExceptionObject);

                Flush();
            };
        }

        static int loggingUnhandled;

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
            // Only this logger's files, named by their time (not the native side's native.log).
            Dictionary<string, long> fileTimes = new Dictionary<string, long>();

            foreach (string logFile in Directory.GetFiles(logPath, "*.log"))
            {
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(logFile), logFileDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileTime))
                    fileTimes[logFile] = fileTime.Ticks;
            }

            string[] logFiles = new string[fileTimes.Count];
            fileTimes.Keys.CopyTo(logFiles, 0);

            Array.Sort(logFiles, delegate (string file1, string file2)
            {
                return fileTimes[file2].CompareTo(fileTimes[file1]);
            });

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

                if (exit)
                    break;
            }
            while (true);
        }

        // From the log thread, and from any thread through Flush (or ImmediateMode): one at a time.
        static readonly object writeLock = new object();

        static void WriteLogs()
        {
            lock (writeLock)
            {
                string logEntry;
                bool wroteEntries = false;

                while (logQueue.TryDequeue(out logEntry))
                {
                    try
                    {
                        logWriter.WriteLine(logEntry);
                    }
                    catch { }

                    wroteEntries = true;
                }

                if (wroteEntries)
                {
                    try
                    {
                        logWriter.Flush();
                    }
                    catch { }
                }
            }
        }

        public static void FlushAndShutdown()
        {
            exit = true;

            if (logThread != null)
                logThread.Join();

            logThread = null;
        }
    }
}
