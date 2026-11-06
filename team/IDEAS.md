# Ideas backlog

Statuses: new · researching · designed · approved · in progress · done · rejected (with reason).
Every idea needs evidence, a fit with existing systems, and a safety note. Only the owner approves.

| Id | Idea | Origin | Value | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| I-001 | Code-sign the installer, portable exe and Rescue exe | PROGRESS.md "Next ideas" | Fewer SmartScreen warnings, trust | M | new | Needs a certificate; costs money unless a free signing option exists (research). Must stay free for users. |
| I-002 | More games in Potato Graphics | PROGRESS.md "Next ideas" | Low-end PCs | M per game | new | Needs each game's real config format; never touch anti-cheat-protected files. |
| I-003 | Per-game GPU preference picker (iGPU/dGPU) | PROGRESS.md "Next ideas" | Laptops with two GPUs | S-M | new | Windows stores this per exe in HKCU `UserGpuPreferences`; reuse GameLibrary. Fortnite already has a dGPU tweak. |
| I-004 | Elevated round-trip test mode for admin tweaks | 1.79 verification gap | Proves HKLM tweaks restore exactly | S | new | Admin tweaks were only verified read-only in 1.79. Must run with the owner present (elevation). |
| I-005 | Review WinAero Tweaker's public feature list | 1.79 report | More tweak ideas | S | new | Closed source: feature names as ideas only, values from Microsoft docs. |
