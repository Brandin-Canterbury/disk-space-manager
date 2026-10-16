using DiskSpaceManager.Core;

namespace DiskSpaceManager.Monitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool test = args.Contains("--test", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, AppLinks.MonitorMutexName, out bool created);
        if (!created)
        {
            // Already running: forward the request to the live instance.
            AppLinks.SignalMonitor(test ? AppLinks.MonitorTestEventName : AppLinks.MonitorCheckEventName);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(test));
        GC.KeepAlive(mutex);
    }
}
