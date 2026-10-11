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
