namespace DiskSpaceManager.Core;

public enum Safety { Safe, Review, Caution }

public sealed record HogRule(string Name, string Pattern, Safety Safety, string Advice);

public sealed record HogMatch(HogRule Rule, DirNode Node);

/// <summary>Post-scan reports computed from the tree and the collected files.</summary>
public static class Analysis
{
    public static List<FileEntry> LargestFiles(IEnumerable<FileEntry> files, int max) =>
        files.OrderByDescending(f => f.Size).Take(max).ToList();

    public static List<DirNode> LargestFolders(IEnumerable<DirNode> roots, int max)
    {
        var heap = new PriorityQueue<DirNode, long>();
        foreach (var root in roots)
        foreach (var n in TreeOps.Descendants(root, includeSelf: false))
        {
            if (heap.Count < max) heap.Enqueue(n, n.Size);
            else if (n.Size > heap.Peek().Size) heap.EnqueueDequeue(n, n.Size);
        }
        var list = new List<DirNode>(heap.Count);
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Folders of at least <paramref name="minSize"/> where nothing inside has been modified since
    /// <paramref name="cutoffUtc"/>. Only the outermost such folder is reported, so results don't overlap.
    /// </summary>
    public static List<DirNode> StaleFolders(IEnumerable<DirNode> roots, DateTime cutoffUtc, long minSize)
    {
        long cutoff = cutoffUtc.Ticks;
        var found = new List<DirNode>();
        var stack = new Stack<DirNode>();
        foreach (var r in roots) stack.Push(r);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.Children)
            {
                if (c.Size < minSize) break; // children are sorted by size
                if (c.LastWriteTicks > 0 && c.LastWriteTicks < cutoff) found.Add(c);
                else stack.Push(c);
            }
        }
        found.Sort((a, b) => b.Size.CompareTo(a.Size));
        return found;
    }

    public static List<ExtStat> ExtensionsBySize(Dictionary<string, ExtStat> ext) =>
        ext.Values.OrderByDescending(e => e.Size).ToList();

    public static List<HogMatch> FindHogs(IEnumerable<DirNode> roots, long minSize)
    {
        var matches = new List<HogMatch>();
        var stack = new Stack<DirNode>();
        foreach (var r in roots) stack.Push(r);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var c in n.Children)
            {
                if (c.Size < minSize) break;
                var rule = HogRules.Match(c);
                if (rule is not null) matches.Add(new HogMatch(rule, c)); // outermost match only
                else stack.Push(c);
            }
        }
        matches.Sort((a, b) => b.Node.Size.CompareTo(a.Node.Size));
        return matches;
    }
}

