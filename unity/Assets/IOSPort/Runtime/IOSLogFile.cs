using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace IOSPort
{
    /// <summary>
    /// Mirrors the Unity log to Documents/iosport-log.txt (visible in the Files app,
    /// see UIFileSharingEnabled in the Info.plist) so problems on a device can be
    /// diagnosed without Xcode. The previous run is kept as iosport-log-prev.txt.
    /// </summary>
    public static class IOSLogFile
    {
        static StreamWriter writer;
        static readonly object gate = new object();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            if (writer != null) return;
            try
            {
                string dir = Application.persistentDataPath;
                string path = Path.Combine(dir, "iosport-log.txt");
                string prev = Path.Combine(dir, "iosport-log-prev.txt");
                if (File.Exists(path))
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(path, prev);
                }
                writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Application.productName} {Application.version} " +
                    $"Unity {Application.unityVersion} on {SystemInfo.deviceModel} ({SystemInfo.operatingSystem}), " +
                    $"{SystemInfo.systemMemorySize} MB RAM, {SystemInfo.graphicsDeviceName}");
                Application.logMessageReceivedThreaded += OnLog;
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("UNHANDLED " + e.ExceptionObject);
                Application.lowMemory += () => Write("LOW MEMORY warning");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IOSPort] log file unavailable: " + e.Message);
            }
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Write($"{type}: {message}\n{stack}");
            else
                Write($"{type}: {message}");
        }

        static void Write(string line)
        {
            lock (gate)
            {
                try { writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}"); }
                catch { }
            }
        }
    }
}
