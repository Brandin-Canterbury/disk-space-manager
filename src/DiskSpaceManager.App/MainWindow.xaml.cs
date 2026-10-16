using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DiskSpaceManager.Core;
using Microsoft.Win32;

namespace DiskSpaceManager.App;

public partial class MainWindow : Window
{
    private const int MaxListedFiles = 5000;
    private const int MaxListedFolders = 3000;
    private const int MaxFolderFiles = 50_000;

    private AppSettings _settings;
    private readonly ObservableCollection<DriveCardVM> _drives = [];
    private readonly ObservableCollection<DirVM> _treeRoots = [];
    private List<FreeSpaceSample> _history = [];
    private ScanResult? _result;
    private DirNode? _current;
    private int _showToken;
    private CancellationTokenSource? _scanCts, _dupCts;
    private bool _scanning, _initializing = true;
    private readonly bool _isAdmin = ShellOps.IsAdministrator();
    private readonly DispatcherTimer _driveTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _monitorTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow(bool openSettings, string? scanPath = null)
    {
        InitializeComponent();
        _settings = SettingsStore.Load();

        DriveCards.ItemsSource = _drives;
        Tree.ItemsSource = _treeRoots;
        Map.HoverChanged += item => MapHoverText.Text = item is null
            ? "Treemap: each block's area is its size. Click a folder to drill in."
            : $"{item.Path} — {Fmt.Bytes(item.Size)}";
        Map.ItemActivated += Map_ItemActivated;

        AdminButton.Visibility = _isAdmin ? Visibility.Collapsed : Visibility.Visible;
        if (_isAdmin) Title += " (Administrator)";
        UpButton.IsEnabled = false;

        LoadDrives();
        PopulateSettings();
        UpdateMonitorStatus();
        SetStatus(_isAdmin
            ? "Running as administrator — protected folders will be included."
            : "Tip: without administrator rights a few protected folders (other users, parts of Windows) can't be read.");

        _driveTimer.Tick += (_, _) => RefreshDrives();
        _driveTimer.Start();
        _monitorTimer.Tick += (_, _) => UpdateMonitorStatus();
        _monitorTimer.Start();

        if (openSettings) Tabs.SelectedItem = SettingsTab;
        _initializing = false;

        // DiskSpaceManager.exe --scan "D:\Some Folder" starts scanning that folder right away.
        if (scanPath is not null && Directory.Exists(scanPath))
        {
            Loaded += async (_, _) =>
            {
                var result = await RunScanAsync([scanPath], $"Scanning {scanPath}");
                if (result is not null) await LoadResultAsync(result);
            };
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanCts?.Cancel();
        _dupCts?.Cancel();
        base.OnClosing(e);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ======================================================================
    // Drives
    // ======================================================================

    private void LoadDrives()
    {
        _history = FreeSpaceHistory.Read();
        _drives.Clear();
        foreach (var d in DriveProbe.GetDrives())
        {
            bool selected = d.Type != DriveType.Network || _settings.ScanNetworkDrivesByDefault;
            var card = new DriveCardVM(d, selected);
            UpdateTrend(card);
            _drives.Add(card);
        }
        _ = RefreshRecycleBinsAsync();
    }

    private void RefreshDrives()
    {
        _history = FreeSpaceHistory.Read();
        var snaps = DriveProbe.GetDrives();
        foreach (var s in snaps)
        {
            var card = _drives.FirstOrDefault(c => c.Root.Equals(s.Root, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                _drives.Add(card = new DriveCardVM(s, s.Type != DriveType.Network));
            }
            else
            {
                string info = card.ScanInfo;
                long rb = card.RecycleSize;
                card.Update(s);
                card.ScanInfo = info;
                card.RecycleSize = rb;
            }
            UpdateTrend(card);
        }
        foreach (var gone in _drives.Where(c => snaps.All(s => !s.Root.Equals(c.Root, StringComparison.OrdinalIgnoreCase))).ToList())
            _drives.Remove(gone);
        _ = RefreshRecycleBinsAsync();
    }

    private void UpdateTrend(DriveCardVM card)
    {
        var free = card.Snapshot.Free;
        var day = FreeSpaceHistory.FreeChange(_history, card.Root, free, TimeSpan.FromDays(1));
        var week = FreeSpaceHistory.FreeChange(_history, card.Root, free, TimeSpan.FromDays(7));
        var month = FreeSpaceHistory.FreeChange(_history, card.Root, free, TimeSpan.FromDays(30));
        var parts = new List<string>();
        static string Used(long freeDelta) => (freeDelta <= 0 ? "+" : "−") + Fmt.Bytes(Math.Abs(freeDelta));
        if (day is { } d) parts.Add($"{Used(d)} 24h");
        if (week is { } w) parts.Add($"{Used(w)} 7d");
        if (month is { } m) parts.Add($"{Used(m)} 30d");
        card.TrendText = parts.Count > 0 ? "Space used: " + string.Join(" · ", parts) : "";
    }

    private async Task RefreshRecycleBinsAsync()
    {
        var roots = _drives.Where(d => d.Snapshot.Type != DriveType.Network).Select(d => d.Root).ToList();
        var sizes = await Task.Run(() => roots.ToDictionary(r => r, r => ShellOps.QueryRecycleBin(r).Size));
        foreach (var c in _drives)
            if (sizes.TryGetValue(c.Root, out var s)) c.RecycleSize = s;
    }

    private async void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DriveCardVM card) return;
        if (MessageBox.Show(this,
                $"Permanently delete everything in the Recycle Bin on {card.Root.TrimEnd('\\')}?\n\nThis frees {Fmt.Bytes(card.RecycleSize)} and cannot be undone.",
                "Empty Recycle Bin", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        ShowBusy("Emptying Recycle Bin…");
        try { await RunSta(() => ShellOps.EmptyRecycleBin(card.Root, hwnd)); }
        finally { HideBusy(); }
        RefreshDrives();
        SetStatus($"Emptied the Recycle Bin on {card.Root.TrimEnd('\\')}.");
    }

    // ======================================================================
    // Scanning
    // ======================================================================

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var roots = _drives.Where(d => d.Selected).Select(d => d.Root).ToList();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one drive on the cards above.", "Nothing to scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = await RunScanAsync(roots, $"Scanning {string.Join(", ", roots.Select(r => r.TrimEnd('\\')))}");
        if (result is not null) await LoadResultAsync(result);
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dlg.ShowDialog(this) != true) return;
        var result = await RunScanAsync([dlg.FolderName], $"Scanning {dlg.FolderName}");
        if (result is not null) await LoadResultAsync(result);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private ScanOptions BuildScanOptions(IReadOnlyList<string> roots) => new()
    {
        Roots = roots,
        CollectFileMinSize = (long)(Math.Max(1, _settings.BigFileMinMB) * Fmt.MB),
        KindFileMinSize = 1 * Fmt.MB,
        ExcludedPaths = _settings.ExcludedPaths,
    };

    private async Task<ScanResult?> RunScanAsync(IReadOnlyList<string> roots, string label)
    {
        if (_scanning) return null;
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        var progress = new ScanProgress();
        var sw = Stopwatch.StartNew();
        ScanButton.Visibility = Visibility.Collapsed;
        ScanFolderButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            ProgressText.Text = $"{label}…  {Fmt.Count(progress.Files)} files · {Fmt.Count(progress.Dirs)} folders · {Fmt.Bytes(progress.Bytes)} · {sw.Elapsed:m\\:ss}";
            ProgressPath.Text = progress.CurrentPath ?? "";
        };
        timer.Start();
        try
        {
            var opts = BuildScanOptions(roots);
            var token = _scanCts.Token;
            return await Task.Run(() => Scanner.Scan(opts, progress, token));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            timer.Stop();
            _scanning = false;
            ScanButton.Visibility = Visibility.Visible;
            ScanFolderButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadResultAsync(ScanResult result)
    {
        _result = result;
        _current = null;
        _treeRoots.Clear();
        foreach (var r in result.Roots) _treeRoots.Add(new DirVM(r, null));
        TreeEmpty.Visibility = _treeRoots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var card in _drives) card.ScanInfo = "";
        foreach (var root in result.Roots)
        {
            var card = _drives.FirstOrDefault(c => c.Root.Equals(root.Name, StringComparison.OrdinalIgnoreCase));
            if (card is null) continue;
            long hidden = card.Snapshot.Used - root.Size;
            var info = $"Scanned {Fmt.Bytes(root.Size)} in {Fmt.Count(root.TotalFiles)} files";
            if (hidden > Fmt.GB)
                info += $"\n{Fmt.Bytes(hidden)} not visible to the scan (file system metadata, restore points{(_isAdmin ? "" : ", folders needing admin")})";
            else if (hidden < -Fmt.GB)
                info += $"\nFiles add up to {Fmt.Bytes(-hidden)} more than the drive uses (compressed, sparse or hard-linked files)";
            card.ScanInfo = info;
        }

        ShowBusy("Building reports…");
        try { await RecomputeReportsAsync(includeExtensions: true); }
        finally { HideBusy(); }

        if (_treeRoots.Count > 0)
        {
            _treeRoots[0].IsExpanded = true;
            _treeRoots[0].IsSelected = true;
            await ShowFolderAsync(_treeRoots[0].Node);
        }

        var status = $"Scanned {Fmt.Count(result.TotalFiles)} files in {Fmt.Count(result.TotalDirs)} folders in {result.Elapsed.TotalSeconds:0.0}s";
        if (result.Errors > 0) status += $" · {Fmt.Count(result.Errors)} folders couldn't be read{(_isAdmin ? "" : " (try Restart as administrator)")}";
        if (result.CloudOnlyBytes > 0) status += $" · {Fmt.Bytes(result.CloudOnlyBytes)} of cloud-only files ignored";
        if (result.Cancelled) status += " · stopped early: results are partial";
        SetStatus(status);
    }

    // ======================================================================
    // Reports
    // ======================================================================

    private sealed record Reports(
        List<ItemRow> Files, List<ItemRow> Folders, List<ItemRow> Stale, List<ItemRow> Hogs,
        List<ItemRow> Archives, List<ExtRow>? Types, string HogSummary);

    private async Task RecomputeReportsAsync(bool includeExtensions)
    {
        if (_result is null) return;
        ReadReportInputs();
        var roots = _result.Roots.ToList();
        var files = _result.Files.ToList();
        var ext = includeExtensions ? _result.Extensions : null;
        var s = _settings;
        var archiveKind = ArchiveKindFilter();

        var rep = await Task.Run(() =>
        {
            var top = Analysis.LargestFiles(files, MaxListedFiles);
            long maxFile = top.Count > 0 ? top[0].Size : 1;
            var fileRows = top.Select(f => { var r = ItemRow.FromFile(f); r.Percent = 100.0 * f.Size / maxFile; return r; }).ToList();

            var folders = Analysis.LargestFolders(roots, MaxListedFolders);
            long maxFolder = folders.Count > 0 ? folders[0].Size : 1;
            var folderRows = folders.Select(n => FolderRow(n, maxFolder)).ToList();

            var stale = Analysis.StaleFolders(roots, DateTime.UtcNow.AddDays(-s.StaleDays), (long)(s.StaleMinGB * Fmt.GB));
            long maxStale = stale.Count > 0 ? stale[0].Size : 1;
            var staleRows = stale.Select(n => FolderRow(n, maxStale)).ToList();

            var hogs = Analysis.FindHogs(roots, (long)(s.HogMinMB * Fmt.MB));
            var hogRows = hogs.Select(h => new ItemRow(h.Node.FullPath, true, h.Node.Size, h.Node.LastWriteTicks, h.Node.LastAccessTicks, h.Node)
            {
                Kind = h.Rule.Name, Note = h.Rule.Advice, Safety = h.Rule.Safety,
            }).ToList();
            long safe = hogs.Where(h => h.Rule.Safety == Safety.Safe).Sum(h => h.Node.Size);
            long review = hogs.Where(h => h.Rule.Safety != Safety.Safe).Sum(h => h.Node.Size);
            var hogSummary = hogs.Count == 0
                ? "No known space hogs found."
                : $"{hogs.Count} suggestions · {Fmt.Bytes(safe)} marked Safe (caches, temp, rebuildable) · {Fmt.Bytes(review)} to review";

            var archives = BuildArchiveRows(files, s.ArchiveMinAgeDays, archiveKind);

            List<ExtRow>? types = null;
            if (ext is not null)
            {
                long total = Math.Max(1, ext.Values.Sum(x => x.Size));
                types = Analysis.ExtensionsBySize(ext).Take(2000)
                    .Select(x => new ExtRow { Extension = x.Extension, Size = x.Size, Count = x.Count, Percent = 100.0 * x.Size / total })
                    .ToList();
            }
            return new Reports(fileRows, folderRows, staleRows, hogRows, archives, types, hogSummary);
        });

        FilesGrid.ItemsSource = new ObservableCollection<ItemRow>(rep.Files);
        FoldersGrid.ItemsSource = new ObservableCollection<ItemRow>(rep.Folders);
        StaleGrid.ItemsSource = new ObservableCollection<ItemRow>(rep.Stale);
        HogsGrid.ItemsSource = new ObservableCollection<ItemRow>(rep.Hogs);
        ArchivesGrid.ItemsSource = new ObservableCollection<ItemRow>(rep.Archives);
        if (rep.Types is not null) TypesGrid.ItemsSource = rep.Types;
        HogsSummary.Text = rep.HogSummary;
    }

    private static ItemRow FolderRow(DirNode n, long max)
    {
        var r = ItemRow.FromDir(n, note: SafetyGuard.Check(n.FullPath).Message);
        r.Percent = 100.0 * n.Size / Math.Max(1, max);
        return r;
    }

    private static List<ItemRow> BuildArchiveRows(IEnumerable<FileEntry> files, int minAgeDays, FileKind? kind)
    {
        long cutoff = DateTime.UtcNow.AddDays(-minAgeDays).Ticks;
        return files.Where(f => f.Kind != FileKind.Other && (kind is null || f.Kind == kind) && f.LastWriteTicks < cutoff)
            .OrderByDescending(f => f.Size)
            .Select(ItemRow.FromFile)
            .ToList();
    }

    private FileKind? ArchiveKindFilter() => ArchiveKindBox.SelectedIndex switch
    {
        1 => FileKind.Archive,
        2 => FileKind.DiskImage,
        3 => FileKind.Installer,
        4 => FileKind.Leftover,
        _ => null,
    };

    private void ReadReportInputs()
    {
        _settings.StaleDays = ParseInt(StaleDaysBox.Text, _settings.StaleDays, 1);
        _settings.StaleMinGB = ParseDouble(StaleMinBox.Text, _settings.StaleMinGB, 0.01);
        _settings.ArchiveMinAgeDays = ParseInt(ArchiveDaysBox.Text, _settings.ArchiveMinAgeDays, 0);
        _settings.DuplicateMinMB = ParseDouble(DupMinBox.Text, _settings.DuplicateMinMB, 0.01);
    }

    private async void StaleApply_Click(object sender, RoutedEventArgs e)
    {
        ReadReportInputs();
        TrySaveSettings();
        if (_result is null) return;
        ShowBusy("Finding untouched folders…");
        try { await RecomputeReportsAsync(includeExtensions: false); }
        finally { HideBusy(); }
    }

    private void ArchiveApply_Click(object sender, RoutedEventArgs e)
    {
        ReadReportInputs();
        TrySaveSettings();
        if (_result is null) return;
        ArchivesGrid.ItemsSource = new ObservableCollection<ItemRow>(
            BuildArchiveRows(_result.Files, _settings.ArchiveMinAgeDays, ArchiveKindFilter()));
    }

    private void TypesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypesGrid.SelectedItem is not ExtRow x) return;
        Tabs.SelectedIndex = 1;
        FilesToolbar.SetFilter(x.Extension);
    }

