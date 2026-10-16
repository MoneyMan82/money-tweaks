# Changelog

## 1.19.0 – 2026-10-17
- **Legacy Potato — LOD Bias 0x20** (Potato Graphics › NVIDIA texture filtering), from the NVIDIA Inspector low-spec
  guide (Steam guide 1884111121): Transparency Supersampling = AA_MODE_REPLAY_MODE_ALL (0x00000008), Texture Filtering
  LOD Bias (DX) = 0x00000020 exactly (mandatory), Driver Controlled LOD Bias = On, Quality = High performance,
  Anisotropic filter and sample optimization = On, Anisotropic filtering = Off [Point] (with mode User-defined so it
  applies), Threaded optimization = On (OpenGL only), Power management = Prefer maximum performance. Fortnite profile only.
- **Why the 1.18 NVIDIA preset looked like it did nothing:** it was saved correctly (the driver showed +0.5 LOD bias), but
  +0.5 is a subtle blur, 0x20 was refused, and Driver Controlled LOD Bias was Off where the guide turns it On.
- New apply process: each field's ID is resolved from the driver's own setting name and cross-checked with NVIDIA Profile
  Inspector's metadata (its embedded CustomSettingNames.xml and Reference.xml, when Inspector is installed); the whole
  Fortnite profile is backed up (JSON + .nip); the preset is imported as a .nip (through Inspector if you choose it,
  otherwise through NVAPI, the API Inspector uses); everything is read back in a fresh session. Unsupported, rejected and
  unapplied fields are listed; if the mandatory field or anything unrelated (or the Windows display mode) changes, the
  backup is imported again automatically. The report shows whether Fortnite's renderer can honour the override
  (Performance mode / DirectX 11: yes; DirectX 12: likely ignored) separately from "saved".
