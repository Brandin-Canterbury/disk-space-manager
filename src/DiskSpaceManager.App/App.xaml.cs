using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DiskSpaceManager.App;

public partial class App : Application
{
    private const string MutexName = @"Local\DiskSpaceManager.Analyzer";
    private static Mutex? _mutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // A plain launch reuses the open window; "--scan <folder>" always opens its own window.
        bool explicitScan = e.Args.Contains("--scan");
        if (!explicitScan && !AcquireSingleInstance())
        {
            ActivateExisting();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        int scanIdx = Array.IndexOf(e.Args, "--scan");
        string? scanPath = scanIdx >= 0 && scanIdx + 1 < e.Args.Length ? e.Args[scanIdx + 1] : null;
        var win = new MainWindow(openSettings: e.Args.Contains("--settings"), scanPath: scanPath);
        MainWindow = win;
        win.Show();
    }

    private static bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>Lets go of the single-instance lock so an elevated copy can start.</summary>
    public static void ReleaseSingleInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    public static void ReacquireSingleInstance() => AcquireSingleInstance();

    private static void ActivateExisting()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(p.MainWindowHandle, 9 /*SW_RESTORE*/);
            SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(MainWindow, e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
