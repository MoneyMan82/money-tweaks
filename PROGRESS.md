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

## Release state (2026-10-14)
- Tags v1.3.0 and v1.4.0 both point at 1e223fc (redesign); their binaries are correctly stamped 1.3.0/1.4.0.
- Presets + Potato screenshot mode (50a1708, b698c57) are NOT in any release yet. Props now 1.4.1; ship with tag v1.4.1.
- Tags must never be moved (no force-push); always release a new version instead.

## Session 2026-10-14: Gaming Macros
- Core/Macros: VirtualKeys (name<->VK), Macro.cs (model, MacroPolicy blocked games + Validate, MacroStore load/save/import/export),
  MacroEngine.cs (IInputSink, SendInputSink scan-code input, MacroPlayer with ReleaseAll, GlobalKeyHook WH_KEYBOARD_LL ignoring
  injected input, MacroManager one-at-a-time + triggers + safety timeout + blocked-game guard, MacroLibrary built-ins).
- App: MacroService (manager, Arm/Disarm hook, Ctrl+Alt+End hotkey, Shutdown on exit), Views/GamingMacrosView.cs (sidebar "Gaming Macros").
- Tests: MacroTests (11) use a RecordingSink, so no real input is sent. Real SendInput into games is NOT verified by automation.
- Version set to 1.5.0 (not tagged).

## Session 2026-10-14 (later): Macro Maker upgrade (v1.6.0, not tagged)
- Core/Macros/ColorTriggers.cs: ColorTrigger (Evaluate, hex), ScreenCapture (GDI BitBlt, cached 512×512 DIB, physical px,
  virtual screen incl. negative coords), ColorWatcher (rising/falling edges, AND/OR, per-macro interval >= 50 ms,
  union capture, blocked-game pause). TriggerKind.Color; Macro.ColorTriggers/ColorLogic/ColorPollMs.
- MacroPlayer.Pause/Resume; MacroManager.ColorGate (key AND colour), OnColorRising/OnColorFalling.
- App: Controls/ScreenPicker.cs (overlay, click=pixel, drag=region, GetCursorPos physical coords);
  MacroService owns the watcher; GamingMacrosView: colour section, Pause button, colour status line, drag-drop rows, step highlight.
- Tests: ColorTriggerTests (fake screen; real capture smoke test) + ColorWatcherCpuTests (non-parallel collection).
- Not verified by automation: the picker UI by hand, drag-drop by mouse, real SendInput into games.

## Session 2026-10-14 (evening): Tweak Testing & Benchmark (v1.6.0, not tagged)
- Core/Engine/TweakAudit.cs: known limitations per tweak (Ineffective/Obsolete/Risky/OneWay/Security) + computed
  duplicates/conflicts. DisabledReason: adv.vbs-hvci (engine refuses Apply; Undo allowed).
- Core/Engine/TweakVerifier.cs: Verify (read-back per op type, backup check, restart-pending, live effect checks),
  RestoreMismatches (used by TweakEngine.Undo), RoundTrip (live test, only on tweaks that are off; not App Removal).
- TweakEngine.Apply: read-back after apply; NotApplied/Partial -> rollback (3 s settle + re-check for command ops).
- CommandOp.ReadCheckOutput/HasCheck/InvalidateCache; DynamicRegOp.Resolve. Every command tweak now has a check.
- Core/Benchmarks: BenchmarkModel (runs, studies, conditions, store benchmarks.json, CSV/JSON export), BenchmarkStats
  (metrics, mean/sd, verdict = beyond max(noise floor, 2*SE) with n>=2 each, condition warnings, PresentMon/CapFrameX
  CSV parser), BenchmarkSampler (HardwareMonitor 1 Hz + ICMP 2 Hz, nvidia-smi temps, power plan, AC, foreground, Fortnite running).
- App: Views/BenchmarkView.cs (audit + studies + history + guide; NumberForm for manual FPS/ping), Test/Benchmark buttons
  on tweak cards, Benchmark button on preset cards, nav entry "benchmark". Screenshot mode snaps benchmark-audit.
- Tests: VerificationTests (registry collection) + BenchmarkStatsTests; opt-in MT_AUDIT_REAL=1 writes %TEMP%/mt-audit-real.txt.
- Real read-only audit on the dev PC (non-admin): 334 audited, 256 pass, 9 warning, 5 fail (applied 2026-10-11, values
  later reverted outside Money Tweaks), 64 unverifiable (60 not present, 2 bcdedit need admin, 2 BIOS).
- Not verified by automation: FPS capture inside Fortnite (no API; CSV/manual by design), Fortnite server ping (manual),
  live round-trip of admin-only tweaks (dev session not elevated), the UI flows by mouse beyond screenshots.

## Session 2026-10-14 (night): Synthetic 3D benchmark (not tagged; v1.7.0 is the latest tag)
- App/Controls/StressScene.cs: SceneWindow on a dedicated STA thread (Dispatcher.Run), Viewport3D scene, seeded Random(1234);
  frames counted when RenderingEventArgs.RenderingTime changes; 3 s warm-up excluded; HardwareMonitor sampled 1 Hz in background;
  animations use Timeline.DesiredFrameRate = display refresh (WPF default caps at 60).
- Core/Benchmarks/TweakStateGuard.cs: Eligible/Capture/SetAll/Restore + synthetic-restore.json pending file.
- App/Views/BenchmarkView.Synthetic.cs (partial): mode chips, preset/intensity/duration/resolution/runs, run loop with
  try/finally restore, results table + FPS chart, CheckPendingSyntheticRestoreAsync (called from MainWindow.Loaded).
  MacroService.EmergencyStopPressed event (Ctrl+Alt+End). BenchmarkRun.FpsSeries. Synthetic studies hide the network steps.
- Tests: TweakStateGuardTests (switch both ways, restore states and original values, pending file, eligibility). 79 pass.
- Verified: build, tests, one 8 s scene run (completed, real frame/CPU/GPU/RAM/GPU-temp values logged). That run happened while
  Fortnite was running, so its numbers are not a valid result. NOT yet verified: FPS above 60 after the DesiredFrameRate change,
  the full Compare flow with real tweak switching by hand.
- Fix (same session): the synthetic benchmark aborted when a preset tweak was already on without a Money Tweaks backup
  (USB selective suspend on the user's High performance plan). Such tweaks are now left as is and listed; PowerCfgOp.DefaultValue
  (USB suspend = 1) for Undo without backup, otherwise a clear message instead of a false read-back error.

## Session 2026-10-15: Smart Optimization Scanner (in 1.8.0, not tagged)
- Core/Scanner/OptimizationScanner.cs: ScanFacts (Collect = read-only), Analyze = pure rules -> Finding records,
  Candidates/Fixable (no Services, no TweakAudit-disabled), ScannerState (Ignored, Fixed -> tweak ids) in scanner.json.
- App/Views/ScannerView.cs: facts card, severity counts, finding cards with Fix…/Undo fix/Ignore, "Fixed by the scanner" list;
  nav tag "scanner"; screenshot mode waits for the scan and snaps scanner-results.
- Tests: ScannerTests (healthy PC = no findings, detection with evidence, laptops not nagged, fix filter, real read-only collect). 85 pass.
- Not verified by hand: clicking Fix/Undo in the UI (engine paths are the same ones covered by existing tests).
