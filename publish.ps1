# Builds a ready-to-run copy of both apps into .\publish
# Requires the .NET 8 Desktop Runtime on the target PC (already installed here).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($p in 'src\DiskSpaceManager.App', 'src\DiskSpaceManager.Monitor') {
    dotnet publish $p -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}
Get-ChildItem $out -Filter *.pdb | Remove-Item
Write-Host "`nDone. Run $out\DiskSpaceManager.exe" -ForegroundColor Green
