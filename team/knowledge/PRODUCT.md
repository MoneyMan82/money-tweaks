# What Money Tweaks already does

Check here (and grep the code) before proposing anything. Source: `src/MoneyTweaks.Core/Model/AppNavigation.cs`,
README.md and CHANGELOG.md, 2026-11-07. Version 1.79: 401 reversible tweaks.

## Pages by sidebar group (tag = page key)
- **Dashboard:** dashboard (Dashboard), dashboard#fix (Fix My Game 2.0: Quick/Deep scan, ranked findings, fixes,
  Verification Lab, history).
- **Optimize:** tweaks (All Tweaks, 9 categories), tweaks#radar (Conflict Radar), tweaks#xray (Tweak X-Ray), presets,
  scanner (Smart Scanner), map (Tweak Map), genome (Performance Genome), cleaner (PC Cleaner), tuning (CPU & GPU Tuning).
- **Gaming:** library (Game Library), profiles (Gaming Profiles), autopilot (Game Autopilot), booster (Game Booster),
  potato (Potato Graphics), input (Input Delay Reducer), ping (Ping Stabilizer), overlay (Performance Overlay),
  crosshair (Custom Crosshair), stretched / fnstretch (Stretched Resolution, Fortnite Stretched), community (Community Hub).
- **Monitor:** monitor (Hardware Monitor), monitor#health (Driver & Windows Health), monitor#crash (Crash Detective),
  benchmark (Test & Benchmark), lab (Reality Lab), advisor (Upgrade Advisor).
- **Tools:** startup (Startup Apps), macros (Gaming Macros), pocket (Pocket Panel - phone remote), logs (Activity Log).
- **Backup:** restore (Backup & Restore), restore#rescue (Emergency Rescue + standalone MoneyTweaks-Rescue.exe).
- **Settings:** about (Settings & About), #appearance, #evidence (result sharing), #hotkeys, #legacy (Legacy UI), #updates.
Services category also hosts Service Nexus (dependency graph). Tweak details show Evidence (Tweak Truth Index).

## Tweak categories (counts are tested by PublishedCountsTests)
General 55 · Debloating 91 · Networking 20 · Advanced 32 · Services 111 · App Optimizer 3 · App Removal 76 ·
Game Profiles 10 · BIOS 3 (guides).

## Other deliverables
Linux build (Avalonia), Emergency Rescue exe, website https://money-tweaks.barkgdd.workers.dev with Community Hub,
in-app updater.

## Product principles
Free forever, no ads/accounts/telemetry · every change reversible and verified · measured, honest results (no promised
FPS) · never weaken security or touch anti-cheat · add/organize/improve, never remove features · users are gamers, many
young, non-technical, on low-end PCs and laptops.
