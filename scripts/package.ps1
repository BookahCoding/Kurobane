# Builds the release package the in-app updater expects: artifacts/release/Kurobane-win-x64.zip with Kurobane.exe at
# its root (self-contained .NET 9 x64: no .NET runtime needs to be installed). If -KeyPath is given, also writes the
# detached signature Kurobane-win-x64.zip.sig using YOUR private key. The key stays wherever you keep it; this script
# never copies, prints or stores it, and nothing here uploads or publishes anything. Attach both files to a GitHub
# Release by hand.
# -Installer also builds artifacts/release/Kurobane-Setup-X.Y.Z.exe (Inno Setup 6, per-user) from the same files; set
# $env:ISCC to ISCC.exe if Inno Setup is not in one of its usual folders.
#   ./scripts/package.ps1 -Version v0.5.0 -KeyPath D:\keys\kurobane-release.pem -Installer
param(
  [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Version,
  [string]$KeyPath,
  [string]$Configuration = 'Release',
  [switch]$Installer
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$stage = 'artifacts/release/stage'
$zip = 'artifacts/release/Kurobane-win-x64.zip'
Get-ChildItem artifacts/release -Filter 'Kurobane-Setup-*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
dotnet publish src/Kurobane.App -c $Configuration -r win-x64 --self-contained true -p:Version=$($Version.TrimStart('v')) -o $stage -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
if (-not (Test-Path (Join-Path $stage 'Kurobane.exe'))) { throw 'Kurobane.exe missing from publish output.' }
$notices = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item LICENSE -Destination $notices
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Entry names use '/' as the zip format requires (Windows PowerShell's CreateFromDirectory writes '\', which other tools misread).
$root = (Resolve-Path $stage).Path
$archive = [IO.Compression.ZipFile]::Open((Join-Path (Get-Location) $zip), [IO.Compression.ZipArchiveMode]::Create)
try {
  Get-ChildItem $root -Recurse -File | ForEach-Object {
    $name = $_.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
  }
} finally { $archive.Dispose() }
Write-Host "Package: $zip"
if ($KeyPath) {
  dotnet run --project tools/Kurobane.ReleaseTool -c $Configuration -- sign --key $KeyPath --version $Version --zip $zip
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
} else {
  Write-Host 'No -KeyPath: package is UNSIGNED and cannot be installed by the updater.'
}
if ($Installer) {
  $iscc = @($env:ISCC, (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
  if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found; install it or set $env:ISCC.' }
  $src = (Get-Location).Path
  & $iscc /Q "/DAppVersion=$($Version.TrimStart('v'))" "/DStageDir=$root" "/DOutDir=$(Join-Path $src 'artifacts\release')" "/DSourceRoot=$src" installer/Kurobane.iss
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
  Write-Host "Installer: artifacts/release/Kurobane-Setup-$($Version.TrimStart('v')).exe"
}
