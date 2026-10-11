## Money Tweaks 1.2.1

Free, open-source Windows 10/11 performance and privacy tweaker. No accounts, ads, telemetry or paid tiers.

**Fixes**
- Fixes the endless "Update now" prompt. The previous v1.2.0 release mistakenly contained 1.1.0 files. This release is
  correctly versioned, and the app now verifies update versions before and after installing.

**New**
- Potato Graphics: Fortnite potato studio with presets, a before/after 3D preview, and a review of every setting
  change before applying. Low meshes (rounded low-poly) use Fortnite's own Performance-mode Meshes setting.

**Downloads**
- `MoneyTweaks-Setup.exe`: installer (per-user by default, no admin needed to install)
- `MoneyTweaks-1.2.1-portable-win-x64.zip`: portable, unzip and run `MoneyTweaks.exe`
- `MoneyTweaks-1.2.1-win-x64.exe`: the single-file app on its own
- `SHA256SUMS.txt`: verify with `Get-FileHash <file> -Algorithm SHA256`

**Notes**
- The binaries are not code-signed, so Windows SmartScreen may warn on first run. Choose *More info → Run anyway*.
- Requires Windows 10 1809+ or Windows 11, x64. The .NET runtime is bundled.
