# Builds a fake "PC" folder tree for demos and README screenshots.
# Files are NTFS sparse files: they report large sizes but use almost no real disk space.
param([string]$Root = (Join-Path $PSScriptRoot '..\sample-pc'))

$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.IO; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class Sparse {
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
  public static void Create(string path, long size) {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite)) {
      int r;
      if (!DeviceIoControl(fs.SafeFileHandle, 0x900C4 /*FSCTL_SET_SPARSE*/, IntPtr.Zero, 0, IntPtr.Zero, 0, out r, IntPtr.Zero))
        throw new IOException("Could not mark sparse: " + path);
      // A few unique bytes so equal-sized demo files don't look like duplicates.
      var tag = System.Text.Encoding.UTF8.GetBytes(path);
      fs.Write(tag, 0, tag.Length);
      fs.SetLength(size);
    }
  }
}
"@

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$GB = 1GB; $MB = 1MB
$now = Get-Date

function F([string]$rel, [double]$size, [int]$ageDays = 3) {
    $p = Join-Path $Root $rel
    [Sparse]::Create($p, [long]$size)
    $t = $now.AddDays(-$ageDays).AddMinutes(-(Get-Random -Maximum 600))
    (Get-Item $p).LastWriteTime = $t
    (Get-Item $p).LastAccessTime = $t
}
function Many([string]$dir, [int]$count, [double]$total, [string]$ext, [int]$ageDays = 3) {
    for ($i = 1; $i -le $count; $i++) {
        $size = $total / $count * (0.5 + (Get-Random -Maximum 100) / 100)
        F "$dir\$('{0:x8}' -f (Get-Random))$ext" $size ($ageDays + (Get-Random -Maximum 20))
    }
}

$u = 'Users\Alex'
# Caches & temp (Cleanup suggestions)
Many "$u\AppData\Local\Temp" 60 (7.4 * $GB) '.tmp' 1
Many "$u\AppData\Local\NVIDIA\DXCache" 24 (18.2 * $GB) '.nvph' 2
Many "$u\AppData\Local\npm-cache\_cacache" 80 (3.1 * $GB) '' 30
Many "$u\AppData\Local\pip\Cache\http" 40 (2.6 * $GB) '' 60
Many "$u\.nuget\packages\microsoft.netcore.app" 30 (5.2 * $GB) '.nupkg' 90
Many "$u\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data" 50 (1.9 * $GB) '' 1
Many "$u\AppData\Local\CrashDumps" 4 (2.2 * $GB) '.dmp' 200

# Downloads: old installers, archives, disk images
F "$u\Downloads\Win11_23H2_English_x64.iso" (6.2 * $GB) 700
F "$u\Downloads\ubuntu-22.04-desktop-amd64.iso" (4.7 * $GB) 900
F "$u\Downloads\Unity-2021.3-Setup.exe" (2.9 * $GB) 820
F "$u\Downloads\VisualStudioSetup.exe" (4.1 * $MB) 400
F "$u\Downloads\Blender-3.4-windows-x64.msi" (310 * $MB) 650
F "$u\Downloads\project-assets-final-v2.zip" (8.8 * $GB) 540
F "$u\Downloads\project-assets-final-v3.zip" (9.1 * $GB) 520
F "$u\Downloads\photos-2019.7z" (14.6 * $GB) 1500
F "$u\Downloads\drivers-backup.rar" (1.3 * $GB) 1100

# Big untouched folders
Many "$u\Videos\Raw Footage 2021\Day 1" 40 (92 * $GB) '.mp4' 1200
Many "$u\Videos\Raw Footage 2021\Day 2" 35 (78 * $GB) '.mp4' 1190
Many "$u\Videos\Renders" 12 (21 * $GB) '.mov' 40
Many "$u\Documents\Old Projects\2020 Thesis\data" 25 (11 * $GB) '.csv' 1400
Many "$u\Documents\Old Projects\shop-site\node_modules" 400 (1.8 * $GB) '.js' 1300
Many "$u\Pictures\Phone Backup 2020" 300 (36 * $GB) '.jpg' 1600
Many "$u\Documents\Taxes" 30 (120 * $MB) '.pdf' 100

# Active dev work
Many 'Projects\web-app\node_modules' 500 (2.4 * $GB) '.js' 5
Many 'Projects\web-app\src' 120 (40 * $MB) '.ts' 1
Many 'Projects\ml-experiments\.venv\Lib\site-packages' 300 (6.3 * $GB) '.pyd' 20
Many 'Projects\ml-experiments\checkpoints' 8 (28 * $GB) '.safetensors' 15
Many 'Projects\game-engine\.vs' 20 (3.4 * $GB) '.db' 4

# Games
Many 'Games\SteamLibrary\steamapps\common\Starfield' 60 (126 * $GB) '.ba2' 400
Many 'Games\SteamLibrary\steamapps\common\Cyberpunk 2077' 50 (71 * $GB) '.archive' 30
Many 'Games\SteamLibrary\steamapps\shadercache' 30 (4.1 * $GB) '.foz' 10

# Backups and VMs
F 'Backups\2022-laptop-image.zip' (61 * $GB) 1000
F 'Backups\2023-laptop-image.zip' (64 * $GB) 640
F 'VMs\Ubuntu Dev\ubuntu-dev.vhdx' (48 * $GB) 9
Many 'Media\Movies' 25 (84 * $GB) '.mkv' 300

# Real duplicate files (actual content, small)
$dup = New-Object byte[] (24MB); (New-Object Random 42).NextBytes($dup)
foreach ($p in "$u\Pictures\wedding-video-final.mp4", "$u\Videos\Renders\wedding-video-final.mp4", "Backups\wedding-video-final (copy).mp4") {
    $full = Join-Path $Root $p
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    [IO.File]::WriteAllBytes($full, $dup)
    (Get-Item $full).LastWriteTime = $now.AddDays(-(Get-Random -Maximum 300))
}

# Folder timestamps follow their newest content (deepest first)
Get-ChildItem $Root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
    $newest = Get-ChildItem $_.FullName -Force | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { $_.LastWriteTime = $newest.LastWriteTime }
}
Write-Host "Sample data created at $Root"
