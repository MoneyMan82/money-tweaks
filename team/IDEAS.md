# Ideas backlog

Statuses: new · researching · designed · approved · in progress · done · rejected (with reason).
Every idea needs evidence, a fit with existing systems, and a safety note. The idea-critic adds `score NN/50, VERDICT`
(rubric in SCORING.md) to the Notes; the coordinator builds the highest-scoring BUILD idea. Only the owner approves merges.

| Id | Idea | Origin | Value | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| I-001 | Code-sign the installer, portable exe and Rescue exe | PROGRESS.md "Next ideas" | Fewer SmartScreen warnings, trust | M | new | Needs a certificate; costs money unless a free signing option exists (research). Must stay free for users. |
| I-002 | More games in Potato Graphics | PROGRESS.md "Next ideas" | Low-end PCs | M per game | new | Needs each game's real config format; never touch anti-cheat-protected files. |
| I-003 | Per-game GPU preference picker (iGPU/dGPU) | PROGRESS.md "Next ideas" | Laptops with two GPUs | S-M | new | Windows stores this per exe in HKCU `UserGpuPreferences`; reuse GameLibrary. Fortnite already has a dGPU tweak. |
| I-004 | Elevated round-trip test mode for admin tweaks | 1.79 verification gap | Proves HKLM tweaks restore exactly | S | new | Admin tweaks were only verified read-only in 1.79. Must run with the owner present (elevation). |
| I-005 | Review WinAero Tweaker's public feature list | 1.79 report | More tweak ideas | S | new | Closed source: feature names as ideas only, values from Microsoft docs. |
| I-006 | Tweak Guardian: detect tweaks you applied that Windows Update or a driver silently reverted; offer re-apply (never automatic) | innovation-scout 2026-11-07 | Tweaks stay applied after updates | M | new | Reuses BackupStore, TweakVerifier, TweakService, ActivityLog; Dashboard card + Backup & Restore section. |
| I-007 | Boot Timeline: Windows-measured boot/sign-in time with before/after for startup changes | innovation-scout 2026-11-07 | Proves startup changes | S-M | new | Diagnostics-Performance log event IDs are from memory - researcher must verify. Label Fast Startup boots separately. |
| I-008 | Per-game compatibility flags (fullscreen optimizations, high-DPI override) in Game Library | innovation-scout 2026-11-07 | Per-game stutter fixes | S-M | new | HKCU AppCompatFlags\Layers; verify flag names; skip/warn for kernel anti-cheat games; pair with I-003. |
| I-009 | Help Bundle: redacted local PC report to copy into Discord/bug reports, previewed before copy | innovation-scout 2026-11-07 | Faster support | M | new | No upload; redact user name, machine name, paths. |
| I-010 | Driver/Windows regression watch: re-run a saved benchmark after a driver or Windows build change | innovation-scout 2026-11-07 | Catches FPS regressions | M | new | Reuses BenchmarkStore, SyntheticPlan, BenchmarkStats; manual prompted run only. |
