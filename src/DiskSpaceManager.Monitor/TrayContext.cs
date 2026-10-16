using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiskSpaceManager.Core;
using Microsoft.Win32;

namespace DiskSpaceManager.Monitor;

/// <summary>
/// The whole monitor: a tray icon, a timer that checks free space, Windows notifications when a
/// drive crosses its limit (or drops fast), and an hourly free-space log for trend display.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private sealed class DriveState
    {
        public bool Low;
        public DateTime LastNotifiedUtc = DateTime.MinValue;
        public DateTime LastRapidUtc = DateTime.MinValue;
        public readonly Queue<(DateTime Utc, long Free)> Recent = new();
    }

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 750 };
    private readonly FileSystemWatcher? _watcher;
    private readonly SynchronizationContext _ui;
    private readonly EventWaitHandle _testEvent, _checkEvent;
    private readonly RegisteredWaitHandle _testWait, _checkWait;
    private readonly Dictionary<string, DriveState> _state = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings = SettingsStore.Load();
    private List<DriveSnapshot> _drives = [];
    private DateTime _lastHistoryUtc = DateTime.MinValue;
    private Icon? _currentIcon;

    public TrayContext(bool showTest)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true, Text = "Disk Space Monitor" };
        _tray.DoubleClick += (_, _) => OpenAnalyzer();
        _tray.BalloonTipClicked += (_, _) => OpenAnalyzer();
        _menu.Opening += (_, _) => BuildMenu();

        _timer.Tick += (_, _) => Check();
        ApplyInterval();
        _timer.Start();

        // Pick up settings changes made in the analyzer.
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            _watcher = new FileSystemWatcher(SettingsStore.Folder, Path.GetFileName(SettingsStore.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _ui.Post(_ => RestartDebounce(), null);
            _watcher.Created += (_, _) => _ui.Post(_ => RestartDebounce(), null);
            _watcher.Renamed += (_, _) => _ui.Post(_ => RestartDebounce(), null);
        }
        catch { /* settings changes then apply on next start */ }
        _settingsDebounce.Tick += (_, _) =>
        {
            _settingsDebounce.Stop();
            _settings = SettingsStore.Load();
            ApplyInterval();
            Check();
        };

        // Let the analyzer poke us ("send test notification", "settings saved").
        _testEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppLinks.MonitorTestEventName);
        _checkEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppLinks.MonitorCheckEventName);
        _testWait = ThreadPool.RegisterWaitForSingleObject(_testEvent, (_, _) => _ui.Post(_ => ShowTest(), null), null, -1, false);
        _checkWait = ThreadPool.RegisterWaitForSingleObject(_checkEvent, (_, _) => _ui.Post(_ => { _settings = SettingsStore.Load(); Check(); }, null), null, -1, false);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        FreeSpaceHistory.Prune();
        Check();
        if (showTest) ShowTest();
        TrimWorkingSet();
    }

    private void RestartDebounce()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    private void ApplyInterval() =>
        _timer.Interval = (int)TimeSpan.FromMinutes(Math.Clamp(_settings.CheckIntervalMinutes, 1, 24 * 60)).TotalMilliseconds;

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.Post(_ => Check(), null);
    }

    // ------------------------------------------------------------------
    // The check
    // ------------------------------------------------------------------

    private void Check()
    {
        var now = DateTime.UtcNow;
        try
        {
            _drives = DriveProbe.GetDrives(_settings.MonitorNetworkDrives, _settings.MonitorRemovableDrives);
        }
        catch { return; }

        var lowAlerts = new List<string>();
        var rapidAlerts = new List<string>();
        foreach (var d in _drives)
        {
            if (!_state.TryGetValue(d.Root, out var st)) _state[d.Root] = st = new DriveState();
            var rule = _settings.RuleFor(d.Root);
            bool low = rule.Enabled && d.Free < rule.ThresholdBytes(d.Total);

            if (low && (!st.Low || now - st.LastNotifiedUtc >= TimeSpan.FromHours(Math.Max(0.1, _settings.RenotifyHours))))
            {
                lowAlerts.Add($"{d.Letter} has {Fmt.Bytes(d.Free)} free ({d.FreePercent:0.#}%), below your {rule.Describe()} limit.");
                st.LastNotifiedUtc = now;
            }
            st.Low = low;

            // Rapid-drop detection over a rolling hour.
            st.Recent.Enqueue((now, d.Free));
            while (st.Recent.Count > 0 && now - st.Recent.Peek().Utc > TimeSpan.FromMinutes(65)) st.Recent.Dequeue();
            var oldest = st.Recent.Peek();
            long dropped = oldest.Free - d.Free;
            if (_settings.RapidDropGB > 0 && dropped >= _settings.RapidDropGB * Fmt.GB && now - st.LastRapidUtc > TimeSpan.FromHours(1))
            {
                var mins = Math.Max(1, (now - oldest.Utc).TotalMinutes);
                rapidAlerts.Add($"{d.Letter} lost {Fmt.Bytes(dropped)} in {mins:0} min — now {Fmt.Bytes(d.Free)} free.");
                st.LastRapidUtc = now;
            }
        }

        UpdateIcon();

        if (lowAlerts.Count > 0)
            Notify(lowAlerts.Count == 1 ? "Drive running low on space" : $"{lowAlerts.Count} drives running low on space",
                string.Join("\n", lowAlerts) + "\nClick to see what's using it.", ToolTipIcon.Warning);
        else if (rapidAlerts.Count > 0)
            Notify("Disk space is disappearing fast", string.Join("\n", rapidAlerts), ToolTipIcon.Info);

        if (now - _lastHistoryUtc >= TimeSpan.FromMinutes(59))
        {
            try
            {
                // Log every ready drive (not just monitored ones) so the analyzer can show trends.
                FreeSpaceHistory.Append(DriveProbe.GetDrives(includeNetwork: _settings.MonitorNetworkDrives));
                _lastHistoryUtc = now;
            }
            catch { }
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        // NotifyIcon balloons are shown as Windows toast notifications on Windows 10/11.
        _tray.ShowBalloonTip(15000, title, text.Length > 250 ? text[..250] + "…" : text, icon);
    }

    private void ShowTest()
    {
        var worst = _drives.OrderBy(d => d.FreePercent).FirstOrDefault();
        Notify("Disk Space Monitor is working",
            worst is null ? "No drives found to monitor."
                : $"Lowest drive: {worst.Letter} with {Fmt.Bytes(worst.Free)} free ({worst.FreePercent:0}%). You'll be alerted below {_settings.RuleFor(worst.Root).Describe()}.",
            ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------
    // Tray icon + menu
    // ------------------------------------------------------------------

    private void UpdateIcon()
    {
        bool anyLow = _state.Values.Any(s => s.Low);
        double worstUsed = _drives.Count == 0 ? 0 : _drives.Max(d => d.UsedFraction);
        var icon = DrawIcon(worstUsed, anyLow);
        _tray.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;

        var parts = _drives.Select(d => $"{d.Letter} {Fmt.Bytes(d.Free)}{(_state.TryGetValue(d.Root, out var s) && s.Low ? "⚠" : "")}");
        var tip = (anyLow ? "Low disk space!\n" : "Free space\n") + string.Join(" · ", parts);
        _tray.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static Icon DrawIcon(double usedFraction, bool low)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var bgColor = low ? Color.FromArgb(220, 38, 38) : Color.FromArgb(37, 99, 235);
            using (var bg = new SolidBrush(bgColor))
            using (var path = RoundedRect(new RectangleF(0, 0, size - 1, size - 1), size * 0.25f))
                g.FillPath(bg, path);

            float m = size * 0.16f, d = size - 2 * m;
            using (var disk = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.FillEllipse(disk, m, m, d, d);
            var sliceColor = usedFraction >= 0.9 || low ? Color.FromArgb(220, 38, 38) : usedFraction >= 0.8 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(37, 99, 235);
            using (var slice = new SolidBrush(sliceColor))
                g.FillPie(slice, m, m, d, d, -90, (float)(360 * Math.Clamp(usedFraction, 0.02, 1)));
            using var ring = new Pen(Color.White, Math.Max(1, size / 16f));
            g.DrawEllipse(ring, m, m, d, d);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripLabel("Disk Space Monitor") { Font = new Font(_menu.Font, FontStyle.Bold) });
        foreach (var d in _drives)
        {
            bool low = _state.TryGetValue(d.Root, out var s) && s.Low;
            var item = new ToolStripMenuItem($"{(low ? "⚠ " : "")}{d.DisplayName} — {Fmt.Bytes(d.Free)} free of {Fmt.Bytes(d.Total)} ({d.FreePercent:0}%)");
            if (low) item.ForeColor = Color.FromArgb(185, 28, 28);
            item.Click += (_, _) => OpenAnalyzer();
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem("Open Disk Space Manager") { Font = new Font(_menu.Font, FontStyle.Bold) };
        open.Click += (_, _) => OpenAnalyzer();
        _menu.Items.Add(open);
        _menu.Items.Add("Alert settings…", null, (_, _) => OpenAnalyzer("--settings"));
        _menu.Items.Add("Check now", null, (_, _) => { _settings = SettingsStore.Load(); Check(); ShowSummary(); });
        bool autostart = false;
        try { autostart = AppLinks.StartsWithWindows; } catch { }
        var auto = new ToolStripMenuItem("Start with Windows") { Checked = autostart };
        auto.Click += (_, _) =>
        {
            try { AppLinks.StartsWithWindows = !auto.Checked; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Disk Space Monitor"); }
        };
        _menu.Items.Add(auto);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit monitor", null, (_, _) => ExitThread());
    }

    private void ShowSummary()
    {
        var lines = _drives.Select(d => $"{d.Letter} {Fmt.Bytes(d.Free)} free ({d.FreePercent:0}%)");
        Notify("Free space", string.Join("\n", lines), ToolTipIcon.None);
    }

    private static void OpenAnalyzer(string? args = null)
    {
        try
        {
            if (File.Exists(AppLinks.AnalyzerPath))
                Process.Start(new ProcessStartInfo(AppLinks.AnalyzerPath, args ?? "") { UseShellExecute = true });
        }
        catch { }
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    private static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _testWait.Unregister(null);
        _checkWait.Unregister(null);
        _testEvent.Dispose();
        _checkEvent.Dispose();
        _watcher?.Dispose();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
