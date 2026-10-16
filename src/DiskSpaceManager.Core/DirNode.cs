using System.Text;

namespace DiskSpaceManager.Core;

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    /// <summary>This folder (or part of it) could not be read.</summary>
    Inaccessible = 1,
    /// <summary>Something below this folder could not be read, so sizes are a lower bound.</summary>
    Incomplete = 2,
    /// <summary>Folder was excluded by settings and not scanned.</summary>
    Excluded = 4,
}

/// <summary>
/// One folder in the scanned tree. Files are not stored individually (that would cost
/// gigabytes of RAM on a full-PC scan); each folder keeps aggregates instead, and large or
/// interesting files are collected separately into <see cref="ScanResult.Files"/>.
/// Fields are public for scan speed; treat them as read-only outside the Core library.
/// </summary>
public sealed class DirNode
{
    public DirNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    /// <summary>Folder name; for a root this is the full root path (e.g. "C:\").</summary>
    public string Name { get; }
    public DirNode? Parent { get; internal set; }
    internal List<DirNode>? ChildList;

    /// <summary>Total bytes of all files below this folder.</summary>
    public long Size;
    /// <summary>Bytes of files directly inside this folder.</summary>
    public long OwnSize;
    public int OwnFiles;
    public long TotalFiles;
    public long TotalDirs;
    /// <summary>Newest modification time of anything inside (UTC ticks).</summary>
    public long LastWriteTicks;
    /// <summary>Newest access time of any file inside (UTC ticks). Only meaningful if NTFS last-access updates are on.</summary>
    public long LastAccessTicks;
    /// <summary>The folder's own modified time (changes when entries are added/removed/renamed).</summary>
    public long OwnWriteTicks;
    public NodeFlags Flags;

    /// <summary>Slot for the UI layer to hang its view-model on.</summary>
    public object? Tag;

    public IReadOnlyList<DirNode> Children => (IReadOnlyList<DirNode>?)ChildList ?? Array.Empty<DirNode>();
    public bool HasChildren => ChildList is { Count: > 0 };
    public bool IsRoot => Parent is null;

    public DirNode Root
    {
        get
        {
            var n = this;
            while (n.Parent is not null) n = n.Parent;
            return n;
        }
    }

    public int Depth
    {
        get
        {
            int d = 0;
            for (var n = Parent; n is not null; n = n.Parent) d++;
            return d;
        }
    }

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var chain = new List<string>(16);
            var n = this;
            while (n.Parent is not null) { chain.Add(n.Name); n = n.Parent; }
            var sb = new StringBuilder(n.Name, 260);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (sb[^1] != '\\') sb.Append('\\');
                sb.Append(chain[i]);
            }
            return sb.ToString();
        }
    }

    public DirNode? FindChild(string name)
    {
        if (ChildList is null) return null;
        foreach (var c in ChildList)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    public override string ToString() => $"{FullPath} ({Fmt.Bytes(Size)})";
}