- Controls: Apply, Verify, Restore Previous Profile (exact restore of the backup), Reset (only Money Tweaks' preset
  settings back to NVIDIA's values), Open backup folder. Mild/Extreme kept as comparison presets; custom controls kept.
- Verified on this PC (RTX 4070 SUPER, driver 617.14): import, verify, restore and reset on a throwaway profile; then
  Legacy applied to the real Fortnite profile: 8 written, 2 already set, LOD bias 0x00000004 → 0x00000020 read back,
  unrelated settings (V-Sync, pre-rendered frames, NVIDIA predefined) and Windows 2560×1440 @ 240 Hz unchanged.

## 1.18.0 – 2026-10-17
- **Driver & Windows Health Center** (Hardware Monitor tab, now "Hardware & Health"; the live monitor is unchanged above it):
  Scan now runs ten read-only checks in parallel with live progress, then shows Passed / Warning / Problem detected /
  Unable to check per result, with severity, timestamp, uncertainty notes and technical details. Results are saved.
  - Drivers: GPU (NVIDIA version decoded, e.g. 617.14; Microsoft Basic Display = missing driver), chipset (AMD 3D V-Cache
    driver check on dual-CCD X3D CPUs), network, audio, storage, Device Manager error codes, newer drivers offered by
    Windows Update. Driver age alone is never reported as "outdated"; official NVIDIA/AMD/Intel links, nothing installed.
  - Windows: edition/version/build with Microsoft's servicing dates, update services and policies (paused, disabled, WSUS),
    pending restart, update history (failed updates not installed since, with error meanings; Store app installs ignored),
    available updates from the Windows Update agent (search only; offline/service problems become "Unable to check").
  - Stability (last 14 days): blue screens with stop codes, unexpected restarts, GPU driver resets (Display 4101,
    nvlddmkm/amdkmdag), app/game crashes (incl. crashes in GPU driver modules), WHEA hardware errors, disk errors,
    CPU/GPU load, throttling (CPU performance limit, firmware limit events, nvidia-smi thermal flags), memory/commit
    pressure, page file, free space and drive health (Storage API).
  - SFC/DISM: CheckHealth, ScanHealth, SFC verify-only (read-only) with progress and cancel; results read language-
    independently (DISM /English, SFC from CBS.log [SR] lines), admin status shown.
  - Safe repairs, each confirmed with risk/admin/restart/undo: DISM RestoreHealth, sfc /scannow, re-enable disabled update
    services (start types backed up, verified, Roll back), scan for hardware changes. Restore point first where useful.
- **NVIDIA texture filtering for Fortnite** (Potato Graphics): real NVIDIA driver profile integration (NVAPI DRS, the
  database NVIDIA Control Panel and Profile Inspector use). Texture filtering Quality, Anisotropic sample optimization,
  Trilinear optimization, Negative LOD bias, Texture LOD bias (DX) +0.375/+0.5 (Driver Controlled LOD Bias turned off
  with it, otherwise the driver ignores it); OGL LOD bias shown as +0.0000, never written, no effect on DirectX Fortnite.
  Presets Default / Mild Potato / Extreme Potato. Each setting ID must match the driver's own name and each value must be
  in Money Tweaks' list and the driver's list/range; anything else, including 0x00000020 (+4.0 LOD bias), is refused.
  Originals backed up before the first change; every value read back in a fresh session, rolled back on mismatch;
  Restore original values / Reset to NVIDIA defaults. Explains texture detail vs filtering vs geometry; no guarantee about
  how skins look. Verified on an RTX 4070 SUPER (driver 617.14): write/read-back/restore on throwaway profiles, including
  an existing profile holding 0x00000020, which came back exactly; Fortnite's own profile was not touched.

## 1.17.0 – 2026-10-17
- **Start with Windows now runs as a background process** (Settings, and on the Fortnite auto-switch card): per-user Run entry with
  --background. No window opens, only a tray icon (Open / Exit); Fortnite auto-resolution, Gaming Profiles auto-switch and the
  macro emergency stop keep working, and their messages appear as Windows notifications. The dashboard is built only when
  opened from the tray; closing the window hides it again. Update check waits 2 minutes after sign-in. Measured: ready in
  578 ms, no visible window, 142 MB. The entry follows the exe if it moves.

## 1.16.0 – 2026-10-17
- **Start with Windows** (Settings, and on the Fortnite auto-switch card): per-user Run entry with --background. Opens
  minimized, builds the dashboard and animations only when first opened, checks for updates 2 minutes after sign-in
  (measured 450 ms to first frame vs 540-720 ms for a normal start). The entry follows the exe if it moves.
- **Single instance**: launching Money Tweaks again brings the running window to the front instead of starting a second
  copy (two copies would both run the watchers). Elevation restarts, the installer and portable updates pass --restarted
  so the new copy waits for the old one to close.

## 1.15.0 – 2026-10-17
- **Auto-switch resolution for Fortnite** (Fortnite Stretched tab): when FortniteClient-Win64-Shipping.exe starts, the chosen
  monitor switches to the stretched resolution (default 2288×1440, Fullscreen scaling); about 4 s after Fortnite closes it goes
  back to the saved Windows resolution. The switch is temporary (never saved), checked with the driver first, reverted when
  Money Tweaks closes and offered for revert after a crash. Off by default; turning it on checks the driver accepts the size.

## 1.14.0 – 2026-10-17
- Fortnite Stretched: 2288×1440 preset (also in Stretched Resolution); step-by-step guide for creating the custom resolution
  in NVIDIA Control Panel or AMD Software: Adrenalin, filled in with the chosen size and the monitor's refresh rate and
  pre-selected from the main display's GPU. After Apply, the status points to those steps.
- Screenshot mode (used for website images) shows the Fortnite settings path as %LOCALAPPDATA%, hiding the Windows user name.
- Website: new screenshots, tool cards for Profiles, Scanner, Test & Benchmark, Stretched Resolution, Fortnite Stretched and Macros, 340-tweak counts, recent-release changelog.

## 1.13.0 – unreleased
- **Fortnite Stretched** (new tab, INI method): finds Fortnite's GameUserSettings.ini automatically, any custom width × height
  with live aspect ratio and common stretched presets, window mode, optional read-only lock. Apply (preview of every key,
  original backed up once, unrelated settings preserved, values read back), Restore backup, Reset (writes the monitor's
  current resolution, keeps the window mode, removes the lock). Handles read-only files, missing file, invalid input and
  Fortnite running. Never changes Windows desktop/monitor resolution or refresh rate, and says that Fortnite, its rendering
  mode and GPU scaling can override the file, so stretched visuals aren't guaranteed.
- Stretched Resolution tab: its Fortnite section now opens the Fortnite Stretched tab.

## 1.12.0 – unreleased
- **Stretched Resolution** (new tab): lists real monitors (EnumDisplayDevices) with active/saved/native mode, scaling and every
  driver-reported mode; width/height editor with live aspect ratio; aspect buttons (4:3, 5:4, 16:10, 16:9, 21:9); built-in and
  saved presets (unlisted ones dimmed); refresh rates from the driver; scaling request (Fullscreen/Aspect=default/No scaling).
  Every change is checked with CDS_TEST first and applied temporarily: Test reverts after 10 s, Apply asks "Keep" within
  15 s or reverts; Esc/close = revert. Original mode saved before the first change ("Restore original"); unconfirmed modes
  are dropped on exit and offered for revert after a crash. No admin needed. Unsupported modes get the driver's answer and
  where to create a custom resolution (NVIDIA/AMD/Intel).
- Fortnite in-game resolution: writes only resolution, window-mode and letterbox keys in GameUserSettings.ini (own backup,
  read-back check, optional read-only lock, restore, unlock). Requires Fortnite closed; no game files touched.

## 1.11.0 – unreleased
- **6 new verified tweaks** (340 catalog entries): Optimizations for windowed games (DirectX SwapEffectUpgradeEnable, only that
  entry of the settings string), Fortnite on the high-performance GPU (per-app GPU preference, found via Epic manifests; two-GPU
  PCs only), Energy-Efficient Ethernet off (supporting adapters only), PCI Express link power saving off, never turn off hard
  disks, notification pop-ups off. New RegStringFlagOp edits one "Key=Value;" entry and restores the exact original string.
- **Every tweak** now shows expected benefit, confidence, risk, requirements, how it is verified and how Undo works
  ("What changes" on each card). Benefits are hand-written for performance tweaks and conservative defaults elsewhere.
- **Network diagnostic** (Ping Stabilizer): router + Cloudflare + Google in parallel for 20 s, DNS timing, and a plain-language
  verdict (home network vs ISP/route). Labelled as generic ping; Fortnite's real ping is in Net Debug Stats.
- Dashboard gauge renamed from "Tuning score" to "Recommended active" (it was always a checklist, not a performance rating).

## 1.10.0 – 2026-10-16
- **Gaming Profiles** (new page): built-in Fortnite Competitive, Fortnite Performance Mode, Geometry Dash and General Gaming;
  create, edit/rename, duplicate, export/import (money-tweaks-profiles), delete. A profile combines instant reversible tweaks,
  a temporary power plan, a timer-resolution request (Input Delay Reducer mechanism) and game priority. One-click activate/
  switch off; optional auto-switch when a listed game .exe starts and restore when it closes (2 s poll, only while Money
  Tweaks runs; system-wide profiles need admin). Preview shows every change; conflicts are detected. The session is saved
  before any change; switch-off restores tweaks (read back), timer and power plan; restored on app exit, offered at next start
  after a crash. No FPS/ping promises.

## 1.9.0 – 2026-10-15 (also covers the untracked 1.8.0 / 1.8.1 tags)
- Benchmark verdicts: changes under 1% of the baseline are never called meaningful (fixes a false "better" on frame time).
- **Synthetic benchmark audit fixes**: counterbalanced A/B order (off,on,on,off…) plus a 10 s unmeasured priming run (the old
  off,on,off,on order always gave "without" the cold-GPU first run); waits for CPU to settle after switching tweaks; no nvidia-smi
  launches during measured frames (GPU temp read once at the end); each run stores its real start time; headline verdict
  ("No meaningful difference … within run-to-run variation") above the table.
- New "Each tweak separately" mode: A/B per tweak with Helps / No measurable effect / Hurts / Mixed verdicts on FPS, 1% lows and
  frame time. Tweaks that cannot affect a windowed synthetic scene (game-specific, input, network, Game Mode/DVR, MMCSS games
  task, fullscreen optimizations, privacy/background) are listed with the reason instead of being "tested".
- "Large system cache" can no longer be applied (file-server setting, no gaming benefit; Undo kept). Risk note on "Minimum
  processor state 100%".
- **Smart Optimization Scanner** (new page): read-only scan of hardware (CPU/GPU/RAM speed), power plan and battery, RAM use and
  largest apps, busy background processes (1 s CPU sample), startup apps, network adapters (Wi-Fi vs Ethernet, link speed),
  system drive space, HAGS, candidate tweak states, reverted tweaks and restarts pending. Findings have severity, evidence,
  explanation, expected benefit (no FPS/ping promises) and confidence. Fix applies catalog tweaks through the verified engine
  (backup, read-back, auto-rollback) after showing every change; Undo fix and Ignore persist in scanner.json. Never touches
  services or security settings, never closes apps.
- **Synthetic 3D benchmark** (Test & Benchmark page): seeded 3D stress scene rendered by WPF/DirectX on its own UI thread
  (procedural textures, sun + orbiting coloured point lights, planar projected shadows, animated objects), intensity 1–5,
  15–120 s, 720p/900p/1080p, 1–3 runs per state. Asks "Without tweaks", "With tweaks" or "Compare both" (runs alternate).
  Real frame times from render callbacks (avg FPS, 1%/0.1% lows, frame-time p99, FPS per second), CPU/GPU/RAM from Windows
  counters, CPU temp (ACPI) and GPU temp (nvidia-smi) where available. Side-by-side table, % change, "meaningful" verdict,
  FPS chart, saved to history. Labelled synthetic, not a Fortnite benchmark; warns if a game is running.
- Tweak switching uses the verified engine; original states are saved to disk first and restored (and read back) after the
  test, on stop and on errors; an interrupted test offers the restore at next start. Reboot-only tweaks are not switched.
- Emergency stop: button, Esc in the scene window, or Ctrl+Alt+End.

## 1.6.0 – unreleased
- **Tweak Testing & Benchmark** (new page "Test & Benchmark"):
  - Verify all tweaks (read-only): reads every registry value, service start type, power-plan index, scheduled task,
    Store package and command check back from Windows; checks Undo has a usable backup; live-effect checks where Windows
    exposes them (pointer acceleration, menu delay, key repeat, running game priority, edition for telemetry level,
    disabled-but-running services, restart pending). PASS / WARNING / FAIL / UNVERIFIABLE with evidence; text report export.
  - Test button on every tweak card; optional live round-trip test (apply → read back → undo → compare every original value).
  - Before/after studies for a tweak, a preset or nothing: N timed runs per phase with CPU/GPU utilisation, RAM, CPU temp
    (ACPI, if exposed), GPU temp (nvidia-smi), ICMP endpoint latency/jitter/loss (labelled as NOT Fortnite ping).
    FPS / 1% / 0.1% lows / frame times from PresentMon or CapFrameX CSVs, or typed in; Fortnite ping typed in from Net Debug Stats.
    Mean ± sd per phase, difference and %, verdict only when beyond run-to-run variation; condition-mismatch warnings;
    bar chart per run; apply/roll back from the study with verification; history saved locally, CSV/JSON export, delete.
- Engine: Apply now reads every value back and rolls back automatically if Windows didn't keep the change. Undo compares
  the result against the backup and keeps the backup if anything differs.
- Audit fixes: "Disable Memory Integrity (HVCI)" can no longer be applied (security protection; Undo still works).
  Honest descriptions for Nagle (TCP only, not Fortnite's UDP), QoS reserve, MaxUserPort (ignored), TTL. Nagle and QoS reserve
  are no longer "Recommended"; Nagle removed from the Competitive Fortnite preset. Hibernation and HPET tweaks gained read-back.
  Duplicate/conflict detection (same registry value, DNS choices, power-plan order).
- **Macro Maker upgrade**: screen-colour triggers (pick a pixel or region from any monitor, HEX/RGB, tolerance,
  appears / disappears / changes), AND/OR logic, and colour conditions combined with key triggers (key AND colour).
  Looping colour macros stop when the colour condition ends. Live colour-trigger status, Pause/Resume (held inputs are
  released while paused and pressed again on resume), drag-to-reorder timeline blocks, live step highlight during Test/Play.
- Colour watcher: one background loop, one screen read per check when regions are close together, cached capture bitmap;
  about 0.5% of one core with 4 × 50×50 regions at 10 checks/s. Paused while a blocked competitive game is focused.

## 1.5.0 – 2026-10-14
- **Gaming Macros** (new sidebar page): ready-made macros by game/category (auto-walk, hold-to-toggle mouse, Minecraft sprint
  and AFK fishing, hold-Shift sequence example, Game Bar clip, region screenshot, paste-and-send) and a visual timeline editor:
  key press/down/up, combinations, mouse click/down/up, delays, wait-for-key-press/release, repeats/loops, safety timeout.
  Triggers: Run button, press key, hold key (stops on release), toggle key. Test (no input), Play (3 s countdown), Stop,
  Emergency stop (button + global Ctrl+Alt+End). Create, edit, duplicate, rename, delete, import/export (`.mtmacros.json`).
- Safety: standard SendInput only (no drivers/injection, no cursor movement); held keys/buttons always released; refuses to run
  while competitive anti-cheat games are focused; the trigger hook is only installed while armed and ignores injected input.

## 1.4.1 – 2026-10-14
- **Presets** (new sidebar page): built-in Competitive Fortnite, Low Latency, Stable FPS, Potato Graphics (Windows side)
  and Balanced Gaming; custom presets with editor (search, pick tweaks, validation); preview shows every op; apply uses
  the normal engine (admin, restore point, backups); Undo reverts exactly the tweaks a run changed; duplicate, rename,
  delete; import/export (`.mtpresets.json`) with validation. Stored in %LOCALAPPDATA%\MoneyTweaks\presets.json.
- Potato Graphics: Fortnite-style original 3D scene (wood/brick/metal builds, pines, storm wall, original character) and
  a "My Fortnite screenshots" mode to compare your own real before/after captures.

## 1.4.0 – 2026-10-12 (same build as 1.3.0)
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
