using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DiskSpaceManager.Core;

namespace DiskSpaceManager.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    /// <summary>Refreshes every binding on this object.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A drive card at the top of the window.</summary>
public sealed class DriveCardVM : Observable
{
    private DriveSnapshot _snap;
    private bool _selected;
    private long _recycleSize;
    private string _trend = "";
    private string _scanInfo = "";

    public DriveCardVM(DriveSnapshot snap, bool selected)
    {
        _snap = snap;
        _selected = selected;
    }

    public DriveSnapshot Snapshot => _snap;
    public string Root => _snap.Root;
    public string DisplayName => _snap.DisplayName;
    public string MediaText => _snap.Media switch
    {
        MediaKind.Ssd => "SSD",
        MediaKind.Hdd => "HDD",
        MediaKind.Network => "Network",
        MediaKind.Removable => "Removable",
        _ => _snap.Type.ToString(),
    };
    public double UsedPercent => _snap.UsedFraction * 100;
    public string FreeText => $"{Fmt.Bytes(_snap.Free)} free of {Fmt.Bytes(_snap.Total)}";
    public Brush BarBrush => _snap.UsedFraction switch
    {
        >= 0.95 => Palette.Danger,
        >= 0.85 => Palette.Warning,
        _ => Palette.Accent,
    };

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public long RecycleSize { get => _recycleSize; set { Set(ref _recycleSize, value); Raise(nameof(RecycleText)); Raise(nameof(HasRecycle)); } }
    public string RecycleText => _recycleSize > 0 ? $"Recycle Bin: {Fmt.Bytes(_recycleSize)}" : "Recycle Bin: empty";
    public bool HasRecycle => _recycleSize > 0;
    public string TrendText { get => _trend; set { Set(ref _trend, value); Raise(nameof(HasTrend)); } }
    public bool HasTrend => _trend.Length > 0;
    public string ScanInfo { get => _scanInfo; set { Set(ref _scanInfo, value); Raise(nameof(HasScanInfo)); } }
    public bool HasScanInfo => _scanInfo.Length > 0;

    public void Update(DriveSnapshot snap)
    {
        _snap = snap;
        RaiseAll();
    }
}

/// <summary>Folder node in the left-hand tree. Children load lazily on expand.</summary>
public sealed class DirVM : Observable
{
    private static readonly DirVM Placeholder = new();
    private bool _expanded, _selected, _loaded;

    private DirVM() { Node = new DirNode("…", null); }

    public DirVM(DirNode node, DirVM? parent)
    {
        Node = node;
        ParentVM = parent;
        node.Tag = this;
        if (node.HasChildren) Children.Add(Placeholder);
    }

    public DirNode Node { get; }
    public DirVM? ParentVM { get; }
    public ObservableCollection<DirVM> Children { get; } = [];

    public string Name => Node.Name;
    public string SizeText => Fmt.Bytes(Node.Size);
    public double Percent => ParentVM is null || ParentVM.Node.Size <= 0 ? 100 : 100.0 * Node.Size / ParentVM.Node.Size;
    public string PercentText => $"{Percent:0.0}%";
    public string Tooltip => $"{Node.FullPath}\n{Fmt.Bytes(Node.Size)} · {Fmt.Count(Node.TotalFiles)} files · {Fmt.Count(Node.TotalDirs)} folders\n" +
                             $"Last modified {Fmt.Ago(Node.LastWriteTicks)}" +
                             ((Node.Flags & (NodeFlags.Inaccessible | NodeFlags.Incomplete)) != 0 ? "\n⚠ Some contents could not be read" : "");
    public bool HasWarning => (Node.Flags & NodeFlags.Inaccessible) != 0;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            if (value) EnsureLoaded();
        }
    }

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        foreach (var c in Node.Children) Children.Add(new DirVM(c, this));
    }

    /// <summary>Re-syncs children with the model after deletes/rescans.</summary>
    public void Refresh(bool children = false)
    {
        RaiseAll();
        if (!children) return;
        if (!_loaded)
        {
            Children.Clear();
            if (Node.HasChildren) Children.Add(Placeholder);
            return;
        }
        Children.Clear();
        foreach (var c in Node.Children) Children.Add(c.Tag as DirVM is { } vm && vm.ParentVM == this ? vm : new DirVM(c, this));
        foreach (var c in Children) c.RaiseAll();
    }
}