    // ======================================================================
    // Duplicates
    // ======================================================================

    private async void DupFind_Click(object sender, RoutedEventArgs e)
    {
        if (_dupCts is not null) { _dupCts.Cancel(); return; }
        if (_result is null)
        {
            MessageBox.Show(this, "Run a scan first.", "Duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ReadReportInputs();
        TrySaveSettings();
        double minMb = Math.Max(_settings.DuplicateMinMB, _settings.BigFileMinMB);
        var files = _result.Files.ToList();
        var p = new DuplicateProgress();
        _dupCts = new CancellationTokenSource();
        var token = _dupCts.Token;
        DupFindButton.Content = "Stop";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => DupStatus.Text = p.BytesTotal > 0
            ? $"{p.Stage}… {p.FilesDone:N0}/{p.FilesTotal:N0} files · {Fmt.Bytes(p.BytesDone)} of {Fmt.Bytes(p.BytesTotal)}"
            : $"{p.Stage}… {p.FilesDone:N0}/{p.FilesTotal:N0} files";
        timer.Start();
        try
        {
            var groups = await Task.Run(() => DuplicateFinder.Find(files, (long)(minMb * Fmt.MB), p, token));
            var rows = new ObservableCollection<ItemRow>();
            int g = 0;
            foreach (var grp in groups)
            {
                g++;
                bool first = true;
                foreach (var f in grp.Files.OrderByDescending(f => f.LastWriteTicks))
                {
                    rows.Add(new ItemRow(f.Path, false, f.Size, f.LastWriteTicks, f.LastAccessTicks)
                    {
                        Group = g, File = f, Note = first ? "newest copy" : "", IsKeeper = first,
                    });
                    first = false;
                }
            }
            DupGrid.ItemsSource = rows;
            DupStatus.Text = groups.Count == 0
                ? $"No duplicates ≥ {minMb:0.#} MB found."
                : $"{groups.Count:N0} {(groups.Count == 1 ? "group" : "groups")} ·{Fmt.Bytes(groups.Sum(x => x.Wasted))} reclaimable by keeping one copy of each";
        }
        catch (OperationCanceledException) { DupStatus.Text = "Stopped."; }
        finally
        {
            timer.Stop();
            _dupCts = null;
            DupFindButton.Content = "Find duplicates";
        }
    }

    private void DupSelect_Click(object sender, RoutedEventArgs e)
    {
        DupGrid.SelectedItems.Clear();
        foreach (var r in DupGrid.Items.OfType<ItemRow>().Where(r => !r.IsKeeper)) DupGrid.SelectedItems.Add(r);
        DupGrid.Focus();
    }

    // ======================================================================
    // Explorer view
    // ======================================================================

    private async void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirVM vm && vm.Node != _current) await ShowFolderAsync(vm.Node);
    }

