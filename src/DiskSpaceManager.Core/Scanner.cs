using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskSpaceManager.Core;

public sealed class ScanOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    /// <summary>Files at least this big are remembered individually (largest files, duplicates).</summary>
    public long CollectFileMinSize { get; init; } = 10 * Fmt.MB;
    /// <summary>Archives / installers / leftovers at least this big are remembered.</summary>
    public long KindFileMinSize { get; init; } = 1 * Fmt.MB;
    public IReadOnlyCollection<string> ExcludedPaths { get; init; } = [];
}

/// <summary>Live counters, safe to read from the UI thread while a scan runs.</summary>
public sealed class ScanProgress
{
    internal long files, dirs, bytes, errors;
    public long Files => Interlocked.Read(ref files);
    public long Dirs => Interlocked.Read(ref dirs);
    public long Bytes => Interlocked.Read(ref bytes);
    public long Errors => Interlocked.Read(ref errors);
    public volatile string? CurrentPath;
}

public sealed class ScanResult
{
    public List<DirNode> Roots { get; } = [];
    public List<FileEntry> Files { get; } = [];
    public Dictionary<string, ExtStat> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long TotalFiles { get; internal set; }
    public long TotalDirs { get; internal set; }
    public long Errors { get; internal set; }
    public long SkippedLinks { get; internal set; }
    /// <summary>Bytes in cloud placeholder files (OneDrive "online-only" etc.) that use no local space.</summary>
    public long CloudOnlyBytes { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
    public bool Cancelled { get; internal set; }
    public DateTime ScannedAtUtc { get; internal set; } = DateTime.UtcNow;
}

/// <summary>
/// Multi-threaded directory scanner. Each root gets its own work stack and a thread count
/// tuned for the underlying device (SSDs love parallelism, spinning disks do not).
/// </summary>
public static class Scanner
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly EnumerationOptions EnumOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false, // we want ContinueOnError callbacks so we can flag folders
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private readonly record struct WorkItem(DirNode Node, string Path);

    private sealed class RootJob(DirNode root, string path)
    {
        public readonly DirNode Root = root;
        public readonly string Path = path;
        public readonly BlockingCollection<WorkItem> Queue = new(new ConcurrentStack<WorkItem>());
        public int Pending;
    }

    private sealed class WorkerState
    {
        public readonly List<FileEntry> Files = [];
        public readonly Dictionary<string, ExtStat> Ext = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<WorkItem> NewDirs = [];
        public long Files_, Dirs, Errors, SkippedLinks, CloudBytes;
        public string LastExt = "";
        public ExtStat? LastStat;
    }

    public static ScanResult Scan(ScanOptions opt, ScanProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ScanResult();
        var excluded = new HashSet<string>(
            opt.ExcludedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizeDir),
            StringComparer.OrdinalIgnoreCase);

        var jobs = new List<RootJob>();
        foreach (var raw in opt.Roots)
        {
            var path = NormalizeRoot(raw);
            if (!Directory.Exists(path)) continue;
            var root = new DirNode(path, null);
            try { root.OwnWriteTicks = Directory.GetLastWriteTimeUtc(path).Ticks; } catch { }
            var job = new RootJob(root, path) { Pending = 1 };
            job.Queue.Add(new WorkItem(root, path));
            jobs.Add(job);
            result.Roots.Add(root);
        }

        var states = new List<WorkerState>();
        var threads = new List<Thread>();
        foreach (var job in jobs)
        {
            int n = DriveProbe.RecommendedScanThreads(job.Path);
            for (int i = 0; i < n; i++)
            {
                var st = new WorkerState();
                states.Add(st);
                var t = new Thread(() => Worker(job, st, opt, excluded, progress, ct))
                {
                    IsBackground = true,
                    Name = $"Scan {job.Path} #{i}",
                    Priority = ThreadPriority.BelowNormal,
                };
                threads.Add(t);
            }
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        foreach (var st in states)
        {
            result.Files.AddRange(st.Files);
            foreach (var (k, v) in st.Ext)
            {
                if (!result.Extensions.TryGetValue(k, out var agg))
                    result.Extensions[k] = agg = new ExtStat(k);
                agg.Size += v.Size;
                agg.Count += v.Count;
            }
            result.TotalFiles += st.Files_;
            result.TotalDirs += st.Dirs;
            result.Errors += st.Errors;
            result.SkippedLinks += st.SkippedLinks;
            result.CloudOnlyBytes += st.CloudBytes;
        }

        foreach (var root in result.Roots) TreeOps.Aggregate(root);
        result.Cancelled = ct.IsCancellationRequested;
        result.Elapsed = sw.Elapsed;
        result.ScannedAtUtc = DateTime.UtcNow;
        return result;
    }