/// <summary>A row in any of the list views. Folder rows read live from their tree node.</summary>
public sealed class ItemRow : Observable
{
    private readonly long _size;
    private bool _isKeeper;

    public ItemRow(string path, bool isFolder, long size, long writeTicks, long accessTicks = 0, DirNode? node = null)
    {
        Path = path;
        IsFolder = isFolder;
        _size = size;
        LastWriteTicks = writeTicks;
        LastAccessTicks = accessTicks;
        Node = node;
    }

    public static ItemRow FromDir(DirNode n, string? kind = null, string? note = null) =>
        new(n.FullPath, true, n.Size, n.LastWriteTicks, n.LastAccessTicks, n) { Kind = kind ?? "", Note = note ?? "" };

    public static ItemRow FromFile(FileEntry f) =>
        new(f.Path, false, f.Size, f.LastWriteTicks, f.LastAccessTicks) { Kind = FileEntry.KindLabel(f.Kind), File = f };

    public string Path { get; }
    public bool IsFolder { get; }
    public DirNode? Node { get; }
    public FileEntry? File { get; init; }
    public long LastWriteTicks { get; }
    public long LastAccessTicks { get; }

    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path;
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Icon => IsFolder ? "" : "";
    public long Size => Node?.Size ?? _size;
    public string SizeText => Fmt.Bytes(Size);
    public long Files => Node?.TotalFiles ?? 1;
    public string FilesText => IsFolder ? Fmt.Count(Files) : "";
    public DateTime? Modified => Fmt.Local(LastWriteTicks);
    public DateTime? Accessed => Fmt.Local(LastAccessTicks);
    public string AgeText => Fmt.Age(LastWriteTicks);
    public string AccessAgeText => Fmt.Age(LastAccessTicks);
    public string Kind { get; init; } = "";
    public string Note { get; set; } = "";
    public Safety Safety { get; init; }
    public string SafetyText => Safety switch { Safety.Safe => "Safe", Safety.Review => "Review", _ => "Caution" };
    public Brush SafetyBrush => Safety switch { Safety.Safe => Palette.Good, Safety.Review => Palette.Warning, _ => Palette.Danger };

    /// <summary>Share of the containing folder (explorer view) or of the largest item (lists).</summary>
    public double Percent { get; set; }
    public string PercentText => $"{Percent:0.0}%";

    // Duplicate view
    public int Group { get; init; }
    public bool GroupShade => Group % 2 == 1;
    public bool IsKeeper { get => _isKeeper; set => Set(ref _isKeeper, value); }

    public bool Matches(string filter) =>
        Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || Kind.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

public sealed class ExtRow
{
    public required string Extension { get; init; }
    public long Size { get; init; }
    public long Count { get; init; }
    public double Percent { get; init; }
    public string SizeText => Fmt.Bytes(Size);
    public string CountText => Fmt.Count(Count);
    public string PercentText => $"{Percent:0.0}%";
    public string AverageText => Count > 0 ? Fmt.Bytes(Size / Count) : "";
}

public sealed class DriveRuleRow : Observable
{
    public required string Drive { get; init; }
    public string Label { get; init; } = "";
    public string FreeText { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public double Threshold { get; set; } = 10;
    public ThresholdUnit Unit { get; set; } = ThresholdUnit.Percent;
}

public static class Palette
{
    public static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    public static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
    public static readonly Brush Danger = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
    public static readonly Brush Good = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
