# Gaming and performance knowledge

Owned by gaming-performance-analyst. Add durable facts with a source link and date.

## Measurement rules used by the app
- Compare like with like: same game/program, same scene type, same resolution and settings; Fix My Game marks runs
  with a different program or FPS variation over 25% as Inconclusive.
- Use noise margins from repeated runs (BenchmarkStats) and say "no clear change" inside the margin.
- Report frame-time percentiles (1% lows) and stutter, not only average FPS. Never promise an FPS number.
- GameCapture treats a Win32Exception from an anti-cheat-protected process as "still running".

## Anti-cheat boundaries
- Never inject into, hook, read memory of, or modify files of games protected by kernel anti-cheat
  (Easy Anti-Cheat, BattlEye, Riot Vanguard, Ricochet, FACEIT). Editing a game's own user settings file while it is
  closed is fine (Potato Graphics, Fortnite GameUserSettings.ini), and the app refuses while the game runs.
- Process priority via Image File Execution Options (Game Profiles) and High priority for the game's own process
  (Autopilot) are existing, accepted practices.

## Game facts already in the app
- Fortnite: GameUserSettings.ini keys incl. [PerformanceMode] MeshQuality, sg.*, bShowGrass,
  CosmeticStreamingEnabled; stretched resolution; dGPU preference tweak (UserGpuPreferences).
- Games with CPU priority profiles: Fortnite, VALORANT, CS2, Apex Legends, Rocket League, GTA V (Legacy and
  Enhanced), Roblox, Marvel Rivals.
