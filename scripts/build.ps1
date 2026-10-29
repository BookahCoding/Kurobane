# Reproducible build: restore, build (warnings as errors), test, publish to ./artifacts. No installs, no system changes.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build Kurobane.sln -c $Configuration -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test tests/Kurobane.Tests -c $Configuration --no-build -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish src/Kurobane.App -c $Configuration -r win-x64 --self-contained false -o artifacts/Kurobane -nologo; if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "Published: artifacts/Kurobane/Kurobane.exe"
