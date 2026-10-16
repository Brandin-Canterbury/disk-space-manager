namespace DiskSpaceManager.Core;

public enum FileKind : byte
{
    Other,
    Archive,
    DiskImage,
    Installer,
    Leftover,
}

/// <summary>A file worth remembering after the scan: big, or an archive/installer/leftover.</summary>
public sealed class FileEntry
{
    public required string Path { get; init; }
    public long Size { get; internal set; }
    public long LastWriteTicks { get; init; }
    public long LastAccessTicks { get; init; }
    public long CreatedTicks { get; init; }
    public FileKind Kind { get; init; }

    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    public static string KindLabel(FileKind k) => k switch
    {
        FileKind.Archive => "Archive",
        FileKind.DiskImage => "Disk image",
        FileKind.Installer => "Installer",
        FileKind.Leftover => "Dump / backup / temp",
        _ => "",
    };

    private static readonly Dictionary<string, FileKind> KindByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = FileKind.Archive, [".rar"] = FileKind.Archive, [".7z"] = FileKind.Archive,
        [".tar"] = FileKind.Archive, [".gz"] = FileKind.Archive, [".tgz"] = FileKind.Archive,
        [".bz2"] = FileKind.Archive, [".xz"] = FileKind.Archive, [".zst"] = FileKind.Archive,
        [".cab"] = FileKind.Archive, [".lz"] = FileKind.Archive, [".lzma"] = FileKind.Archive,
        [".iso"] = FileKind.DiskImage, [".img"] = FileKind.DiskImage, [".dmg"] = FileKind.DiskImage,
        [".msi"] = FileKind.Installer, [".msix"] = FileKind.Installer, [".msixbundle"] = FileKind.Installer,
        [".appx"] = FileKind.Installer, [".appxbundle"] = FileKind.Installer, [".msu"] = FileKind.Installer,
        [".dmp"] = FileKind.Leftover, [".mdmp"] = FileKind.Leftover, [".hdmp"] = FileKind.Leftover,
        [".bak"] = FileKind.Leftover, [".old"] = FileKind.Leftover, [".tmp"] = FileKind.Leftover,
        [".etl"] = FileKind.Leftover,
    };

    /// <param name="inDownloads">.exe files only count as installers when they sit in a Downloads folder.</param>
    public static FileKind Classify(string ext, bool inDownloads)
    {
        if (KindByExt.TryGetValue(ext, out var k)) return k;
        if (inDownloads && ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return FileKind.Installer;
        return FileKind.Other;
    }
}

public sealed class ExtStat
{
    public ExtStat(string ext) => Extension = ext;
    public string Extension { get; }
    public long Size;
    public long Count;
}
