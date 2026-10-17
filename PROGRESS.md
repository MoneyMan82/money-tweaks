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

## Session 2026-10-15 (later): benchmark audit ("1–2 FPS more without tweaks")
- Evidence from the user's saved study (Competitive Fortnite, intensity 3, 2 runs/state, order off,on,off,on):
  off 101.4 / 98.2, on 98.7 / 98.4 FPS. Runs 2–4 within 0.5 FPS; the gap came from the cold first run (always "off").
  Diff −1.25 FPS vs ±3.2 FPS run-to-run threshold = no meaningful difference. Scene was CPU/WPF-bound (GPU ~31%).
- Every switchable tweak in that preset targets something the scene does not use (SyntheticPlan.NotMeasurable), so the
  synthetic test could not show an effect either way. No evidence that any of them hurt.
- Core/Benchmarks/SyntheticPlan.cs (Abba, NotMeasurable); BenchmarkView.Synthetic: Step plan, priming run, SettleAsync,
  "each" mode + RenderPerTweak, headline verdict, real run timestamps; StressScene: GPU temp only after the window.
- TweakAudit.Harmful: adv.large-system-cache disabled (DisabledReason), Risky note on gen.min-processor-state.
- Tests: SyntheticPlanTests (ABBA balance, preset not measurable, user data = NoClearChange, large cache disabled). 89 pass.
- NOT run: a new per-tweak A/B on the user's PC (long GPU stress + temporary setting changes; needs the user's go-ahead and
  admin for HKLM tweaks). No per-tweak "helps/hurts" result exists yet.
- 2026-10-16: verdict threshold now also has a 1% relative floor (BenchmarkStats.RelativeFloor); the user's intensity-5 run
  showed avg frame time 34.9 -> 34.7 ms (-0.6%) as 'better' while FPS (+0.6%) was within noise. Tagged v1.9.0.

## Session 2026-10-16: Gaming Profile Manager (v1.10.0, not tagged)
- Core/Profiles/GamingProfiles.cs: GamingProfile, ProfileSession, ProfileFile (profiles.json: custom profiles, AutoSwitch ids,
  Active session), ProfileStore (BuiltIns, Validate, Usable = TweakStateGuard.Eligible, Export/Import), ProfileEngine
  (Preview, Conflicts, NeedsAdmin, Activate: persist -> power plan -> capture tweak states -> SetAll -> timer/priority;
  Deactivate: Restore tweaks -> release timer -> restore plan; RunningGame).
