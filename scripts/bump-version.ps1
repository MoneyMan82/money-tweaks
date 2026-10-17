# Sets the next Money Tweaks version in Directory.Build.props.
#   update: +0.1   (1.23 -> 1.33)
#   minor:  +0.01  (1.23 -> 1.24)
# Versions are stored as X.YY.0 (the trailing .0 keeps older installs able to update) and shown as X.YY.
param([Parameter(Mandatory)][ValidateSet('update', 'minor')][string]$Kind)
$ErrorActionPreference = 'Stop'
$props = Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props'
$text = Get-Content $props -Raw -Encoding UTF8
if ($text -notmatch '<Version>(\d+)\.(\d+)(?:\.(\d+))?</Version>') { throw 'No <Version> in Directory.Build.props' }
$hundredths = [int]$Matches[1] * 100 + [math]::Min(99, [int]$Matches[2]) + $(if ($Kind -eq 'update') { 10 } else { 1 })
$major = [math]::Floor($hundredths / 100); $minor = $hundredths % 100
$next = '{0}.{1:00}.0' -f $major, $minor
$text = $text -replace '<Version>[^<]+</Version>', "<Version>$next</Version>"
[IO.File]::WriteAllText($props, $text, (New-Object Text.UTF8Encoding($false))) # keep UTF-8 without BOM
Write-Host ("Version is now {0}.{1:00} (tag v{2})" -f $major, $minor, $next)
