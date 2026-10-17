# Writes RELEASE_NOTES.md for a release from its CHANGELOG.md section, so every GitHub release shows its own notes.
# Versions use the decimal scheme (an update adds 0.1, a minor update 0.01): tag v1.24.0 is shown as 1.24.
param(
    [Parameter(Mandatory)][string]$Version,   # build version, e.g. 1.24.0 (or a legacy 1.22.1)
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $root 'RELEASE_NOTES.md' }

$parts = $Version.TrimStart('v', 'V').Split('.')
$display = if ($parts.Count -eq 2 -or ($parts.Count -eq 3 -and $parts[2] -eq '0')) { '{0}.{1:00}' -f [int]$parts[0], [int]$parts[1] } else { $Version.TrimStart('v', 'V') }

# The section whose heading starts with this version ("## 1.24 - date" or "## 1.24.0 - date").
$lines = Get-Content (Join-Path $root 'CHANGELOG.md') -Encoding UTF8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## (\S+)') {
        if ($start -ge 0) { $end = $i; break }
        if ($Matches[1] -eq $display -or $Matches[1] -eq $Version) { $start = $i + 1 }
    }
}
if ($start -lt 0) {
    Write-Warning "CHANGELOG.md has no section for $display; the release notes only link to the changelog."
    $section = @('See CHANGELOG.md in the repository for what changed.')
} else {
    if (-not $end) { $end = $lines.Count }
    $section = $lines[$start..($end - 1)]
}

$full = $Version.TrimStart('v', 'V')
$notes = @(
    "## Money Tweaks $display", '',
    'Free, open-source Windows 10/11 performance and privacy tweaker. No accounts, ads, telemetry or paid tiers.', '',
    '**What''s new**'
) + $section + @(
    '', '**Downloads**',
    '- `MoneyTweaks-Setup.exe`: installer (per-user by default, no admin needed to install)',
    "- ``MoneyTweaks-$full-portable-win-x64.zip``: portable, unzip and run ``MoneyTweaks.exe``",
    "- ``MoneyTweaks-$full-win-x64.exe``: the single-file app on its own",
    '- `SHA256SUMS.txt`: verify with `Get-FileHash <file> -Algorithm SHA256`', '',
    '**Notes**',
    '- Not code-signed, so Windows SmartScreen may warn on first run: More info -> Run anyway.',
    '- Windows 10 1809+ or Windows 11, x64. The .NET runtime is bundled.',
    '- Version numbers: an update adds 0.1, a minor update adds 0.01.'
)
[IO.File]::WriteAllLines($Out, [string[]]$notes, (New-Object Text.UTF8Encoding($false))) # UTF-8 without BOM
if ($env:GITHUB_ENV) { "DISPLAY_VERSION=$display" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8 }
Write-Host "Release notes for $display written to $Out ($($section.Count) changelog lines)"
