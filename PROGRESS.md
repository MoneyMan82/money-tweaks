# Money Tweaks – progress log

Resume point for future sessions. Keep it short.

## Done
- Solution: `src/MoneyTweaks.Core` (engine + catalog), `src/MoneyTweaks` (WPF UI), `tests/MoneyTweaks.Tests` (xUnit). .NET 10, WPF.
- Engine: reversible ops (registry, service start type, powercfg, commands, scheduled tasks, Store apps, dynamic registry sets),
  JSON backups per tweak in `%LOCALAPPDATA%\MoneyTweaks\backups`, rollback on partial failure, restore points, logging.
- Catalog: 334 verified tweaks across 9 categories (see tests output `Every_category_is_populated`).

## Decisions
- EMTweaks' full list (356) only exists inside their paid app; public demo shows 52. We did NOT decompile or copy it.
  Catalog is original, built from documented Windows settings. Count is reported honestly.
- No unlock password (owner decision, 2026-10-11).
- Repo: https://github.com/MoneyMan82/money-tweaks (private at time of writing).

- WPF app: dashboard, 9 category pages, Potato Graphics, Game Booster, Input Delay Reducer, Ping Stabilizer,
  crosshair overlay (Ctrl+Alt+X), Startup Apps, Hardware Monitor, Backup & Restore, Activity Log, Settings/About.
  `MoneyTweaks.exe --screenshots <dir>` renders every page to PNG (used for docs/assets/screenshots).
- Release: scripts/build-release.ps1 -> dist/ (self-contained single-file exe, portable zip, Inno Setup installer, SHA256SUMS).
  GitHub Actions: ci.yml (build+test), release.yml (on tag v* -> GitHub Release with assets).
- Website: docs/ (GitHub Pages from main:/docs).

## Gotchas
- Pillow ICOs with PNG-compressed entries produce a "Bad IL format" assembly. Keep bitmap_format="bmp" (branding script in history).
- Git Bash mangles the non-ASCII path (Työpöytä) when launching the apphost directly; use `dotnet MoneyTweaks.dll` or PowerShell.
- netsh/fsutil output is localized; checks use PowerShell properties or registry values instead.

## Next ideas
- Code signing (needs a certificate), more Potato Graphics games, per-game GPU preference picker.

## Session 2026-10-11 (late): potato look + updater loop
- Updater loop ROOT CAUSE: tag v1.2.0 was pushed on the same commit as v1.1.0 (5fe270a) while Directory.Build.props said
  1.1.0, and release.yml built from props, so "v1.2.0" shipped FileVersion 1.1.0 binaries. App (1.1.0) always saw v1.2.0 as newer.
  Fixes: scripts/build-release.ps1 -ReleaseVersion (CI passes the tag) + guards that fail if exe/setup FileVersion != tag;
  UpdateService.DownloadAsync refuses files that aren't newer (UpdateMismatchException); Updater records
  PendingUpdateTag/Version and VerifyPendingUpdate() checks it on next start; failed/mislabeled tags go to SkippedUpdateTag
  (no auto-prompt). Installer runs with /LOG=%TEMP%\MoneyTweaks-update-install.log.
- Released v1.2.1 to rescue installs stuck on 1.1.0. Broken v1.2.0 release still exists on GitHub (delete it in the GitHub UI if you want).
- Potato preview (src/MoneyTweaks/Controls/PotatoPreview.cs): `toy` branch = rounded low-poly illustration. Screenshot mode
  writes potato-preset-*.png for each preset to compare. Fortnite keys found in a real GameUserSettings.ini: [PerformanceMode] MeshQuality, sg.*, bShowGrass, CosmeticStreamingEnabled.
- To release: bump <Version> in Directory.Build.props (keep it equal to the tag), commit, `git tag vX.Y.Z`, `git push origin vX.Y.Z`.

## Session 2026-10-12: premium redesign + performance
- Perf instrumentation: Core/Sys/Perf.cs (`[perf]` log lines >= threshold) + UI stall detector in MainWindow
  (200 ms heartbeat, ignores screenshot captures). Read results in %LOCALAPPDATA%\MoneyTweaks\logs.
- Real lag causes found & fixed: per-check powershell.exe (CommandOp.PrewarmChecks batches by module, parallel, 15 s cache,
  cache cleared on apply/undo); TweakService.RefreshStatesAsync shows registry tweaks first, global gate of 4;
  WinForms Screen in crosshair (now Monitors.All via EnumDisplayMonitors, UseWindowsForms removed);
  Booster process scan off UI thread; HardwareMonitor process count via perf counter, NIC list cached 15 s, WMI temp 5 s;
  Potato preview DispatcherTimer 30 fps only while rotating+active (was CompositionTarget.Rendering 60 fps forever);
  App.SaveSettingsSoon debounce.
- Design system: Themes/Theme.xaml (palette keys unchanged + Violet/Success/Subtle/CardFill/CardRim/HeroFill),
  Controls/Ui.cs (Page eyebrow/icon, ClickCard, Tag, Empty, Loading, ResponsiveGrid, RingGauge/RingArc, StatTile).
  App.ReduceMotion (setting null=auto). MainWindow: ambient layer (BitmapCache, opacity-only animation), sliding nav pill,
  Ctrl+K palette (TweakListView.FocusTweak), toasts ShowToast(text, tone).
- Logo recoloured (scratchpad logo2.py equivalent: blue→violet gradient line, same mark + gold coin). Website palette updated.
- NOT released yet: version still 1.2.1. To ship: set <Version>1.3.0</Version>, commit, tag v1.3.0, push tag.
- Updater: unchanged logic; latest GitHub release = v1.2.1 = app version → no update offered (no loop).

## Session 2026-10-14: Presets
- Core/Features/Presets.cs: TweakPreset, PresetRun (history for exact undo), PresetStore (BuiltIns, atomic Save,
  corrupt-file backup, Validate, Export/Import with format "money-tweaks-presets" v1, 1 MB cap, unknown ids dropped+reported).
- Views/PresetsView.cs (sidebar "Presets", MainWindow.Tools): cards, preview (op list), apply via TweakService.BatchAsync,
  undo last run, duplicate, edit/rename, delete, export, import, inline editor with virtualized grouped picker.
- No keyboard/mouse macros on purpose (anti-cheat/game ToS risk); stated in the UI.
- Tests: tests/PresetTests.cs (11). Apply/undo UI not clicked through by automation (dialogs); engine paths are the tested ones.
