# Fails the build if tracked files contain obvious secrets or personal data patterns.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$patterns = @(
    '(?i)api[_-]?key\s*[:=]\s*["''][A-Za-z0-9_\-]{16,}["'']',
    '(?i)bearer\s+[A-Za-z0-9_\-\.]{20,}',
    '(?i)-----BEGIN [A-Z ]*PRIVATE KEY-----',
    '(?i)password\s*[:=]\s*["''][^"'']{8,}["'']'
)
$files = git ls-files | Where-Object { $_ -notmatch '\.(png|ico|jpg|ttf)$' }
$bad = 0
foreach ($f in $files) {
    $text = Get-Content -Raw -LiteralPath $f -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($p in $patterns) {
        if ($text -match $p) {
            Write-Host "POSSIBLE SECRET in ${f}: pattern $p"
            $bad++
        }
    }
}
if ($bad -gt 0) { exit 1 }
Write-Host "Secret scan clean."