/// <summary>Known folders that commonly hoard space, matched by path suffix.</summary>
public static class HogRules
{
    // Pattern syntax: backslash-separated segments matched against the END of a folder's path.
    // "*" matches any single segment. A leading "?:" anchors the pattern at a drive root.
    public static readonly HogRule[] All =
    [
        new("Temp files", @"AppData\Local\Temp", Safety.Safe, "Temporary files. Safe to delete; files in use will be skipped."),
        new("Windows temp", @"?:\Windows\Temp", Safety.Safe, "System temp files. Needs admin; in-use files are skipped."),
        new("Windows Update downloads", @"?:\Windows\SoftwareDistribution\Download", Safety.Safe, "Already-installed update payloads. Needs admin."),
        new("Previous Windows install", @"?:\Windows.old", Safety.Caution, "Leftover from a Windows upgrade. Best removed with Disk Cleanup › 'Previous Windows installation(s)'."),
        new("Recycle Bin", @"?:\$Recycle.Bin", Safety.Safe, "Use 'Empty Recycle Bin' on the drive card."),
        new("Crash dumps", @"AppData\Local\CrashDumps", Safety.Safe, "Application crash dumps."),
        new("Error reports", @"?:\ProgramData\Microsoft\Windows\WER", Safety.Safe, "Windows Error Reporting archives."),
        new("Error reports", @"AppData\Local\Microsoft\Windows\WER", Safety.Safe, "Windows Error Reporting archives."),
        new("Kernel reports", @"?:\Windows\LiveKernelReports", Safety.Safe, "Kernel diagnostic dumps. Needs admin."),
        new("Minidumps", @"?:\Windows\Minidump", Safety.Safe, "Blue-screen minidumps. Needs admin."),
        new("Internet cache", @"AppData\Local\Microsoft\Windows\INetCache", Safety.Safe, "Legacy browser / WebView cache."),
        new("Chrome cache", @"AppData\Local\Google\Chrome\User Data\*\Cache", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Chrome code cache", @"AppData\Local\Google\Chrome\User Data\*\Code Cache", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Chrome service worker cache", @"AppData\Local\Google\Chrome\User Data\*\Service Worker", Safety.Review, "Offline data for web apps. Rebuilt, but sites may need to re-download."),
        new("Edge cache", @"AppData\Local\Microsoft\Edge\User Data\*\Cache", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Edge code cache", @"AppData\Local\Microsoft\Edge\User Data\*\Code Cache", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Edge service worker cache", @"AppData\Local\Microsoft\Edge\User Data\*\Service Worker", Safety.Review, "Offline data for web apps."),
        new("Brave cache", @"AppData\Local\BraveSoftware\Brave-Browser\User Data\*\Cache", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Firefox cache", @"AppData\Local\Mozilla\Firefox\Profiles\*\cache2", Safety.Safe, "Browser cache, rebuilt automatically."),
        new("Discord cache", @"AppData\Roaming\discord\Cache", Safety.Safe, "Rebuilt automatically."),
        new("VS Code cache", @"AppData\Roaming\Code\Cache", Safety.Safe, "Rebuilt automatically."),
        new("VS Code cached data", @"AppData\Roaming\Code\CachedData", Safety.Safe, "Rebuilt automatically."),
        new("VS Code old extensions", @"AppData\Roaming\Code\CachedExtensionVSIXs", Safety.Safe, "Downloaded extension packages."),
        new("DirectX shader cache", @"AppData\Local\D3DSCache", Safety.Safe, "Rebuilt by games as needed."),
        new("NVIDIA shader cache", @"AppData\Local\NVIDIA\DXCache", Safety.Safe, "Rebuilt by games as needed."),
        new("NVIDIA GL cache", @"AppData\Local\NVIDIA\GLCache", Safety.Safe, "Rebuilt by games as needed."),
        new("NVIDIA installer downloads", @"?:\ProgramData\NVIDIA Corporation\Downloader", Safety.Safe, "Old driver installers."),
        new("Steam shader cache", @"steamapps\shadercache", Safety.Safe, "Rebuilt by Steam as needed."),
        new("Steam downloads (partial)", @"steamapps\downloading", Safety.Review, "Interrupted downloads."),
        new("Installed Steam games", @"steamapps\common", Safety.Review, "Uninstall unused games from Steam instead of deleting."),
        new("npm cache", @"AppData\Local\npm-cache", Safety.Safe, "Or run: npm cache clean --force"),
        new("Yarn cache", @"AppData\Local\Yarn\Cache", Safety.Safe, "Or run: yarn cache clean"),
        new("pnpm store", @"AppData\Local\pnpm\store", Safety.Safe, "Or run: pnpm store prune"),
        new("pip cache", @"AppData\Local\pip\Cache", Safety.Safe, "Or run: pip cache purge"),
        new("NuGet packages", @".nuget\packages", Safety.Safe, "Or run: dotnet nuget locals all --clear"),
        new("Gradle caches", @".gradle\caches", Safety.Safe, "Re-downloaded on next build."),
        new("Maven repository", @".m2\repository", Safety.Safe, "Re-downloaded on next build."),
        new("Cargo registry", @".cargo\registry", Safety.Safe, "Re-downloaded on next build."),
        new("Playwright browsers", @"AppData\Local\ms-playwright", Safety.Safe, "Reinstall with: npx playwright install"),
        new("Hugging Face cache", @".cache\huggingface", Safety.Review, "Downloaded AI models/datasets; re-downloaded on demand."),
        new("Ollama models", @".ollama\models", Safety.Review, "Local LLMs. Remove individually with: ollama rm <model>"),
        new("LM Studio models", @".lmstudio\models", Safety.Review, "Local LLMs."),
        new("Android emulators", @".android\avd", Safety.Review, "Emulator disk images. Manage in Android Studio › Device Manager."),
        new("Docker data", @"AppData\Local\Docker", Safety.Caution, "WSL disk images. Shrink with: docker system prune -a"),
        new("Node.js packages", "node_modules", Safety.Safe, "Restored by npm/yarn/pnpm install. Great target for old projects."),
        new("Python bytecode", "__pycache__", Safety.Safe, "Regenerated automatically."),
        new("Python virtual env", ".venv", Safety.Safe, "Recreate with: python -m venv .venv"),
        new("Visual Studio cache", ".vs", Safety.Safe, "Solution cache; rebuilt when the solution opens."),
        new("Installer package cache", @"?:\ProgramData\Package Cache", Safety.Caution, "Needed to repair/uninstall programs. Don't delete by hand."),
        new("Downloads folder", @"?:\Users\*\Downloads", Safety.Review, "Review old downloads; sort the 'Old archives & installers' tab by age."),
    ];

    private sealed record Compiled(HogRule Rule, string[] Segments, bool Anchored);

    private static readonly Dictionary<string, List<Compiled>> ByLastSegment = Build();

    private static Dictionary<string, List<Compiled>> Build()
    {
        var map = new Dictionary<string, List<Compiled>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in All)
        {
            var segs = r.Pattern.Split('\\');
            bool anchored = segs[0] == "?:";
            if (anchored) segs = segs[1..];
            var last = segs[^1];
            if (!map.TryGetValue(last, out var list)) map[last] = list = [];
            list.Add(new Compiled(r, segs, anchored));
        }
        return map;
    }

    public static HogRule? Match(DirNode node)
    {
        if (!ByLastSegment.TryGetValue(node.Name, out var candidates)) return null;
        foreach (var c in candidates)
        {
            var n = node;
            bool ok = true;
            for (int i = c.Segments.Length - 1; i >= 0; i--)
            {
                if (n is null || n.IsRoot) { ok = false; break; }
                var seg = c.Segments[i];
                if (seg != "*" && !string.Equals(seg, n.Name, StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
                n = n.Parent;
            }
            if (!ok) continue;
            if (c.Anchored && (n is null || !n.IsRoot || n.Name.TrimEnd('\\').Length != 2)) continue;
            return c.Rule;
        }
        return null;
    }
}
