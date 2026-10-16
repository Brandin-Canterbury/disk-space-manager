using System.Globalization;

namespace DiskSpaceManager.Core;

public readonly record struct FreeSpaceSample(DateTime Utc, string Drive, long Free, long Total);

/// <summary>
/// Tiny CSV log of free space per drive, written hourly by the monitor, so the analyzer can show
/// "this drive lost 40 GB this week" and point you at what is growing.
/// </summary>
public static class FreeSpaceHistory
{
    public static string FilePath { get; } = Path.Combine(SettingsStore.Folder, "free-space-history.csv");
    private static readonly TimeSpan Retention = TimeSpan.FromDays(120);

    public static void Append(IEnumerable<DriveSnapshot> drives)
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var lines = drives.Select(d => $"{now},{d.Letter},{d.Free},{d.Total}");
        File.AppendAllLines(FilePath, lines);
    }

    public static List<FreeSpaceSample> Read()
    {
        var list = new List<FreeSpaceSample>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            while (sr.ReadLine() is { } line)
            {
                var p = line.Split(',');
                if (p.Length != 4) continue;
                if (DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                    && long.TryParse(p[2], out var free) && long.TryParse(p[3], out var total))
                    list.Add(new FreeSpaceSample(t.ToUniversalTime(), p[1], free, total));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Drops samples older than the retention window. Called at monitor start-up.</summary>
    public static void Prune()
    {
        try
        {
            var keep = Read().Where(s => DateTime.UtcNow - s.Utc < Retention).ToList();
            File.WriteAllLines(FilePath, keep.Select(s =>
                $"{s.Utc.ToString("o", CultureInfo.InvariantCulture)},{s.Drive},{s.Free},{s.Total}"));
        }
        catch { }
    }

    /// <summary>How much free space changed since roughly <paramref name="ago"/> (negative = space was used up).</summary>
    public static long? FreeChange(IReadOnlyList<FreeSpaceSample> samples, string drive, long currentFree, TimeSpan ago)
    {
        var target = DateTime.UtcNow - ago;
        FreeSpaceSample? best = null;
        foreach (var s in samples)
        {
            if (!string.Equals(s.Drive, drive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Utc > target) continue;
            if (best is null || s.Utc > best.Value.Utc) best = s;
        }
        // Require the sample to be reasonably close to the requested point in time.
        if (best is null || target - best.Value.Utc > TimeSpan.FromTicks(ago.Ticks / 2)) return null;
        return currentFree - best.Value.Free;
    }
}
