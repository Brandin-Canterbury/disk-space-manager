using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceManager.Core;

public enum MediaKind { Unknown, Ssd, Hdd, Network, Removable }

/// <summary>Snapshot of one volume, used by the drive cards and the monitor.</summary>
public sealed record DriveSnapshot(
    string Root, string Label, DriveType Type, string Format,
    long Total, long Free, MediaKind Media)
{
    public long Used => Total - Free;
    public double UsedFraction => Total <= 0 ? 0 : (double)Used / Total;
    public double FreePercent => Total <= 0 ? 0 : 100.0 * Free / Total;
    public string Letter => Root.TrimEnd('\\');
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter} {Label}";
}

public static class DriveProbe
{
    public static List<DriveSnapshot> GetDrives(bool includeNetwork = true, bool includeRemovable = true)
    {
        var list = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                if (d.DriveType == DriveType.Network && !includeNetwork) continue;
                if (d.DriveType == DriveType.Removable && !includeRemovable) continue;
                if (!d.IsReady) continue;
                list.Add(new DriveSnapshot(d.Name, SafeLabel(d), d.DriveType, d.DriveFormat,
                    d.TotalSize, d.AvailableFreeSpace, GetMediaKind(d.Name, d.DriveType)));
            }
            catch { /* drive vanished or is locked (BitLocker) */ }
        }
        return list;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch { return ""; }
    }

    private static readonly Dictionary<string, MediaKind> MediaCache = new(StringComparer.OrdinalIgnoreCase);

    public static MediaKind GetMediaKind(string root, DriveType? type = null)
    {
        var letter = Path.GetPathRoot(Path.GetFullPath(root))?.TrimEnd('\\') ?? root;
        lock (MediaCache)
            if (MediaCache.TryGetValue(letter, out var cached)) return cached;

        type ??= new DriveInfo(letter).DriveType;
        var kind = type switch
        {
            DriveType.Network => MediaKind.Network,
            DriveType.Removable => MediaKind.Removable,
            DriveType.Fixed => QuerySeekPenalty(letter) switch
            {
                true => MediaKind.Hdd,
                false => MediaKind.Ssd,
                null => MediaKind.Unknown,
            },
            _ => MediaKind.Unknown,
        };
        lock (MediaCache) MediaCache[letter] = kind;
        return kind;
    }

    public static int RecommendedScanThreads(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return 6;
        int cpu = Environment.ProcessorCount;
        return GetMediaKind(root) switch
        {
            MediaKind.Ssd => Math.Clamp(cpu, 4, 12),
            MediaKind.Hdd => 3,
            MediaKind.Network => 6,
            MediaKind.Removable => 2,
            _ => 4,
        };
    }

    // ---- IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceSeekPenaltyProperty ----

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref StoragePropertyQuery inBuf, int inSize,
        out DeviceSeekPenaltyDescriptor outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <returns>true = spinning disk, false = SSD, null = unknown.</returns>
    private static bool? QuerySeekPenalty(string letter)
    {
        try
        {
            using var h = CreateFile(@"\\.\" + letter, 0, 3 /*read|write share*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var q = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = 0 };
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref q, Marshal.SizeOf<StoragePropertyQuery>(),
                    out var d, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(), out _, IntPtr.Zero))
                return null;
            return d.IncursSeekPenalty != 0;
        }
        catch { return null; }
    }
}
