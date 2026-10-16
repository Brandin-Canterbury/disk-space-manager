using System.Diagnostics;
using Microsoft.Win32;

namespace DiskSpaceManager.Core;

/// <summary>Locating and launching the two executables, plus the "start with Windows" switch.</summary>
public static class AppLinks
{
    public const string AnalyzerExe = "DiskSpaceManager.exe";
    public const string MonitorExe = "DiskSpaceMonitor.exe";
    public const string MonitorMutexName = @"Local\DiskSpaceManager.Monitor";
    public const string MonitorTestEventName = @"Local\DiskSpaceManager.Monitor.Test";
    public const string MonitorCheckEventName = @"Local\DiskSpaceManager.Monitor.CheckNow";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DiskSpaceMonitor";

    public static string AnalyzerPath => Path.Combine(AppContext.BaseDirectory, AnalyzerExe);
    public static string MonitorPath => Path.Combine(AppContext.BaseDirectory, MonitorExe);

    public static bool IsMonitorRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MonitorMutexName);
            return true;
        }
        catch { return false; }
    }

    public static bool StartMonitor(string? args = null)
    {
        if (!File.Exists(MonitorPath)) return false;
        Process.Start(new ProcessStartInfo(MonitorPath, args ?? "") { UseShellExecute = true });
        return true;
    }

    /// <summary>Signals a running monitor (test toast / immediate check). Returns false if none is running.</summary>
    public static bool SignalMonitor(string eventName)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(eventName);
            e.Set();
            return true;
        }
        catch { return false; }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunValue, $"\"{MonitorPath}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
