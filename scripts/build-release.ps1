<#
.SYNOPSIS
  Builds a Money Tweaks release: tests, self-contained single-file EXE, portable ZIP, Inno Setup installer, SHA-256 checksums.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
  Output goes to .\dist\
#>
param(
    [string]$Configuration = 'Release',
    # Release version, e.g. 1.24 or v1.24 (older releases: 1.2.1). CI passes the git tag so binaries always match the tag.
    [string]$ReleaseVersion = '',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$SkipLinux
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($ReleaseVersion) {
    $tagVersion = $ReleaseVersion.TrimStart('v', 'V')
    # Decimal scheme: an update adds 0.1 (1.23 -> 1.33), a minor update adds 0.01 (1.23 -> 1.24). Two decimals always ("1.30").
    if ($tagVersion -notmatch '^\d+\.\d{2}(\.0)?$' -and $tagVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Release version '$ReleaseVersion' must look like 1.24.0 (two decimals, shown as 1.24) or, for older releases, 1.2.3" }
    if ($tagVersion -match '^\d+\.\d{2}$') { $tagVersion += '.0' } # older installs need a three-part version to update
    if ($tagVersion -ne $version) { Write-Warning "Directory.Build.props says $version but the release is $tagVersion; building as $tagVersion." }
    $version = $tagVersion
}
Write-Host "Money Tweaks $version" -ForegroundColor Green
# Release notes come from this version's CHANGELOG.md section (they were a fixed file before, stuck at 1.4.1).
& (Join-Path $PSScriptRoot 'release-notes.ps1') -Version $version

$dist = Join-Path $root 'dist'
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $dist, $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist, $publish | Out-Null

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\MoneyTweaks.Tests') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

dotnet publish (Join-Path $root 'src\MoneyTweaks\MoneyTweaks.csproj') -c $Configuration -r win-x64 --self-contained true -p:Version=$version `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$exe = Join-Path $publish 'MoneyTweaks.exe'
# Guard: the app must report exactly the release version, or in-app updates would loop forever.
$built = (Get-Item $exe).VersionInfo.FileVersion
if (-not $built.StartsWith($version)) { throw "Built MoneyTweaks.exe reports $built but the release is $version" }

# Emergency Rescue: the separate recovery program, next to MoneyTweaks.exe (so the ZIP and the installer carry it) and as
# its own download with a fixed name (releases/latest/download/MoneyTweaks-Rescue.exe). Trimmed single file.
# Not "*-win-x64.exe": versions up to 1.73 take any such asset as the portable app update.
dotnet publish (Join-Path $root 'src\MoneyTweaks.Rescue\MoneyTweaks.Rescue.csproj') -c $Configuration -r win-x64 --self-contained true -p:Version=$version `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Emergency Rescue publish failed' }
$rescue = Join-Path $publish 'MoneyTweaks-Rescue.exe'
$rescueBuilt = (Get-Item $rescue).VersionInfo.FileVersion
if (-not $rescueBuilt.StartsWith($version)) { throw "Built MoneyTweaks-Rescue.exe reports $rescueBuilt but the release is $version" }
Copy-Item (Join-Path $root 'LICENSE') $publish
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $publish   # MPL-2.0 / Apache-2.0 notices for bundled libraries
Copy-Item (Join-Path $root 'README.md') $publish

# Portable ZIP
$zip = Join-Path $dist "MoneyTweaks-$version-portable-win-x64.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
Copy-Item $exe (Join-Path $dist "MoneyTweaks-$version-win-x64.exe")
Copy-Item $rescue (Join-Path $dist 'MoneyTweaks-Rescue.exe')

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
    $setupVersion = (Get-Item (Join-Path $dist 'MoneyTweaks-Setup.exe')).VersionInfo.FileVersion
    if (-not $setupVersion.StartsWith($version)) { throw "MoneyTweaks-Setup.exe reports $setupVersion but the release is $version" }
}

# Money Tweaks for Linux (preview): self-contained linux-x64 build, packed with Unix file modes by scripts/pack-linux.cs.
# Fixed file name (the version is in the folder inside) so releases/latest/download/MoneyTweaks-Linux-x64.tar.gz always works.
if (-not $SkipLinux) {
    if (-not $SkipTests) {
        dotnet test (Join-Path $root 'tests\MoneyTweaks.Linux.Tests') -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Linux tests failed' }
    }
    $linuxPublish = Join-Path $root 'artifacts\publish-linux'
    Remove-Item $linuxPublish -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $root 'src\MoneyTweaks.Linux\MoneyTweaks.Linux.csproj') -c $Configuration -r linux-x64 --self-contained true -p:Version=$version -p:DebugType=none -o $linuxPublish
    if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed' }
    dotnet run --file (Join-Path $PSScriptRoot 'pack-linux.cs') -- $linuxPublish (Join-Path $dist "MoneyTweaks-Linux-x64.tar.gz") $version
    if ($LASTEXITCODE -ne 0) { throw 'Linux packaging failed' }
}

# Checksums (sha256sum-compatible format)
$sums = Get-ChildItem $dist -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "`nRelease files in $dist" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} -AutoSize
Get-Content (Join-Path $dist 'SHA256SUMS.txt')
