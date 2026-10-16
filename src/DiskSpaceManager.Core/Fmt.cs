namespace DiskSpaceManager.Core;

/// <summary>Human-friendly formatting for sizes, counts and ages.</summary>
public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public const long KB = 1024;
    public const long MB = 1024 * KB;
    public const long GB = 1024 * MB;

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-" + Bytes(-bytes);
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:0} {Units[i]}" : v >= 10 ? $"{v:0.0} {Units[i]}" : $"{v:0.00} {Units[i]}";
    }

    public static string Count(long n) => n.ToString("N0");

    public static string Age(long utcTicks) => utcTicks <= 0 ? "—" : Age(new DateTime(utcTicks, DateTimeKind.Utc));

    public static string Age(DateTime? utc)
    {
        if (utc is null || utc.Value.Year < 1980) return "—";
        var span = DateTime.UtcNow - utc.Value;
        if (span.TotalDays >= 365) return $"{span.TotalDays / 365.25:0.0} years";
        if (span.TotalDays >= 60) return $"{span.TotalDays / 30.44:0} months";
        if (span.TotalDays >= 2) return $"{span.TotalDays:0} days";
        if (span.TotalHours >= 2) return $"{span.TotalHours:0} hours";
        if (span.TotalMinutes >= 2) return $"{span.TotalMinutes:0} minutes";
        return "just now";
    }

    /// <summary>"3 days ago", "just now", or "—".</summary>
    public static string Ago(long utcTicks) => Age(utcTicks) switch
    {
        "—" => "—",
        "just now" => "just now",
        var a => a + " ago",
    };

    public static DateTime? Local(long utcTicks) =>
        utcTicks <= 0 ? null : new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
}
