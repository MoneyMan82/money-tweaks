# Architecture map

Read with `PROGRESS.md` (decisions and gotchas). Facts here were checked against the code on 2026-11-07.

## Solution (`MoneyTweaks.slnx`, .NET 10, version in `Directory.Build.props`)
| Project | What it is |
|---|---|
| `src/MoneyTweaks.Core` | Engine and logic, no WPF. Folders: Advisor, Benchmarks, Catalog, Cleaner, Community, Crosshair, Discovery, Display, Engine, Evidence, Features, Games, Genome, Health, Lab, Macros, Model, Monitoring, Ops, Pocket, Profiles, Scanner, Sys, Tuning |
| `src/MoneyTweaks` | WPF app (net10.0-windows). Shell `MainWindow.xaml(.cs)`, views mostly code-built in `Views/`, controls in `Controls/`, theme `Themes/Theme.xaml`, app services at the root (AutopilotService, ProfileService, OverlayService, MonitorService, CrosshairService, PocketService, Updater...), `ViewModels/TweakItem.cs` (TweakService: ToggleAsync/BatchAsync) |
| `src/MoneyTweaks.Rescue` | Standalone console recovery exe (`MoneyTweaks-Rescue.exe`), engine only, trimmed |
| `src/MoneyTweaks.Linux`, `.Linux.Core` | Avalonia Linux build |
| `src/MoneyTweaks.Community.Server`, `.Shared` | Community Hub local server and contracts |
| `worker/` | Cloudflare Worker: website (serves `docs/`) and Community/evidence API (D1, Images) |
| `tests/MoneyTweaks.Tests` | xUnit, ~420 tests (Windows). Tests that change AppPaths/registry use `[Collection("registry")]` |
| `tests/MoneyTweaks.Community.Tests`, `tests/MoneyTweaks.Linux.Tests` | Other suites (CI runs all) |

## Tweak engine (most important invariant: every change is reversible and verified)
- `Model/Tweak.cs`: Id, Category (General, Debloating, Networking, Advanced, Services, AppOptimizer, AppRemoval,
  GameProfiles, Bios), Kind (Toggle/Guide), Risk, Warning, MinBuild, Editions, Source, Ops.
- `Ops/`: RegValueOp (RemoveKeyIfCreated, DefaultValue), DynamicRegOp, ServiceStartOp, CommandOp, ScheduledTaskOp,
  AppxRemoveOp, RegStringFlagOp. Each op: Capture / Apply / Undo / Check / Describe.
- `Engine/TweakEngine.cs`: Apply (backup first via BackupStore, apply steps, roll back on failure, read-back),
  Undo, GetState. `BackupStore` = one JSON per tweak in `%LOCALAPPDATA%\MoneyTweaks\backups`, written with
  `Sys/AtomicFile`. `TweakVerifier` = Verify / RoundTrip / RestoreMismatches. Known weaknesses: FINDINGS.md (T-006..T-008).
- Catalog: `Catalog/TweakCatalog*.cs` (Build, Extra, Expanded (1.79, with Source), Debloat, Services, Apps...).
  Helpers T, CU, LM, CUs, LMs, Cmd, Ps.
- UI flow: TweakService.ToggleAsync/BatchAsync -> Compatibility check, Conflict Radar, admin, restore point -> engine.

## Shared systems to reuse (don't rebuild)
SensorHub (Acquire/Release, Last frame, alerts) · OverlayService · GameRuntime / GameLibrary / GameDetector (verified
process identity) · ProfileEngine + ProfileService (gaming sessions, crash recovery) · GameAutopilot · Advisor
(GameCapture, BottleneckEngine, NetworkDiagnostics, OptimizationScanner, CrashDetective, FixMyGame) · BenchmarkStore /
BenchmarkStats / TweakStateGuard · TuningEngine · DisplayManager · ActivityLog / Log · AppPaths · AtomicFile.

## UI entry points (every feature needs both)
- Sidebar + page: `Core/Model/AppNavigation.cs` (`new(tag, icon, title, group, description, keywords)`; groups:
  dashboard, optimize, gaming, monitor, tools, backup, settings) and the page map in `MainWindow.xaml.cs`.
  `page#section` tags open a section of an existing page instead of a new page.
- Ctrl+K palette: built from AppNavigation pages and tweaks (`MainWindow.BuildPaletteItems`), so keywords matter.
- Screenshot mode: `MoneyTweaks.exe --screenshots <dir>` renders every page to PNG.

## Build, CI, release
- Local: `dotnet build MoneyTweaks.slnx -c Release`, `dotnet test tests/MoneyTweaks.Tests -c Release`.
- CI (`.github/workflows/ci.yml`): on push to main and on PRs - Windows build + 3 test suites, Linux publish, Worker tests.
- Release (`release.yml`): on tag `v*` - installer, portable exe+zip, Rescue exe, Linux tar.gz, SHA256SUMS.
- Website: pushing main deploys via Cloudflare Workers Builds; `preview_urls` is on, so pushed branches get previews.
