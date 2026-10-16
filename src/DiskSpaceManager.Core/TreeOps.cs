namespace DiskSpaceManager.Core;

/// <summary>Bottom-up aggregation and in-place edits of the scanned tree.</summary>
public static class TreeOps
{
    private static readonly Comparison<DirNode> BySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

    /// <summary>Computes totals for every folder below <paramref name="root"/> (iterative, no recursion limits).</summary>
    public static void Aggregate(DirNode root)
    {
        var stack = new Stack<(DirNode Node, bool Done)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (n, done) = stack.Pop();
            if (!done)
            {
                stack.Push((n, true));
                if (n.ChildList is not null)
                    foreach (var c in n.ChildList) stack.Push((c, false));
                continue;
            }

            long size = n.OwnSize, files = n.OwnFiles, dirs = 0;
            long write = Math.Max(n.LastWriteTicks, n.OwnWriteTicks), access = n.LastAccessTicks;
            if (n.ChildList is not null)
            {
                foreach (var c in n.ChildList)
                {
                    size += c.Size;
                    files += c.TotalFiles;
                    dirs += 1 + c.TotalDirs;
                    if (c.LastWriteTicks > write) write = c.LastWriteTicks;
                    if (c.LastAccessTicks > access) access = c.LastAccessTicks;
                    if ((c.Flags & (NodeFlags.Inaccessible | NodeFlags.Incomplete)) != 0) n.Flags |= NodeFlags.Incomplete;
                }
                n.ChildList.Sort(BySizeDesc);
                n.ChildList.TrimExcess();
            }
            n.Size = size;
            n.TotalFiles = files;
            n.TotalDirs = dirs;
            n.LastWriteTicks = write;
            n.LastAccessTicks = access;
        }
    }

    /// <summary>Finds the node for a full path within the given roots, or null if it was not scanned.</summary>
    public static DirNode? Find(IEnumerable<DirNode> roots, string path)
    {
        var full = Scanner.NormalizeRoot(path);
        foreach (var root in roots)
        {
            var rp = root.Name.TrimEnd('\\');
            if (string.Equals(full.TrimEnd('\\'), rp, StringComparison.OrdinalIgnoreCase)) return root;
            if (!full.StartsWith(rp + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = full[(rp.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            DirNode? n = root;
            foreach (var seg in rest)
            {
                n = n.FindChild(seg);
                if (n is null) break;
            }
            if (n is not null) return n;
        }
        return null;
    }

    /// <summary>Removes a folder from the tree and subtracts its totals from every ancestor.</summary>
    public static void Remove(DirNode node)
    {
        var parent = node.Parent;
        if (parent?.ChildList is null) return;
        parent.ChildList.Remove(node);
        node.Parent = null;
        for (var a = parent; a is not null; a = a.Parent)
        {
            a.Size -= node.Size;
            a.TotalFiles -= node.TotalFiles;
            a.TotalDirs -= 1 + node.TotalDirs;
        }
        Resort(parent);
    }

    /// <summary>Subtracts a deleted file from its folder and ancestors.</summary>
    public static void RemoveFile(DirNode folder, long size)
    {
        folder.OwnSize = Math.Max(0, folder.OwnSize - size);
        folder.OwnFiles = Math.Max(0, folder.OwnFiles - 1);
        for (var a = folder; a is not null; a = a.Parent)
        {
            a.Size = Math.Max(0, a.Size - size);
            a.TotalFiles = Math.Max(0, a.TotalFiles - 1);
        }
        Resort(folder);
    }

    /// <summary>
    /// Replaces <paramref name="target"/>'s contents with a freshly scanned copy (from a rescan of
    /// that folder), keeping the same node object so UI bindings stay valid.
    /// </summary>
    public static void ReplaceContents(DirNode target, DirNode fresh)
    {
        long dSize = fresh.Size - target.Size;
        long dFiles = fresh.TotalFiles - target.TotalFiles;
        long dDirs = fresh.TotalDirs - target.TotalDirs;

        target.ChildList = fresh.ChildList;
        if (target.ChildList is not null)
            foreach (var c in target.ChildList) c.Parent = target;
        target.Size = fresh.Size;
        target.OwnSize = fresh.OwnSize;
        target.OwnFiles = fresh.OwnFiles;
        target.TotalFiles = fresh.TotalFiles;
        target.TotalDirs = fresh.TotalDirs;
        target.LastWriteTicks = fresh.LastWriteTicks;
        target.LastAccessTicks = fresh.LastAccessTicks;
        target.OwnWriteTicks = fresh.OwnWriteTicks;
        target.Flags = fresh.Flags;

        for (var a = target.Parent; a is not null; a = a.Parent)
        {
            a.Size += dSize;
            a.TotalFiles += dFiles;
            a.TotalDirs += dDirs;
            if (target.LastWriteTicks > a.LastWriteTicks) a.LastWriteTicks = target.LastWriteTicks;
        }
        if (target.Parent is not null) Resort(target.Parent);
    }

    /// <summary>Keeps children sorted by size after sizes change along a path.</summary>
    private static void Resort(DirNode from)
    {
        for (var a = from; a is not null; a = a.Parent)
            a.ChildList?.Sort(BySizeDesc);
    }

    public static IEnumerable<DirNode> Descendants(DirNode root, bool includeSelf = true)
    {
        var stack = new Stack<DirNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (includeSelf || n != root) yield return n;
            if (n.ChildList is not null)
                for (int i = n.ChildList.Count - 1; i >= 0; i--) stack.Push(n.ChildList[i]);
        }
    }

    public static bool IsUnder(string path, string folder)
    {
        var f = folder.TrimEnd('\\');
        return path.Equals(f, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
