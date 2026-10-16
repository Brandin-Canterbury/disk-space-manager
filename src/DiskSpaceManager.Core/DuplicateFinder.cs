using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceManager.Core;

public sealed class DuplicateGroup
{
    public required long Size { get; init; }
    public required List<FileEntry> Files { get; init; }
    public long Wasted => Size * (Files.Count - 1);
}

public sealed class DuplicateProgress
{
    internal long bytesDone, bytesTotal, filesDone, filesTotal;
    public long BytesDone => Interlocked.Read(ref bytesDone);
    public long BytesTotal => Interlocked.Read(ref bytesTotal);
    public long FilesDone => Interlocked.Read(ref filesDone);
    public long FilesTotal => Interlocked.Read(ref filesTotal);
    public volatile string Stage = "";
}

/// <summary>
/// Finds identical files: same size → same first/last 64 KB → same full SHA-256.
/// Hard links (same physical file) are collapsed, since deleting one frees nothing.
/// </summary>
public static class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    public static List<DuplicateGroup> Find(IEnumerable<FileEntry> files, long minSize, DuplicateProgress p, CancellationToken ct)
    {
        var bySize = files.Where(f => f.Size >= minSize)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        // Stage 1: cheap fingerprint of both ends of each file.
        p.Stage = "Comparing file headers";
        var candidates = bySize.SelectMany(g => g).ToList();
        p.filesTotal = candidates.Count;
        var partial = new ConcurrentDictionary<FileEntry, (string Key, ulong FileId)>();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, f =>
        {
            var r = PartialHash(f);
            if (r is not null) partial[f] = r.Value;
            Interlocked.Increment(ref p.filesDone);
        });

        var stage2 = new List<List<FileEntry>>();
        foreach (var g in bySize)
        {
            foreach (var sub in g.Where(partial.ContainsKey).GroupBy(f => partial[f].Key))
            {
                // Collapse hard links that point at the same physical file.
                var distinct = sub.GroupBy(f => partial[f].FileId == 0 ? (ulong)f.GetHashCode() : partial[f].FileId)
                    .Select(x => x.First()).ToList();
                if (distinct.Count > 1) stage2.Add(distinct);
            }
        }

        // Stage 2: full hash, only for files whose ends matched and that are bigger than the sampled region.
        p.Stage = "Hashing full contents";
        var needFull = stage2.Where(g => g[0].Size > 2 * EdgeBytes).SelectMany(g => g).ToList();
        p.filesDone = 0;
        p.filesTotal = needFull.Count;
        p.bytesTotal = needFull.Sum(f => f.Size);
        var full = new ConcurrentDictionary<FileEntry, string>();
        Parallel.ForEach(needFull, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, f =>
        {
            var h = FullHash(f, p, ct);
            if (h is not null) full[f] = h;
            Interlocked.Increment(ref p.filesDone);
        });

        var groups = new List<DuplicateGroup>();
        foreach (var g in stage2)
        {
            if (g[0].Size <= 2 * EdgeBytes)
            {
                groups.Add(new DuplicateGroup { Size = g[0].Size, Files = g });
                continue;
            }
            foreach (var sub in g.Where(full.ContainsKey).GroupBy(f => full[f]))
            {
                var list = sub.ToList();
                if (list.Count > 1) groups.Add(new DuplicateGroup { Size = list[0].Size, Files = list });
            }
        }
        groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        p.Stage = "Done";
        return groups;
    }

    private static FileStream Open(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0,
            Options = FileOptions.SequentialScan,
        });

    private static (string, ulong)? PartialHash(FileEntry f)
    {
        try
        {
            using var fs = Open(f.Path);
            var buf = new byte[EdgeBytes * 2];
            int n = fs.ReadAtLeast(buf.AsSpan(0, EdgeBytes), EdgeBytes, throwOnEndOfStream: false);
            if (fs.Length > EdgeBytes * 2)
            {
                fs.Seek(-EdgeBytes, SeekOrigin.End);
                n += fs.ReadAtLeast(buf.AsSpan(n, EdgeBytes), EdgeBytes, throwOnEndOfStream: false);
            }
            else if (fs.Length > EdgeBytes)
            {
                n += fs.ReadAtLeast(buf.AsSpan(n), (int)(fs.Length - EdgeBytes), throwOnEndOfStream: false);
            }
            var hash = Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n)));
            return (hash, FileId(fs.SafeFileHandle));
        }
        catch { return null; }
    }

    private static string? FullHash(FileEntry f, DuplicateProgress p, CancellationToken ct)
    {
        try
        {
            using var fs = Open(f.Path);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = fs.Read(buf)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                h.AppendData(buf, 0, n);
                Interlocked.Add(ref p.bytesDone, n);
            }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    private static ulong FileId(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var i)) return 0;
        // Mix volume serial in so equal indexes on different volumes don't collide.
        return (((ulong)i.IndexHigh << 32) | i.IndexLow) ^ ((ulong)i.VolumeSerial << 48);
    }
}
