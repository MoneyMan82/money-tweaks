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

## Next
- see bottom of this file for the current TODO