- Order matters: tweak states are captured after the plan switch, because power-plan settings belong to a plan
  (found by a test: capturing first could have changed the user's High performance plan on restore).
- App/ProfileService.cs: Activate/Deactivate, auto-switch watcher (manual sessions never replaced; manual switch-off
  suppresses re-trigger until the game exits), CheckPendingAsync at startup, Shutdown restore in App.OnExit.
- App/Views/ProfilesView.cs: active banner, profile cards, auto-switch toggle (confirms with full preview), editor.
- Tests: ProfileTests (built-ins valid, validation, import/export, conflicts, session persisted + timer released, process detection). 96 pass.
- Not verified by hand: auto-switch with a real game launch, activating the built-ins (needs admin; changes real settings).

## Session 2026-10-16 (later): library, details, diagnostics
- No reference image arrived with the "million-dollar" request; potato preview left as is (already original toy-like scene,
  labelled illustration, screenshot compare, honest "can't do" list). Resend the image to match it more closely.
- Core/Ops/RegStringFlagOp.cs; Core/Catalog/TweakCatalog.Extra.cs (FortniteExePath, NicKeysWith, Extra());
  Core/Engine/TweakInfo.cs (TweakFacts shown in TweakItem.Details); TweakVerifier case for RegStringFlagOp.
- Bug caught by verification: the PCIe ASPM subgroup GUID was wrong (read as "not present"); now uses SUB_PCIEXPRESS/ASPM aliases.
- Core/Features/NetworkDiagnostics.cs (RunAsync, pure Diagnose); PingView.DiagnosticSection.
- Updater: numeric System.Version comparison confirmed for 1.10.0 vs 1.9.0 (test added).
- Real read-only audit (non-admin): 340 audited, 262 pass, 12 warning, 3 fail (applied 2026-10-11, reverted outside MT),
  63 unverifiable. Startup to first frame 0.54-0.72 s. Tests: 102 pass.
- Not verified this session: real install/upgrade via the installer (would modify the installed app), new tweaks' Apply on
  HKLM (needs admin; the HKCU string-flag path is covered by a round-trip test).
- v1.10.0 was already tagged by the user (c5423e4, Gaming Profiles); this session's work ships as 1.11.0.

## Session 2026-10-17: Stretched Resolution tab (v1.12.0, not tagged; v1.11.0 released)
- Core/Display/DisplayManager.cs: DEVMODE/DISPLAY_DEVICE P/Invoke, Displays(), Test (CDS_TEST), ApplyTemporary (no registry),
  Commit (CDS_UPDATEREGISTRY of the current mode), RevertToSaved (ChangeDisplaySettingsEx(dev, null)), ApplyAndSave,
  AspectLabel (common ratios sorted; a test caught "between 5:4 and 3:2"), Validate; DisplayState (display-state.json:
  Originals, PendingTemporaryDevice, Presets, BuiltIns).
- Core/Display/FortniteResolution.cs: Values/Plan/Current/Apply/Restore/Unlock; backup file-backups/stretched-fortnite-GameUserSettings.ini.
- App/DisplayService.cs: TryModeAsync (test -> remember original -> pending -> temporary -> Countdown -> commit or revert),
  RestoreOriginal, CheckPending (startup, asks), Shutdown (App.OnExit). Countdown dialog (Esc/close/timeout = revert).
- App/Views/StretchedView.cs; nav tag "stretched"; screenshot mode snaps stretched, stretched-desktop, stretched-fortnite.
- Real data on the dev PC: DISPLAY1 Generic PnP 2560x1440@240 (155 modes), DISPLAY2 Lenovo P27q-20 2560x1440@59;
  CDS_TEST 1440x1080 = Ok, 1720x1080 = BadMode on both (needs a custom resolution). Fortnite file at 2560x1440 Fullscreen.
- Tests: DisplayTests (aspect labels, validation, real enumeration + CDS_TEST only, Fortnite plan read-only). 106 pass.
- NOT done automatically: an actual resolution switch, the countdown, and writing Fortnite's file (they change the user's
  screen/game settings; the code paths are built on the same verified calls). Please try Test resolution once by hand.

## Session 2026-10-17 (later): Fortnite Stretched tab (v1.13.0, not tagged; v1.12.0 released)
- Core/Display/FortniteResolution.cs: ConfigOverride (tests only), ConfigPath, CurrentMode, Reset, Writable (clear error on
  access denied); Apply/Restore use it.
- App/Views/FortniteStretchedView.cs (nav "fnstretch"): file card (path, read-only/backup tags, Open folder), resolution
  fields + presets, window mode, lock, Apply / Restore backup / Reset, honest notice and troubleshooting steps.
- Tests: FortniteIniTests on a temp copy (apply + preserve + backup + lock, read-only input, restore, reset, invalid/missing). 110 pass.
- The real Fortnite file was not modified (timestamp unchanged; no backup in the real profile).

## Session 2026-10-17 (website): v1.13.0 tagged; site updated; next version 1.14.0
- docs/index.html: version 1.13.0 in JSON-LD, 340 tweaks (hero/meta/facts/categories), 6 new tool cards, Potato/Ping cards updated,
  16-entry screenshot gallery, recent-releases changelog card. docs/sitemap.xml lastmod 2026-10-17. README count 340.