    private static void Worker(RootJob job, WorkerState st, ScanOptions opt, HashSet<string> excluded,
        ScanProgress progress, CancellationToken ct)
    {
        try
        {
            foreach (var item in job.Queue.GetConsumingEnumerable(ct))
            {
                try
                {
                    ProcessDir(item, st, opt, excluded, progress);
                    foreach (var child in st.NewDirs)
                    {
                        Interlocked.Increment(ref job.Pending);
                        job.Queue.Add(child, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    item.Node.Flags |= NodeFlags.Inaccessible;
                    st.Errors++;
                    Interlocked.Increment(ref progress.errors);
                }
                finally
                {
                    st.NewDirs.Clear();
                    if (Interlocked.Decrement(ref job.Pending) == 0) job.Queue.CompleteAdding();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // Add after CompleteAdding during cancellation races
    }

    private static void ProcessDir(WorkItem item, WorkerState st, ScanOptions opt, HashSet<string> excluded,
        ScanProgress progress)
    {
        var node = item.Node;
        progress.CurrentPath = item.Path;
        bool inDownloads = item.Path.Contains(@"\Downloads", StringComparison.OrdinalIgnoreCase);
        // Installers/archives inside Windows (e.g. Windows\Installer) are system-managed, not clutter.
        bool classify = !TreeOps.IsUnder(item.Path, WindowsDir);

        long ownSize = 0, maxWrite = 0, maxAccess = 0, cloud = 0;
        int ownFiles = 0;

        using (var e = new DirEnumerator(item.Path, EnumOptions))
        {
            while (e.MoveNext())
            {
                ref readonly var entry = ref e.Entry;
                if (entry.IsDir)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // Junctions/symlinks point at data counted elsewhere (or loop back on themselves).
                        st.SkippedLinks++;
                        continue;
                    }
                    var child = new DirNode(entry.Name, node) { OwnWriteTicks = entry.WriteTicks };
                    (node.ChildList ??= []).Add(child);
                    var full = Path.Join(item.Path, entry.Name);
                    if (excluded.Count > 0 && excluded.Contains(full))
                    {
                        child.Flags |= NodeFlags.Excluded;
                        continue;
                    }
                    st.NewDirs.Add(new WorkItem(child, full));
                }
                else
                {
                    long len = entry.Length;
                    ownFiles++;
                    if (entry.WriteTicks > maxWrite) maxWrite = entry.WriteTicks;
                    if (entry.AccessTicks > maxAccess) maxAccess = entry.AccessTicks;
                    if ((entry.Attributes & CloudOnlyMask) != 0)
                    {
                        cloud += len;
                        continue; // occupies no local disk space
                    }
                    ownSize += len;

                    var ext = ExtensionOf(entry.Name);
                    var stat = LookupExt(st, ext);
                    stat.Size += len;
                    stat.Count++;

                    var kind = classify && len >= opt.KindFileMinSize ? FileEntry.Classify(stat.Extension, inDownloads) : FileKind.Other;
                    if (len >= opt.CollectFileMinSize || kind != FileKind.Other)
                    {
                        st.Files.Add(new FileEntry
                        {
                            Path = Path.Join(item.Path, entry.Name),
                            Size = len,
                            LastWriteTicks = entry.WriteTicks,
                            LastAccessTicks = entry.AccessTicks,
                            CreatedTicks = entry.CreatedTicks,
                            Kind = kind,
                        });
                    }
                }
            }
            if (e.Failed) node.Flags |= NodeFlags.Inaccessible;
        }

        node.OwnSize = ownSize;
        node.OwnFiles = ownFiles;
        node.LastWriteTicks = Math.Max(maxWrite, node.OwnWriteTicks);
        node.LastAccessTicks = maxAccess;

        st.Files_ += ownFiles;
        st.Dirs++;
        st.CloudBytes += cloud;
        if ((node.Flags & NodeFlags.Inaccessible) != 0) { st.Errors++; Interlocked.Increment(ref progress.errors); }
        Interlocked.Add(ref progress.files, ownFiles);
        Interlocked.Add(ref progress.bytes, ownSize);
        Interlocked.Increment(ref progress.dirs);
    }

    private static ReadOnlySpan<char> ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > 16) return "";
        return name.AsSpan(dot);
    }

    private static ExtStat LookupExt(WorkerState st, ReadOnlySpan<char> ext)
    {
        // Files in a folder tend to share an extension; skip the string allocation when it repeats.
        if (st.LastStat is not null && ext.Equals(st.LastExt, StringComparison.OrdinalIgnoreCase))
            return st.LastStat;
        var key = ext.Length == 0 ? "(none)" : ext.ToString().ToLowerInvariant();
        if (!st.Ext.TryGetValue(key, out var stat)) st.Ext[key] = stat = new ExtStat(key);
        st.LastExt = ext.Length == 0 ? "" : key;
        st.LastStat = stat;
        return stat;
    }

    public static string NormalizeRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return root.EndsWith('\\') ? root : root + "\\";
        return full.TrimEnd('\\');
    }

    private static string NormalizeDir(string path)
    {
        try { return Path.GetFullPath(path.Trim()).TrimEnd('\\'); } catch { return path.Trim(); }
    }

    /// <summary>Minimal entry snapshot so the enumerator does no per-entry work we don't need.</summary>
    private struct RawEntry
    {
        public string Name;
        public bool IsDir;
        public FileAttributes Attributes;
        public long Length, WriteTicks, AccessTicks, CreatedTicks;
    }

    private sealed class DirEnumerator(string dir, EnumerationOptions options)
        : FileSystemEnumerator<byte>(dir, options)
    {
        // No initializer: base constructor may set this through ContinueOnError before our ctor body runs.
        public bool Failed;
        private RawEntry _entry;
        public ref readonly RawEntry Entry => ref _entry;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            _entry.Name = entry.FileName.ToString();
            _entry.IsDir = entry.IsDirectory;
            _entry.Attributes = entry.Attributes;
            _entry.Length = entry.IsDirectory ? 0 : entry.Length;
            _entry.WriteTicks = entry.LastWriteTimeUtc.UtcTicks;
            _entry.AccessTicks = entry.IsDirectory ? 0 : entry.LastAccessTimeUtc.UtcTicks;
            _entry.CreatedTicks = entry.IsDirectory ? 0 : entry.CreationTimeUtc.UtcTicks;
            return true;
        }

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;
        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) => false;

        protected override bool ContinueOnError(int error)
        {
            Failed = true;
            return true;
        }
    }
}
