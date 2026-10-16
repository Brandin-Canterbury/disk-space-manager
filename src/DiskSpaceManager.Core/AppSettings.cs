using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSpaceManager.Core;

public enum ThresholdUnit { GB, Percent }

public sealed class DriveRule
{
    public string Drive { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Threshold { get; set; } = 10;
    public ThresholdUnit Unit { get; set; } = ThresholdUnit.Percent;

    public long ThresholdBytes(long total) => Unit == ThresholdUnit.GB
        ? (long)(Threshold * Fmt.GB)
        : (long)(total * Threshold / 100.0);

    public string Describe() => Unit == ThresholdUnit.GB ? $"{Threshold:0.#} GB" : $"{Threshold:0.#}%";
}

/// <summary>Settings shared by the analyzer and the tray monitor (one JSON file).</summary>
public sealed class AppSettings
{
    // ---- Monitor ----
    public List<DriveRule> DriveRules { get; set; } = [];
    public DriveRule DefaultRule { get; set; } = new() { Threshold = 50, Unit = ThresholdUnit.GB };
    public int CheckIntervalMinutes { get; set; } = 5;
    public double RenotifyHours { get; set; } = 6;
    /// <summary>Alert when a drive loses this many GB within an hour (0 disables).</summary>
    public double RapidDropGB { get; set; } = 10;
    public bool MonitorNetworkDrives { get; set; }
    public bool MonitorRemovableDrives { get; set; } = true;

    // ---- Analyzer ----
    public bool ScanNetworkDrivesByDefault { get; set; }
    public double BigFileMinMB { get; set; } = 10;
    public int StaleDays { get; set; } = 365;
    public double StaleMinGB { get; set; } = 1;
    public double HogMinMB { get; set; } = 50;
    public double DuplicateMinMB { get; set; } = 10;
    public int ArchiveMinAgeDays { get; set; } = 90;
    public List<string> ExcludedPaths { get; set; } = [];

    public DriveRule RuleFor(string driveRoot)
    {
        var letter = driveRoot.TrimEnd('\\');
        return DriveRules.FirstOrDefault(r => string.Equals(r.Drive.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase))
               ?? DefaultRule;
    }
}

public static class SettingsStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskSpaceManager");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt or mid-write: fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