- Screenshots from screenshot mode, reviewed for personal data: Fortnite path masked; macros page left out (user's own macros).
- 2026-10-17: 2288x1440 preset; FortniteStretchedView.BuildGpuSteps (NVIDIA/AMD steps, vendor from primary display adapter);
  test Stretched_2288x1440_matches_all_resolution_keys_and_locks (all 8 size keys + window mode + read-only). 111 pass.

## Session 2026-10-17 (later): Fortnite auto resolution (v1.15.0); v1.14.0 released
- DisplayState.AutoFortnite (Enabled/Device/Width/Height/Hz/Scaling); DisplayService.StartAutoSwitch/StopAutoSwitch/PollAuto (2 s poll,
  2 misses before revert, refused-once-per-launch toast, PendingTemporaryDevice for crash recovery), started in MainWindow.Loaded.
- FortniteStretchedView.BuildAuto: size, monitor, live driver check, state, toggle (CDS_TEST before enabling), Save size, Try it for 10 s.
- Dev PC: 2288x1440 listed @240 Hz on DISPLAY1 (user created it), CDS_TEST Ok. Not verified with a real Fortnite launch.

## Session 2026-10-17 (evening): Start with Windows + single instance (v1.16.0; v1.15.0 = auto resolution, released by the user)
- Core/Features/AutoStart.cs (HKCU Run "MoneyTweaks" = "<exe>" --background; KeyPath overridable for tests); App: BackgroundStart,
  ClaimSingleInstance (Local\MoneyTweaks.SingleInstance mutex + Local\MoneyTweaks.Show event, --restarted waits 15 s),
  EnsurePath on start. MainWindow: minimized + OnFirstRestore (dashboard + ambient), BringToFront, update check delayed 2 min.
- --restarted added to Admin.RestartElevated, installer [Run] entries, portable UpdateService.Install.
- Verified for real: --background start 450 ms to first frame; second launch handed off ("already running") and the window
  built the dashboard on first open; one instance remained; closed cleanly. AutoStartTests on a scratch key. 113 pass.
- Background process (v1.17.0; the v1.16.0 tag was cut before it): TrayIcon.cs (WinForms NotifyIcon via alias; implicit WinForms usings removed),
  App: OnExplicitShutdown + EnsureHandle + StartupTasksAsync without showing; ExitRequested; MainWindow.OnClosing hides in background
  mode, EnsureOpened builds dashboard, ShowToast -> tray notification when hidden. Verified: no window, single instance, hide on close.
- 1.18.0 Health Center: Core/Health (HealthModel, HealthChecks, SystemFileCheck, SafeRepairs); MonitorView partial + HealthCenterView.cs.
  ProcessRunner.RunStreamingAsync (UTF-16 detection for sfc), MemoryInfo.Commit, scanner Processes() internal for reuse.
  Real run on this PC: 24 results, found the 5 Oct DRIVER_POWER_STATE_FAILURE blue screen; UI rendered off-screen (game running).
- 1.18.0 NVIDIA: Features/NvidiaProfile.cs (NvApi DRS via nvapi_QueryInterface, raw struct buffers, versions checked by driver),
  NvidiaFortnite.cs (whitelist + driver name/value checks, backup, verify, rollback), PotatoNvidiaView.cs. Probe on driver
  617.14: trilinear ON = 1 (not 0xFFFFFFFF), LOD bias range +-128 = 1/8 steps, OGL 0x20403F79 unknown to driver.
  DRS save worked without admin. Write tests opt-in (MT_NVIDIA_WRITE_TESTS=1), use throwaway profiles only.
- 1.19.0 Legacy Potato: NvidiaProfilePresets.cs (presets, snapshot backup JSON+.nip, .nip build/parse/import via NVAPI,
  Inspector run, verify/rollback, rendering-mode and display checks), InspectorMetadata.cs (PEReader resource read),
  NvApi: SettingIdFromName, EnumSettings, SetString, IsGlobalOrBase. PotatoLegacyNvidiaView.cs. Inspector found in
  Downloads (requireAdministrator, interactive .nip import dialog, no silent switch): Inspector import path built but not
  run here (would need UAC + dialog while a game was running). Lesson: render harness must not construct MoneyTweaks.App
  (its startup hands off to / opens the real app); use a plain Application + Theme.xaml.
- 1.20.0 Overlay: Core PresentMonitor.cs (raw ETW: StartTrace/EnableTraceEx2/OpenTrace/ProcessTrace, EVENT_TRACE_LOGFILEW
  448 bytes, EVENT_RECORD offsets pid@12 ts@16 provider@24 id@40; FrameTracker ring per pid, stats anchored to newest
  delivered frame because real-time buffers flush ~1 s late; 8 KB buffers), OverlaySettings.cs (layout, profiles,
  hotkey parse, GameWindow mode), NvApi.GpuTemperature + HardwareSnapshot.GpuTempC. UI: Controls/PerformanceOverlay.cs,
  OverlayService.cs (hotkey on main hwnd, 250 ms refresh, watcher), Views/OverlayView.cs. User is in Performance Log
  Users, so ETW works unelevated here. Off-screen render caught a status-rebuild re-parent bug (fixed).
