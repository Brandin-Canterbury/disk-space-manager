namespace DiskSpaceManager.Core;

public enum DeleteVerdict { Ok, Warn, Block }

public readonly record struct SafetyCheck(DeleteVerdict Verdict, string? Message)
{
    public static readonly SafetyCheck Ok = new(DeleteVerdict.Ok, null);
}

/// <summary>Stops the user from deleting things that would break Windows, and warns about risky ones.</summary>
public static class SafetyGuard
{
    private static readonly string WindowsDir = Trim(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string SystemRoot = Trim(Path.GetPathRoot(WindowsDir) ?? @"C:\");
    private static readonly string ProgramFiles = Trim(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static readonly string ProgramFilesX86 = Trim(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    private static readonly string ProgramData = Trim(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    private static readonly string UsersDir = Trim(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users");

    private static readonly string[] AllowedWindowsSubfolders =
        ["Temp", @"SoftwareDistribution\Download", "Minidump", "LiveKernelReports", "Logs", @"Prefetch"];

    private static readonly string[] SystemFiles = ["pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"];

    private static string Trim(string p) => p.TrimEnd('\\');

    private static bool Under(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static SafetyCheck Check(string rawPath)
    {
        string p;
        try { p = Trim(Path.GetFullPath(rawPath)); } catch { return new(DeleteVerdict.Block, "Invalid path."); }
        var root = Trim(Path.GetPathRoot(p) ?? "");
        var name = Path.GetFileName(p);

        if (p.Length <= root.Length) return new(DeleteVerdict.Block, "That's an entire drive.");
        if (p.Contains(@"\System Volume Information", StringComparison.OrdinalIgnoreCase))
            return new(DeleteVerdict.Block, "System Volume Information holds restore points. Manage them in System Protection settings.");
        if (Same(name, "$Recycle.Bin") || p.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
            return new(DeleteVerdict.Block, "Use 'Empty Recycle Bin' on the drive card instead.");

        if (Same(Path.GetDirectoryName(p)?.TrimEnd('\\') ?? "", root) && SystemFiles.Any(f => Same(f, name)))
            return new(DeleteVerdict.Block, name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
                ? "Windows manages this file. To remove it, turn hibernation off: run 'powercfg /h off' as admin."
                : "Windows manages this file (virtual memory). Change it in System › Advanced › Performance › Virtual memory.");

        string[] blockedExact = [WindowsDir, ProgramFiles, ProgramFilesX86, ProgramData, UsersDir,
            SystemRoot + @"\Recovery", SystemRoot + @"\Boot", SystemRoot + @"\EFI"];
        if (blockedExact.Any(b => Same(p, b)))
            return new(DeleteVerdict.Block, "This is a core system folder.");
        if (Same(Path.GetDirectoryName(p)?.TrimEnd('\\') ?? "", UsersDir) && !Same(name, "Public"))
            return new(DeleteVerdict.Block, "This is an entire user profile. Remove accounts via Settings › Accounts.");

        if (Under(p, WindowsDir))
        {
            foreach (var sub in AllowedWindowsSubfolders)
            {
                var allowed = WindowsDir + "\\" + sub;
                if (Same(p, allowed) || Under(p, allowed))
                    return new(DeleteVerdict.Warn, "Windows system cache. Usually needs admin; files in use will be skipped.");
            }
            return new(DeleteVerdict.Block, "Part of Windows. Use Disk Cleanup or Settings › Storage › Storage Sense instead.");
        }

        if (Same(p, SystemRoot + @"\Windows.old"))
            return new(DeleteVerdict.Warn, "Previous Windows install. Disk Cleanup removes it more reliably (needs admin).");
        var firstSegment = p.Length > root.Length + 1 ? p[(root.Length + 1)..].Split('\\')[0] : "";
        if (Under(p, ProgramFiles) || Under(p, ProgramFilesX86)
            || firstSegment.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase))
            return new(DeleteVerdict.Warn, "Installed program. Prefer uninstalling via Settings › Apps so it's cleanly removed.");
        if (Under(p, ProgramData + @"\Package Cache"))
            return new(DeleteVerdict.Warn, "Installer cache. Programs may fail to repair or uninstall without it.");
        if (Under(p, ProgramData))
            return new(DeleteVerdict.Warn, "Shared application data. Some programs may stop working.");
        bool looksLikeCache = p.Contains("cache", StringComparison.OrdinalIgnoreCase)
                              || p.Contains(@"\Temp", StringComparison.OrdinalIgnoreCase)
                              || p.Contains("CrashDumps", StringComparison.OrdinalIgnoreCase);
        if (p.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) && !looksLikeCache)
            return new(DeleteVerdict.Warn, "Application data. The app may lose settings or saved data.");

        return SafetyCheck.Ok;
    }
}