    private async Task ShowFolderAsync(DirNode node)
    {
        _current = node;
        int token = ++_showToken;
        CurrentPathText.Text = node.FullPath;
        CurrentInfoText.Text = $"{Fmt.Bytes(node.Size)} · {Fmt.Count(node.TotalFiles)} files · {Fmt.Count(node.TotalDirs)} folders · last change {Fmt.Ago(node.LastWriteTicks)}"
                               + ((node.Flags & (NodeFlags.Inaccessible | NodeFlags.Incomplete)) != 0 ? " · ⚠ some contents unreadable" : "");
        UpButton.IsEnabled = node.Parent is not null;

        var rows = node.Children.Select(c => ItemRow.FromDir(c)).ToList();
        var path = node.FullPath;
        var files = await Task.Run(() => ListFiles(path));
        if (token != _showToken) return;
        rows.AddRange(files);
        double total = Math.Max(1, node.Size);
        foreach (var r in rows) r.Percent = 100.0 * r.Size / total;
        rows.Sort((a, b) => b.Size.CompareTo(a.Size));
        ContentsGrid.ItemsSource = new ObservableCollection<ItemRow>(rows);
        BuildTreemap(node, rows);
    }

    private static List<ItemRow> ListFiles(string dir)
    {
        var list = new List<ItemRow>();
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                list.Add(new ItemRow(fi.FullName, false, fi.Length, fi.LastWriteTimeUtc.Ticks, fi.LastAccessTimeUtc.Ticks)
                {
                    Kind = fi.Extension.ToLowerInvariant(),
                });
            }
        }
        catch { }
        if (list.Count > MaxFolderFiles)
            list = list.OrderByDescending(r => r.Size).Take(MaxFolderFiles).ToList();
        return list;
    }

    private void BuildTreemap(DirNode node, List<ItemRow> rows)
    {
        const int maxTop = 400;
        var items = new List<TreemapItem>();
        foreach (var r in rows.Take(maxTop))
        {
            if (r.Size <= 0) continue;
            List<TreemapItem>? kids = null;
            if (r.IsFolder && r.Node is { } n)
            {
                kids = n.Children.Take(40).Where(c => c.Size > 0)
                    .Select(c => new TreemapItem { Label = c.Name, Path = c.FullPath, Size = c.Size, IsFolder = true })
                    .ToList();
                long rest = n.Size - kids.Sum(k => k.Size);
                if (rest > 0) kids.Add(new TreemapItem { Label = "(files)", Path = n.FullPath, Size = rest, IsFolder = false });
            }
            items.Add(new TreemapItem { Label = r.Name, Path = r.Path, Size = r.Size, IsFolder = r.IsFolder, Children = kids });
        }
        long remaining = rows.Skip(maxTop).Sum(r => r.Size);
        if (remaining > 0)
            items.Add(new TreemapItem { Label = $"({rows.Count - maxTop:N0} more items)", Path = node.FullPath, Size = remaining });
        Map.SetItems(items);
    }

    private async void Map_ItemActivated(TreemapItem item)
    {
        if (_result is null) return;
        if (item.IsFolder && TreeOps.Find(_result.Roots, item.Path) is { } n && n != _current)
        {
            await NavigateAsync(n);
            return;
        }
        // A file (or "files" block): select it in the list.
        var row = ContentsGrid.Items.OfType<ItemRow>().FirstOrDefault(r => r.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            ContentsGrid.SelectedItem = row;
            ContentsGrid.ScrollIntoView(row);
        }
        else if (TreeOps.Find(_result.Roots, item.Path) is { } folder && folder != _current)
        {
            await NavigateAsync(folder);
        }
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } p) await NavigateAsync(p);
    }

    private void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) ShellOps.OpenFolder(_current.FullPath);
    }

    private async void RescanCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) await RescanAsync(_current.FullPath);
    }

    private async Task NavigateAsync(DirNode node)
    {
        Tabs.SelectedIndex = 0;
        await ShowFolderAsync(node);
        RevealInTree(node);
    }

    private void RevealInTree(DirNode node)
    {
        var chain = new List<DirNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();

        // Make sure view-models exist down the chain.
        for (int i = 0; i < chain.Count - 1; i++)
        {
            if (chain[i].Tag is not DirVM vm) return;
            vm.IsExpanded = true;
            vm.EnsureLoaded();
        }
        if (chain[^1].Tag is not DirVM target) return;
        target.IsSelected = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            ItemsControl parent = Tree;
            TreeViewItem? container = null;
            foreach (var n in chain)
            {
                if (n.Tag is not DirVM vm) return;
                container = ContainerFor(parent, vm);
                if (container is null) return;
                parent = container;
            }
            if (container is null) return;
            container.IsSelected = true;
            container.BringIntoView();
        });
    }

    private static TreeViewItem? ContainerFor(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem c) return c;
        parent.ApplyTemplate();
        parent.UpdateLayout();
        var presenter = FindChild<ItemsPresenter>(parent);
        if (presenter is null) return null;
        presenter.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0) return null;
        if (VisualTreeHelper.GetChild(presenter, 0) is VirtualizingStackPanel panel)
        {
            int index = parent.Items.IndexOf(item);
            if (index >= 0)
            {
                panel.BringIndexIntoViewPublic(index);
                return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            }
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private void Tree_PreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Tree.SelectedItem is not DirVM vm) return;
        DeleteRows([ItemRow.FromDir(vm.Node)], permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    // ======================================================================
    // Row actions (context menu, keyboard, double-click)
    // ======================================================================

    private List<ItemRow> MenuRows(object sender)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        switch (menu?.PlacementTarget)
        {
            case DataGridRow row:
                if (ItemsControl.ItemsControlFromItemContainer(row) is DataGrid grid)
                {
                    if (!row.IsSelected)
                    {
                        grid.SelectedItems.Clear();
                        row.IsSelected = true;
                    }
                    return grid.SelectedItems.OfType<ItemRow>().ToList();
                }
                return row.Item is ItemRow r ? [r] : [];
            case TreeView when Tree.SelectedItem is DirVM vm:
                return [ItemRow.FromDir(vm.Node)];
            default:
                return [];
        }
    }

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        foreach (var r in MenuRows(sender).Take(10)) OpenItem(r);
    }

    private static void OpenItem(ItemRow r)
    {
        try
        {
            if (r.IsFolder) ShellOps.OpenFolder(r.Path);
            else Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Couldn't open", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        foreach (var r in MenuRows(sender).Take(10)) ShellOps.RevealInExplorer(r.Path);
    }

    private async void Menu_ShowInTree(object sender, RoutedEventArgs e)
    {
        if (_result is null || MenuRows(sender).FirstOrDefault() is not { } r) return;
        var folder = r.IsFolder ? r.Path : r.Folder;
        if (TreeOps.Find(_result.Roots, folder) is not { } node) return;
        await NavigateAsync(node);
        if (!r.IsFolder)
        {
            var row = ContentsGrid.Items.OfType<ItemRow>().FirstOrDefault(x => x.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase));
            if (row is not null) { ContentsGrid.SelectedItem = row; ContentsGrid.ScrollIntoView(row); }
        }
    }

    private void Menu_CopyPath(object sender, RoutedEventArgs e) => CopyPaths(MenuRows(sender));

    private static void CopyPaths(IEnumerable<ItemRow> rows)
    {
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Path));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private async void Menu_Rescan(object sender, RoutedEventArgs e)
    {
        if (MenuRows(sender).FirstOrDefault() is { } r) await RescanAsync(r.IsFolder ? r.Path : r.Folder);
    }

    private void Menu_Recycle(object sender, RoutedEventArgs e) => DeleteRows(MenuRows(sender), permanent: false);
    private void Menu_DeletePermanent(object sender, RoutedEventArgs e) => DeleteRows(MenuRows(sender), permanent: true);

    private async void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as DataGridRow)?.Item is not ItemRow r) return;
        e.Handled = true;
        if (r.IsFolder && _result is not null && TreeOps.Find(_result.Roots, r.Path) is { } node) await NavigateAsync(node);
        else ShellOps.RevealInExplorer(r.Path);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        var rows = grid.SelectedItems.OfType<ItemRow>().ToList();
        switch (e.Key)
        {
            case Key.Delete:
                DeleteRows(rows, permanent: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyPaths(rows);
                e.Handled = true;
                break;
            case Key.Enter when rows.Count == 1:
                if (rows[0].IsFolder && _result is not null && TreeOps.Find(_result.Roots, rows[0].Path) is { } node) _ = NavigateAsync(node);
                else ShellOps.RevealInExplorer(rows[0].Path);
                e.Handled = true;
                break;
            case Key.Back when grid == ContentsGrid && _current?.Parent is { } p:
                _ = NavigateAsync(p);
                e.Handled = true;
                break;
        }
    }

    // ======================================================================
    // Deleting
    // ======================================================================

    /// <summary>Called by the list toolbars.</summary>
    public void DeleteSelected(DataGrid? grid, bool permanent)
    {
        if (grid is null) return;
        DeleteRows(grid.SelectedItems.OfType<ItemRow>().ToList(), permanent);
    }

    private async void DeleteRows(List<ItemRow> rows, bool permanent)
    {
        if (rows.Count == 0)
        {
            SetStatus("Select one or more items first.");
            return;
        }
        // Drop items already covered by a selected parent folder.
        rows = rows.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        rows = rows.Where(r => !rows.Any(o => o != r && o.IsFolder && r.Path.Length > o.Path.Length && TreeOps.IsUnder(r.Path, o.Path))).ToList();

        var blocked = new List<string>();
        var warnings = new List<string>();
        foreach (var r in rows.ToList())
        {
            var check = SafetyGuard.Check(r.Path);
            if (check.Verdict == DeleteVerdict.Block)
            {
                blocked.Add($"• {r.Path}\n    {check.Message}");
                rows.Remove(r);
            }
            else if (check.Verdict == DeleteVerdict.Warn)
            {
                warnings.Add($"• {r.Name}: {check.Message}");
            }
        }
        if (blocked.Count > 0)
            MessageBox.Show(this, "These are protected and won't be deleted:\n\n" + string.Join("\n", blocked.Take(10)),
                "Protected items", MessageBoxButton.OK, MessageBoxImage.Stop);
        if (rows.Count == 0) return;

        long total = rows.Sum(r => r.Size);
        var sb = new StringBuilder();
        sb.AppendLine(permanent
            ? $"Permanently delete {rows.Count:N0} item(s), {Fmt.Bytes(total)}?"
            : $"Move {rows.Count:N0} item(s), {Fmt.Bytes(total)}, to the Recycle Bin?");
        sb.AppendLine();
        foreach (var r in rows.Take(8)) sb.AppendLine($"  {r.Path}  ({r.SizeText})");
        if (rows.Count > 8) sb.AppendLine($"  …and {rows.Count - 8:N0} more");
        sb.AppendLine();
        sb.AppendLine(permanent
            ? "This cannot be undone."
            : "The space is freed when you empty the Recycle Bin. If something is too big for the Recycle Bin, Windows will ask before deleting it permanently.");
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ Heads up:");
            foreach (var w in warnings.Take(6)) sb.AppendLine(w);
        }
        if (MessageBox.Show(this, sb.ToString(), permanent ? "Delete permanently" : "Move to Recycle Bin",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        var paths = rows.Select(r => r.Path).ToList();
        ShowBusy(permanent ? "Deleting…" : "Moving to Recycle Bin…");
        List<DeleteOutcome> outcomes;
        try { outcomes = await RunSta(() => ShellOps.Delete(paths, toRecycleBin: !permanent, hwnd)); }
        finally { HideBusy(); }

        long freed = 0;
        int ok = 0;
        foreach (var o in outcomes.Where(o => o.Succeeded))
        {
            var row = rows.First(r => r.Path == o.Path);
            freed += row.Size;
            ok++;
            ApplyDeleted(row);
        }
        AfterTreeChanged();

        var failed = outcomes.Where(o => !o.Succeeded).ToList();
        if (failed.Count > 0)
            MessageBox.Show(this, "Some items couldn't be deleted:\n\n" +
                                  string.Join("\n", failed.Take(10).Select(f => $"• {f.Path}\n    {f.Error}")) +
                                  (_isAdmin ? "" : "\n\nTip: files in use can't be deleted; protected ones may need 'Restart as administrator'."),
                "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);

        RefreshDrives();
        SetStatus(ok == 0 ? "Nothing was deleted."
            : permanent ? $"Deleted {ok:N0} item(s) and freed {Fmt.Bytes(freed)}."
            : $"Moved {ok:N0} item(s), {Fmt.Bytes(freed)}, to the Recycle Bin. Empty it from the drive card to free the space.");
    }

    /// <summary>Updates the tree, collected files and every list after something was deleted.</summary>
    private void ApplyDeleted(ItemRow row)
    {
        if (_result is not null)
        {
            if (row.IsFolder)
            {
                var node = TreeOps.Find(_result.Roots, row.Path);
                if (node is not null)
                {
                    if (_current is not null && TreeOps.IsUnder(_current.FullPath, row.Path)) _current = node.Parent;
                    var vm = node.Tag as DirVM;
                    if (node.IsRoot)
                    {
                        _result.Roots.Remove(node);
                        if (vm is not null) _treeRoots.Remove(vm);
                    }
                    else
                    {
                        var parent = node.Parent!;
                        TreeOps.Remove(node);
                        if (parent.Tag is DirVM pvm && vm is not null) pvm.Children.Remove(vm);
                        RefreshAncestors(parent);
                    }
                }
            }
            else if (TreeOps.Find(_result.Roots, row.Folder) is { } dir)
            {
                TreeOps.RemoveFile(dir, row.Size);
                RefreshAncestors(dir);
            }
            _result.Files.RemoveAll(f => TreeOps.IsUnder(f.Path, row.Path));
        }

        foreach (var grid in ListGrids())
        {
            if (grid.ItemsSource is not ObservableCollection<ItemRow> coll) continue;
            for (int i = coll.Count - 1; i >= 0; i--)
                if (TreeOps.IsUnder(coll[i].Path, row.Path)) coll.RemoveAt(i);
        }
    }

    private void AfterTreeChanged()
    {
        foreach (var grid in ListGrids())
            if (grid.ItemsSource is ObservableCollection<ItemRow> coll)
                foreach (var r in coll.Where(r => r.IsFolder)) r.RaiseAll();

        if (_current is null) return;
        // If the current folder itself was deleted, step up to the nearest survivor.
        var probe = _current;
        while (probe is not null && !IsAttached(probe)) probe = probe.Parent;
        if (probe is null) return;
        _current = null;
        _ = ShowFolderAsync(probe);
        if (probe.Tag is DirVM vm) vm.IsSelected = true;
    }

    private bool IsAttached(DirNode n)
    {
        var root = n.Root;
        return _result?.Roots.Contains(root) == true && (n.IsRoot || n.Parent!.Children.Contains(n));
    }

    private static void RefreshAncestors(DirNode from)
    {
        for (var a = from; a is not null; a = a.Parent)
            if (a.Tag is DirVM vm) vm.Refresh(children: true);
    }

    private IEnumerable<DataGrid> ListGrids() => [ContentsGrid, FilesGrid, FoldersGrid, StaleGrid, ArchivesGrid, HogsGrid, DupGrid];

    // ======================================================================
    // Rescan a single folder
    // ======================================================================

    private async Task RescanAsync(string path)
    {
        if (_result is null || _scanning) return;
        var node = TreeOps.Find(_result.Roots, path);
        if (node is null) return;
        var fresh = await RunScanAsync([node.FullPath], $"Rescanning {node.FullPath}");
        if (fresh is null || fresh.Roots.Count == 0 || _result is null) return;

        long before = node.Size;
        TreeOps.ReplaceContents(node, fresh.Roots[0]);
        _result.Files.RemoveAll(f => TreeOps.IsUnder(f.Path, node.FullPath));
        _result.Files.AddRange(fresh.Files);
        RefreshAncestors(node);

        ShowBusy("Updating reports…");
        try { await RecomputeReportsAsync(includeExtensions: false); }
        finally { HideBusy(); }
        if (_current is not null && TreeOps.IsUnder(_current.FullPath, node.FullPath))
        {
            var cur = _current;
            _current = null;
            await ShowFolderAsync(IsAttached(cur) ? cur : node);
        }
        RefreshDrives();
        SetStatus($"Rescanned {node.FullPath}: {Fmt.Bytes(before)} → {Fmt.Bytes(node.Size)} in {fresh.Elapsed.TotalSeconds:0.0}s.");
    }

    // ======================================================================
    // Export
    // ======================================================================

    public void ExportCsv(DataGrid? grid)
    {
        if (grid is null || grid.Items.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "CSV file|*.csv", FileName = "disk-space-report.csv" };
        if (dlg.ShowDialog(this) != true) return;
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        var first = grid.Items[0];
        if (first is ExtRow)
        {
            sb.AppendLine("Extension,Bytes,Files");
            foreach (var x in grid.Items.OfType<ExtRow>()) sb.AppendLine($"{Q(x.Extension)},{x.Size},{x.Count}");
        }
        else
        {
            sb.AppendLine("Path,Type,Bytes,Size,Files,LastModified,LastAccessed,Category,Note");
            foreach (var r in grid.Items.OfType<ItemRow>())
                sb.AppendLine(string.Join(",", Q(r.Path), r.IsFolder ? "Folder" : "File", r.Size.ToString(CultureInfo.InvariantCulture), Q(r.SizeText),
                    r.IsFolder ? r.Files.ToString(CultureInfo.InvariantCulture) : "",
                    Q(r.Modified?.ToString("yyyy-MM-dd HH:mm")), Q(r.Accessed?.ToString("yyyy-MM-dd HH:mm")), Q(r.Kind), Q(r.Note)));
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        SetStatus($"Exported {grid.Items.Count:N0} rows to {dlg.FileName}");
    }

    // ======================================================================
    // Admin
    // ======================================================================

    private void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        App.ReleaseSingleInstance();
        try
        {
            Process.Start(new ProcessStartInfo(AppLinks.AnalyzerPath) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            App.ReacquireSingleInstance(); // user said no to the UAC prompt
        }
    }

    // ======================================================================
    // Monitor & settings
    // ======================================================================

    private void PopulateSettings()
    {
        var s = _settings;
        StaleDaysBox.Text = s.StaleDays.ToString(CultureInfo.CurrentCulture);
        StaleMinBox.Text = s.StaleMinGB.ToString(CultureInfo.CurrentCulture);
        ArchiveDaysBox.Text = s.ArchiveMinAgeDays.ToString(CultureInfo.CurrentCulture);
        DupMinBox.Text = s.DuplicateMinMB.ToString(CultureInfo.CurrentCulture);
        IntervalBox.Text = s.CheckIntervalMinutes.ToString(CultureInfo.CurrentCulture);
        RenotifyBox.Text = s.RenotifyHours.ToString(CultureInfo.CurrentCulture);
        RapidDropBox.Text = s.RapidDropGB.ToString(CultureInfo.CurrentCulture);
        MonitorNetworkBox.IsChecked = s.MonitorNetworkDrives;
        MonitorRemovableBox.IsChecked = s.MonitorRemovableDrives;
        BigFileBox.Text = s.BigFileMinMB.ToString(CultureInfo.CurrentCulture);
        HogMinBox.Text = s.HogMinMB.ToString(CultureInfo.CurrentCulture);
        ScanNetworkBox.IsChecked = s.ScanNetworkDrivesByDefault;
        ExcludedBox.Text = string.Join(Environment.NewLine, s.ExcludedPaths);
        AutoStartBox.IsChecked = SafeGet(() => AppLinks.StartsWithWindows);

        UnitColumn.ItemsSource = Enum.GetValues<ThresholdUnit>();
        RulesGrid.ItemsSource = DriveProbe.GetDrives().Select(d =>
        {
            var rule = s.RuleFor(d.Root);
            return new DriveRuleRow
            {
                Drive = d.Letter, Label = d.Label,
                FreeText = $"{Fmt.Bytes(d.Free)} of {Fmt.Bytes(d.Total)} ({d.FreePercent:0}%)",
                Enabled = rule.Enabled, Threshold = rule.Threshold, Unit = rule.Unit,
            };
        }).ToList();
    }

    private static bool SafeGet(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var s = _settings;
        s.CheckIntervalMinutes = ParseInt(IntervalBox.Text, s.CheckIntervalMinutes, 1);
        s.RenotifyHours = ParseDouble(RenotifyBox.Text, s.RenotifyHours, 0.1);
        s.RapidDropGB = ParseDouble(RapidDropBox.Text, s.RapidDropGB, 0);
        s.MonitorNetworkDrives = MonitorNetworkBox.IsChecked == true;
        s.MonitorRemovableDrives = MonitorRemovableBox.IsChecked == true;
        s.BigFileMinMB = ParseDouble(BigFileBox.Text, s.BigFileMinMB, 1);
        s.HogMinMB = ParseDouble(HogMinBox.Text, s.HogMinMB, 1);
        s.ScanNetworkDrivesByDefault = ScanNetworkBox.IsChecked == true;
        s.ExcludedPaths = ExcludedBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        ReadReportInputs();

        if (RulesGrid.ItemsSource is IEnumerable<DriveRuleRow> rows)
        {
            foreach (var row in rows)
            {
                s.DriveRules.RemoveAll(r => r.Drive.TrimEnd('\\').Equals(row.Drive, StringComparison.OrdinalIgnoreCase));
                s.DriveRules.Add(new DriveRule { Drive = row.Drive, Enabled = row.Enabled, Threshold = Math.Max(0, row.Threshold), Unit = row.Unit });
            }
        }

        if (!TrySaveSettings()) return;
        AppLinks.SignalMonitor(AppLinks.MonitorCheckEventName);
        SettingsSaved.Text = AppLinks.IsMonitorRunning()
            ? "  Saved. The monitor picked up the changes."
            : "  Saved. Start the monitor to get low-space alerts.";
        PopulateSettings();
    }

    private bool TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save settings", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void UpdateMonitorStatus()
    {
        bool running = AppLinks.IsMonitorRunning();
        MonitorDot.Fill = running ? Palette.Good : Brushes.Gray;
        MonitorStatus.Text = running ? "Monitor is running in the system tray" : "Monitor is not running";
        StartMonitorButton.IsEnabled = !running;
    }

    private void StartMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (!AppLinks.StartMonitor())
            MessageBox.Show(this, $"Couldn't find {AppLinks.MonitorExe} next to this app.", "Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void TestToast_Click(object sender, RoutedEventArgs e)
    {
        if (!AppLinks.SignalMonitor(AppLinks.MonitorTestEventName)) AppLinks.StartMonitor("--test");
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            AppLinks.StartsWithWindows = AutoStartBox.IsChecked == true;
            if (AutoStartBox.IsChecked == true && !AppLinks.IsMonitorRunning()) AppLinks.StartMonitor();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't change start-up setting", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ======================================================================
    // Helpers
    // ======================================================================

    private static int ParseInt(string text, int fallback, int min) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? Math.Max(min, v) : fallback;

    private static double ParseDouble(string text, double fallback, double min) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? Math.Max(min, v) : fallback;

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    /// <summary>Shell file operations want an STA thread; run them off the UI thread so it stays responsive.</summary>
    private static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
