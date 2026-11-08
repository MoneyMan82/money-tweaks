# Competitor knowledge

Owned by competitor-analyst. Entries need a link and the date checked. Items without a date are starting points to
research, not verified facts.

| Tool | Type | License | Notes / gaps to research |
|---|---|---|---|
| WinUtil (Chris Titus Tech) | Tweaks, app installs | MIT (in TWEAK-SOURCES) | Reviewed for 1.79 tweak ideas |
| Win11Debloat (Raphire) | Debloat script | MIT (in TWEAK-SOURCES) | Reviewed for 1.79 |
| Sophia Script | Tweak script | MIT (in TWEAK-SOURCES) | Reviewed for 1.79 |
| O&O ShutUp10++ | Privacy settings | Freeware, closed | Setting names used as a checklist only |
| Optimizer (hellzerg) | Tweaks | GPL-3.0 | Feature list only |
| Process Lasso | Process priority/affinity automation | Commercial (verify) | Compare with Game Booster / Autopilot |
| MSI Afterburner / RTSS | GPU tuning, overlay, frame limiter | Freeware (verify) | Integrated via Afterburner.cs |
| NVIDIA App | Driver, game settings, overlay | Proprietary | Per-game optimal settings vs Potato Graphics |
| Intel PresentMon / CapFrameX | Frame-time capture and analysis | PresentMon MIT (verify); CapFrameX (verify) | Measurement methodology for benchmarks |
| LatencyMon | DPC latency diagnosis | Freeware (verify) | Input lag / stutter diagnosis gap? |
| Autoruns (Sysinternals) | Startup entries | Freeware (verify) | Compare with Startup Apps |
| Razer Cortex | Game booster | Proprietary | Compare with Game Booster |

## Checked 2026-11-08 (competitor-analyst, cycle 2)
- **WinUtil** - MIT (https://github.com/ChrisTitusTech/winutil/blob/main/LICENSE). Top complaint pattern: "Default" and
  "Reset Windows Update" leave `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` values behind (issues #1419,
  #2962, #3116, #4171, #5232, #3859). Config tab enables .NET 3.5 / DirectPlay via DISM (https://winutil.christitus.com/guides/features/).
- **Win11Debloat** - since 2026.05.10 backs up registry settings, has Restore backup and "Detect applied tweaks";
  removed apps are not restored, other accounts need their own revert, older changes have no revert path; Silent and
  Sysprep modes. Don't call it "no undo". https://github.com/Raphire/Win11Debloat/wiki/Reverting-Changes
- **Atlas / AtlasOS** - GPL-3.0 (ideas only); AME Wizard closed, TrustedUninstaller backend MIT; Atlas 0.6 needs a fresh
  install. https://github.com/Atlas-OS/Atlas
- **Winaero Tweaker** - freeware, EULA bars copying/reverse engineering (ideas only); registry backup, import/export since
  0.8, "reset all Group Policy options"; some options permanent (e.g. Defender off). https://winaerotweaker.com/
- **Sophia Script** - each tweak restores Windows defaults, not the user's previous value; no backup feature
  (issue #562). https://github.com/farag2/Sophia-Script-for-Windows/issues/562
