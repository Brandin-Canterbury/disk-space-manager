using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DiskSpaceManager.Core;

public sealed record DeleteOutcome(string Path, bool Succeeded, string? Error);

/// <summary>Recycle Bin, permanent delete and Explorer integration via the Windows shell.</summary>
public static class ShellOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private const uint SHERB_NOCONFIRMATION = 0x1;

    /// <summary>
    /// Deletes paths one by one. With <paramref name="toRecycleBin"/>, Windows itself warns if an item
    /// is too big for the Recycle Bin and would be deleted permanently. Must run on an STA thread.
    /// </summary>
    public static List<DeleteOutcome> Delete(IEnumerable<string> paths, bool toRecycleBin, IntPtr owner)
    {
        var results = new List<DeleteOutcome>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                results.Add(new(path, true, null)); // already gone
                continue;
            }

            string? error = null;
            if (path.Length < 259)
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0",
                    fFlags = (ushort)(FOF_NOCONFIRMATION | (toRecycleBin ? FOF_ALLOWUNDO | FOF_WANTNUKEWARNING : 0)),
                };
                int rc = SHFileOperation(ref op);
                if (op.fAnyOperationsAborted) error = "Cancelled.";
                else if (rc != 0) error = $"Windows reported error 0x{rc:X}.";
            }
            else if (toRecycleBin)
            {
                error = "Path is too long for the Recycle Bin; use permanent delete.";
            }

            bool stillThere = File.Exists(path) || Directory.Exists(path);
            if (stillThere && !toRecycleBin && error is not "Cancelled.")
            {
                // Fallback for long paths / read-only files the shell refused.
                error = ManagedDelete(path);
                stillThere = File.Exists(path) || Directory.Exists(path);
            }
            results.Add(new(path, !stillThere, stillThere ? error ?? "Some items could not be deleted (in use or access denied)." : null));
        }
        return results;
    }

    private static string? ManagedDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return null;
            }
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static (long Size, long Items) QueryRecycleBin(string root)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(root, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin(string? root, IntPtr owner) =>
        SHEmptyRecycleBin(owner, root, SHERB_NOCONFIRMATION) is 0 or unchecked((int)0x8000FFFF) /* already empty */;

    public static void RevealInExplorer(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
