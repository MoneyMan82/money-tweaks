# Changelog

## Unreleased (next: 1.3.0)
- **Presets** (new sidebar page): built-in Competitive Fortnite, Low Latency, Stable FPS, Potato Graphics (Windows side)
  and Balanced Gaming; custom presets with editor (search, pick tweaks, validation); preview shows every op; apply uses
  the normal engine (admin, restore point, backups); Undo reverts exactly the tweaks a run changed; duplicate, rename,
  delete; import/export (`.mtpresets.json`) with validation. Stored in %LOCALAPPDATA%\MoneyTweaks\presets.json.
- Potato Graphics: Fortnite-style original 3D scene (wood/brick/metal builds, pines, storm wall, original character) and
  a "My Fortnite screenshots" mode to compare your own real before/after captures.
- **Premium redesign ("Midnight Circuit")**: deep black/graphite surfaces, electric-blue → violet accents, rim-lit cards,
  new buttons, switches, chips, sliders, progress bars and scrollbars; refreshed logo colours (same mark).
- New dashboard: hero with a real "tuning score" (share of Recommended tweaks active on this PC), animated CPU/GPU/RAM
  rings, network/disk/temperature sparklines, gaming-toolkit tiles, per-category "applied" bars.
- Command palette (Ctrl+K) to search all 334 tweaks, categories and tools.
- Sliding sidebar selection, page transitions, stacked toasts, empty states, loading/scan progress, ambient lighting.
- Settings › Motion & effects (Auto / Full / Reduced); Auto follows Windows animation settings and GPU tier.
- **Performance** (measured with built-in [perf] logging):
  - Networking state scan 10.5–14.1 s → ~5 s, and it no longer blocks the rest of the page: PowerShell checks run in
    grouped, parallel batches (11 checks: 24.1 s individually → 5.6 s batched, identical results).
  - Startup to first frame 679 ms → ~430 ms.
  - Custom Crosshair no longer loads WinForms (329 ms freeze removed); Game Booster lists processes off the UI thread.
  - Monitor sampling no longer creates ~300 Process objects per second; adapters and WMI temperature are cached.
  - Potato preview renders at 30 fps only while auto-rotating, visible and focused (was 60 fps continuously).
  - Slider changes are saved once (debounced) instead of on every tick.

## 1.2.1 – 2026-10-11
- **Fixed: endless update prompt.** The v1.2.0 release was tagged on the same commit as 1.1.0, and the release workflow
  took its version from Directory.Build.props instead of the tag, so "v1.2.0" shipped 1.1.0 files. Installing it changed
  nothing and the app offered it again forever. Now:
  - Releases are stamped with the tag's version, and the build fails if the app or installer reports a different one.
  - Before installing, the app checks the downloaded file's real version and refuses a release that isn't newer.
  - After an in-app update, the app confirms the running version actually changed. If not, it says so (with the
    installer log path) and stops auto-offering that release.
- **Potato Graphics redesign:** Fortnite potato studio with presets (Competitive / Potato / Ultra potato / Custom),
  a before/after 3D preview, review-before-apply diff, reset/unlock, a GPU driver guide and a clear list of what is and
  isn't possible. Low meshes are applied through Fortnite's own `[PerformanceMode] MeshQuality` setting.
- Preview restyled toward the rounded, toy-like low-poly look: chunky trees, puffy house and builds, soft rounded
  hills, flat colours and soft "clay" lighting, with an island, beach and ocean. Still an illustration, not game footage.

## 1.1.0 – 2026-10-11
- **Potato Graphics levels:** Low, Potato and Ultra potato, a 3D resolution slider (25–100%), Fortnite Performance mode
  (low-poly builds), uncapped FPS, and optional stretched 1440×1080 (the settings file is locked read-only so Fortnite keeps it).
  Only settings that already exist in the game's file are changed, and Restore always unlocks and restores the original.
- **In-app updates:** an "Update now" banner when a new version is on GitHub, plus Check for updates in Settings & About.
  Downloads are verified against the release's SHA256SUMS.txt before installing. Installed and portable copies both update
  themselves. The automatic check at startup can be turned off.
- Fixed the CI workflow (solution file is MoneyTweaks.slnx) and added Cloudflare Pages/Workers config for the website.

## 1.0.0 – 2026-10-11
First public release.

- 334 reversible tweaks across 9 categories: General 29, Debloating 61, Networking 17, Advanced 20, Services 110,
  App Optimizer 3, App Removal 76, Game Profiles 15, BIOS guides 3.
- Every tweak backs up the original value before changing it; undo from the same switch, per category, or all at once.
- Automatic System Restore point before the first change each session (admin, System Protection on).
- Safe / Caution labels, confirmation dialogs for Caution tweaks, Admin and Restart badges, "show what changes" details.
- Tools: Potato Graphics (Fortnite, Minecraft Java, Rocket League + Windows preset), Game Booster, Input Delay Reducer
  (timer resolution), Ping Stabilizer (latency/jitter/loss monitor), Custom Crosshair overlay (Ctrl+Alt+X),
  Startup Apps manager, Hardware Monitor, Backup & Restore, Activity Log.
- Runs as a standard user; system-wide tweaks prompt to restart as administrator.
