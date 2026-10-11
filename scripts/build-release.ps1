<#
.SYNOPSIS
  Builds a Money Tweaks release: tests, self-contained single-file EXE, portable ZIP, Inno Setup installer, SHA-256 checksums.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
  Output goes to .\dist\
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Money Tweaks $version" -ForegroundColor Green

$dist = Join-Path $root 'dist'
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $dist, $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist, $publish | Out-Null

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\MoneyTweaks.Tests') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

dotnet publish (Join-Path $root 'src\MoneyTweaks\MoneyTweaks.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$exe = Join-Path $publish 'MoneyTweaks.exe'
Copy-Item (Join-Path $root 'LICENSE') $publish
Copy-Item (Join-Path $root 'README.md') $publish

# Portable ZIP
$zip = Join-Path $dist "MoneyTweaks-$version-portable-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $exe (Join-Path $dist "MoneyTweaks-$version-win-x64.exe")

# Installer
if (-not $SkipInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -SkipInstaller.' }
    & $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$dist" (Join-Path $root 'installer\MoneyTweaks.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
}

# Checksums (sha256sum-compatible format)
$sums = Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "`nRelease files in $dist" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} -AutoSize
Get-Content (Join-Path $dist 'SHA256SUMS.txt')
