# Changelog

## 1.65 – 2026-10-24
- **CPU temperature through LibreHardwareMonitor, no other monitoring app needed.** Hardware Monitor → System → Sensor
  sources can install the PawnIO driver LibreHardwareMonitor uses (the official signed installer, pinned by SHA-256 and
  checked with Windows' signature verification before it runs, only after you confirm) and start a small elevated sensor
  helper, so CPU package/core temperature, power and motherboard sensors appear in the Hardware Monitor, dashboard and
  performance overlay while the rest of Money Tweaks keeps running without administrator rights. Optional: start the
  helper with Money Tweaks.
- The helper only reads CPU and motherboard sensors and streams them over a private pipe; it accepts no commands and
  exits with Money Tweaks. Placeholder readings without the driver are never shown; missing sensors say exactly why
  (driver not installed, permission declined, driver not opened). Hints no longer point to MSI Afterburner or HWiNFO.

## 1.64 – 2026-10-24
- **Reality Lab: four more verified GPU models** (CC BY 4.0, licenses re-checked on each Sketchfab page, bundled
  unmodified with checksum and attribution): GeForce RTX 3080 Founders Edition by _surovic_ (used for RTX 3080/3080 Ti/
  3090/3090 Ti Founders Edition), RTX 4090 Founders Edition by GIHYEON (4090, and the 4080/4080 SUPER Founders Editions
  that share its design), MSI GeForce RTX 5070 VENTUS 3X OC by darky.vip21 (only for MSI RTX 5070 cards), and an RTX 5070
  Ti by hakai315 (any 5070 Ti, labelled as a typical card).
- Not bundled: the free RTX 5090 Founders Edition (Sketchfab Standard license doesn't allow redistributing the file) and the
  RTX 5090 ROG Astral (CC BY, but 209 MB / 3.1 million triangles). Both are listed in the manifest with the reason.
- The GLB reader now draws embedded base-colour textures. Models can be rotated upright per manifest entry, and
  maker-specific models are only used for that maker's cards.

## 1.63 – 2026-10-24
- **Reality Lab: verified third-party GPU models.** Downloadable models are used only when their license explicitly
  allows commercial use, modification and redistribution (CC0, CC BY 3.0/4.0); each is bundled unmodified with its
  source, license, attribution and SHA-256 in `Assets/Gpu/manifest.json` (checked before every load) and in
  THIRD-PARTY-NOTICES.md, and the attribution is shown next to the model.
  - Bundled: "Graphics card" by pgonarg (Poly Pizza, CC BY 3.0), shown for GeForce RTX 3080/3090 Founders Edition cards.
  - Evaluated but not bundled (listed in the manifest with the reason): a NonCommercial RTX 4090 Founders Edition, two
    CC BY Sketchfab models that need a signed-in account to download, an unlicensed RTX 4070, AI-generated galleries.
  - No verified, redistributable RTX 4070 SUPER model was found; that card and every other card keep the generated
    model, now clearly labelled "Generic model". A small GLB (glTF 2.0) reader draws the bundled files, with their fans
    spinning at the card's fan speed; rotate and zoom as before.

## 1.62 – 2026-10-24
- **Reality Lab: your graphics card in 3D.** A new panel shows this PC's GPU as a 3D model with studio lighting (key,
  fill, rim and a heat-coloured glow), drag to rotate, scroll to zoom, double-click to reset and a slow turntable:
  - The adapter is matched to a curated library of 49 popular cards (GeForce RTX 30/40/50 and GTX 16, Radeon RX
    6000/7000/9000, Arc A/B) by the exact model tokens of its name, with the PCI vendor confirming the brand, so near
    misses (RTX 4070 Ti SUPER vs 4070 SUPER, RTX 4090 D, workstation cards) never show the wrong card.
  - The card maker comes from the PCI subsystem ID. A Founders Edition / AMD reference / Arc Limited Edition is drawn at
    its published dimensions; a board-partner card (like this PC's ASUS RTX 4070 SUPER) as a typical card of that chip,
    labelled as such, because the PCI IDs don't say which partner model it is.
  - Models are built procedurally by Money Tweaks (no third-party 3D assets or logos; the product name is drawn as text).
    A card that isn't in the library gets a clean "model not in library" placeholder; integrated and laptop GPUs are
    shown as the chip, not as a card.
  - Live readings from the shared sensor hub drive it: fans spin at the card's reported fan speed (and stand still at
    zero-RPM idle), heat colour follows the temperature; load, temperature and hot spot, clock, power against the limit,
    VRAM and fan speed are listed, "not reported" when the card doesn't provide them. Library specs (chip, memory, rated
    board power, size) are shown alongside.
  - The virtual PC's graphics card takes the matched card's length and fan count (one front fan for a Founders Edition),
    a glass outline for an unknown card, and an empty slot when the graphics are integrated.
  - Built only when the page is shown, animates at most 30 fps only while visible (still with reduced motion), and the
    sensor hub is only held while Reality Lab is open.

## 1.61 – 2026-10-24
- **Uninstalling undoes everything Money Tweaks applied**, before any file is removed:
  - Every tweak goes back to its backed-up value (newest first, each verified by reading it back), an active gaming
    profile is turned off, CPU/GPU tuning is reverted, NVIDIA profile changes are restored, Potato Graphics and Fortnite
    settings files are put back, displays switched to a stretched resolution return to their original mode, and Money
    Tweaks' "start with Windows" entry is removed. It reuses the same restore code as the app's own Undo buttons.
  - A running Money Tweaks is asked to close first (its normal shutdown), so nothing re-applies while undoing.
  - Administrator permission is asked for once, and only if system-wide changes are still applied. If it's declined, those
    stay applied and their backups are kept in %LOCALAPPDATA%\MoneyTweaks, so reinstalling can still undo them.
  - A window shows progress and a summary of anything that couldn't be undone and why; a silent uninstall does the same
    without the window. In-app updates don't run the uninstaller, so updating never undoes anything.

## 1.60 – 2026-10-23
- **Performance overlay fixes** (readings flashing n/a, ping glitching):
  - Ping no longer resets to n/a whenever the overlay switches layouts: it keeps running while the overlay is visible.
    One lost reply keeps the last value; "lost" only shows after three in a row. An older ping loop can no longer
    overwrite a newer one after a restart.
  - Switching to another window no longer flips the overlay between layouts: a non-game app has to stay in front for
    about a second first. A detected game's own profile still applies immediately.
  - A reading that is missing for a single sample (a late sensor sample or a late batch of frame events) keeps its last
    value for up to 3 s (FPS 1.5 s) instead of flashing n/a. Readings that are really unavailable still say so, and are
    now noted in the log once a minute with the reason.
  - Values have a fixed width, so changing digits or "n/a" no longer resize the overlay and shift it sideways.
  - The frame-time graph's glide no longer stacks animations (it jittered).
  - Frame statistics are safe against out-of-order event batches, and the frame-timing session has more buffers so events
    aren't dropped while a game and the desktop both draw at high rates.

## 1.59 – 2026-10-23
- **LibreHardwareMonitor sensors** (LibreHardwareMonitorLib 0.9.6 from NuGet, MPL-2.0), read through the existing sensor
  hub, so the Hardware Monitor, dashboard, performance overlay and compact window all get them from one sampler:
  - GPU hot spot and memory junction temperatures, fan RPM and core voltage on NVIDIA; temperature, clocks, power and
    voltages on AMD GPUs (including integrated graphics). These need no driver.
  - CPU package and per-CCD / per-core temperatures, package power, physical core clocks, measured voltages, and
    motherboard temperatures and fans: read only when the PawnIO driver the library uses is installed and Money Tweaks
    runs as administrator. Money Tweaks never installs a driver. Without it the library reports placeholders
    (0 °C, 0 W, 1.55 V), so those are never shown; the monitor says exactly what's missing and offers "Restart as
    administrator" only when the driver is installed and elevation would help.
  - Light on the CPU: CPU sensors every sample, GPUs every 2–3 s (NVIDIA's refresh is the costly one); memory, storage,
    network, controllers and PSUs are left to Money Tweaks' own readers or not opened at all. The library is closed after a
    minute with nothing watching and on exit. Read-only: it never controls fans or clocks.
  - Can be turned off in Hardware Monitor → System → Sensor sources.
- **Third-party notices**: THIRD-PARTY-NOTICES.md (MPL-2.0 for LibreHardwareMonitorLib, DiskInfoToolkit and RAMSPDToolkit
  with links to their source, Apache-2.0 for HidSharp, MIT for .NET libraries) ships next to the app in the zip and
  installer, and is shown in Settings & About → Open-source licenses (so the single .exe carries it too).

## 1.58 – 2026-10-23
- **Performance Overlay upgraded into a gaming HUD** (same page, same hotkey, existing settings carried over):
  - Readings: FPS, frame time, 1% low, a frame-time graph (last two seconds, spikes highlighted), CPU usage and
    temperature, GPU usage and temperature, RAM, **VRAM** and **ping**. Choose exactly which appear and **reorder** them.
    A reading this PC can't provide shows "n/a" (FPS without admin rights says "admin"), never a made-up number.
  - Three layouts: **Minimal** (one line), **Compact** (labelled row) and **Detailed** (list with usage bars and the graph),
    each with graphs or text only. Themes: Vector, Mint, Ice, Amber, Mono or your own colours. A dark text outline keeps
    numbers readable over bright scenes; numbers ease to new values and the graph glides (at most 30 fps, only while
    something changes; off with reduced motion).
  - **Move and scale anywhere**: "Move & resize on screen" or a second global hotkey (Ctrl+Shift+F11) lets you drag the
    overlay to any spot on any display and scale it from its corner; the spot is remembered per display. Overall opacity,
    background opacity and size are adjustable. **Click-through** can be switched off to drag it any time; it never takes
    focus either way.
  - **Game profiles**: the overlay recognises the game it measures and loads that game's layout. Fortnite (compact,
    competitive), Geometry Dash (minimal FPS readout) and Roblox (compact) come built in; any game can be added by exe or
    picked while it's open.
  - Readings come from the Hardware Monitor's shared sampler (the GPU doing the most work, with its VRAM), so nothing is
    measured twice. Frame timing additionally listens for the kernel's present events (PresentMon's "Other" runtime, for
    OpenGL and Vulkan games such as Geometry Dash), with an event-ID filter so the session stays light. DirectX capture is
    verified; the OpenGL/Vulkan path is not yet verified with a real game.
  - Ping is the round trip to your ping host (internet latency), not a game server's ping, and is labelled as such.
  - Everything the overlay starts (frame-timing session, sensor sampling, ping, hotkeys) stops when it's hidden or when
    Money Tweaks exits.

## 1.57 – 2026-10-23
- **Hardware Monitor upgraded into a monitoring center** (same page, same Health Center below it):
  - CPU: total and per-logical-processor usage and effective clock (Windows' own frequency counters), the performance
    limit Windows reports (thermal/power limiting), temperature and package power where this PC provides them.
  - GPU (every adapter): usage, 3D engine, temperature, core and memory clock, power against the power limit, VRAM used
    against total, fan speed and the driver's current clock limiters (NVIDIA through NVML, the read-only telemetry library
    that ships with the driver; other GPUs through Windows counters and HWiNFO / MSI Afterburner when they run).
  - Memory in use, used and available, commit charge and page reads; every drive's active time, read/write throughput,
    response time, temperature (Windows shares it with admins) and free space per volume; every network adapter's download
    and upload; fan speeds from HWiNFO.
  - Sensors are discovered at start and re-checked (a tool started later adds its readings). Anything this PC can't report
    is shown as Not available with the reason. Nothing is estimated, and no driver is installed.
- **Live graphs** with 1-minute, 5-minute, 15-minute and 1-hour history, hover readouts (time and every series), current /
  min / average / max and units on every reading; graphs glide as samples arrive, rescale smoothly and wipe in on a new range.
- **Compact overview, pinned favourites and collapsible detail sections** (CPU with every thread, each GPU, memory with
  the installed modules, each drive, each adapter, fans, warnings, hardware models, system). Open/closed state is kept.
- **Warnings**, configurable: high CPU / GPU / drive temperature, CPU performance limiting, GPU thermal throttling, memory
  pressure and commit charge, sustained high CPU usage (with the busiest processes), sustained upload and a drive busy all
  the time. The reading's card is highlighted, the warning is listed at the top, and a notice appears on other pages.
- **Refresh every 0.5, 1, 2 or 5 s, pause/resume, CSV export of the recorded history, continuous CSV logging**, and an
  optional **compact always-on-top window** with your pinned readings.
- One shared background sampler now feeds the monitor, the dashboard gauges and the in-game overlay (a few milliseconds per
  sample, off the UI thread, only while something is shown). The dashboard and overlay no longer count network traffic two
  to four times on adapters that Windows also lists as filter-driver layers.

## 1.56 – 2026-10-22
- **Square corners everywhere.** Every card, panel, button, input, dropdown, navigation item, dialog, popup, menu, tag,
  tooltip, progress bar, switch, slider, radio button and expander is now sharp and angular (0 rounded elements left in
  an automated audit of all 38 pages). Windows 11 no longer rounds the app's windows. Gauges are square instruments,
  status dots are squares, the slider thumb is a diamond and lines end flat. Depth comes from thin borders, layered
  surfaces and light instead of curves. The logo tile and icons are square too.
- **Much more motion**, mostly 120–350 ms with smooth easing, longer for page changes:
  - Page transitions fade, rise and settle in scale; content enters in a stagger.
  - The navigation indicator glides between items and its accent edge stretches and pulses.
  - Buttons get a light sweep on hover and a press dip; navigation items nudge; tweak cards get a hover highlight.
  - Toggles slide with a slight overshoot and a halo; chips draw an underline when selected; text boxes draw a focus
    line; tooltips, menus and expanders fade and slide in.
  - Progress bars glide to new values; benchmark bars grow, FPS lines draw in, Genome bars extend from zero, gauges
    sweep to their value.
  - Header rules draw out with a passing glint, panel corner brackets draw out, loading shows a stepping sequencer,
    toasts show a countdown line, and the background light drifts very slowly.
  - Windows' "Animation effects" off (or Reduce motion in Money Tweaks) turns the code-driven motion off.
- **Website renewed** to match: square corners, Vector palette, animated hero, light sweeps on buttons, staggered
  reveals, HUD-framed screenshot, and new screenshots of the current version.

## 1.55 – 2026-10-22
- **New look: "Vector".** A redesigned design system across the whole app, with every feature unchanged:
  - Deep neutral graphite surfaces lit from the top-left (cards are a touch lighter at the top, their rim catches the
    light); one electric violet accent for actions and selection, cyan for live data and focus. The violet→cyan
    signature gradient is used only on small elements (selection bars, rings, progress, the header rule).
  - Bahnschrift (the logo's face) for titles, eyebrows, group labels and numbers; Segoe UI for body text.
  - New page header: icon tile with a violet→cyan rim, cyan eyebrow, title, and a hairline rule with a short signature
    segment. Section headings carry a small gradient marker. The same header on the tweak lists.
  - Feature panels (dashboard overview, Reality Lab model, scanner readout) get thin cyan corner brackets; gauges
    get instrument tick marks. Tags have crisp 1 px outlines; chips, inputs and buttons are tighter and squarer.
  - Sidebar: MONEY TWEAKS wordmark as in the logo, group labels with hairlines, gradient icon on the selected page.
  - Dialogs: graphite cards with a thin coloured line for the kind (confirm, warning, error) and an icon tile.
    Toasts have a tone edge and a soft shadow. The search palette is deeper with a shadow.
  - Empty states use the icon tile; colour tokens replace remaining hard-coded colours.
  - Narrow windows: the title-bar search shortens to "Search" so the breadcrumb stays readable.
- Legacy UI still gives the original green look (its colour map covers the new palette).

## 1.54 – 2026-10-22
- **Tweak Truth Index.** Every switchable tweak's details now show its evidence, in three clearly separated parts:
  - **Measured on this PC** (from your benchmark history): per test type, each metric's number of tests, median
    change, spread between tests, repeatability (share of tests reaching the same verdict), regression rate (share
    where it got measurably worse) and a confidence level. A test counts only if exactly this one tweak differed
    and it has 2+ runs with and without it; tests with different run lengths, battery/mains mixes, Fortnite in only
    some runs or a mostly busy PC are listed as "not counted" with the reason, and frame rates capped by the display
    are ignored. Tests that changed several tweaks together are never attributed. No conclusion or effect size is
    shown before 3 valid tests; disagreeing tests are reported as "no consistent effect".
  - **Expected benefit**: the existing estimate, labelled as an estimate and never mixed with measurements.
  - **Other players** (Community Hub, loaded only when you ask): medians and better/worse shares overall and for your
    GPU, CPU, Windows version and games, labelled community-submitted and shown only once a group has 5 results.
    Offline, the measured part works as before and the hub part says it couldn't connect.
- **Sharing is off by default** (Settings › Result sharing). When allowed, "Share my N results" shows exactly what will
  be sent before sending: tweak, test type, per-metric changes and run counts, CPU/GPU model names, RAM rounded, Windows
  build and app version. Never your account, PC or user name, serials, paths or times. Each test is shared once.
- "Measure in Reality Lab" in a tweak's evidence runs a measured A/B test of just that tweak (it becomes a trial).
- Benchmarks now record exactly which tweaks differed between "before" and "after" (older studies are matched from their
  saved details where possible).
- Community Hub API: `/api/community/evidence` (anonymous storage, strict validation, 50 shares/day, robust aggregates,
  moderator clear). Tables are created automatically; free plan only.

## 1.53 – 2026-10-22
- **Fixed: the Website button and links pointed to a dead address** (money-tweaks.pages.dev). The app's Website
  button, the README, and the site's own sharing card, canonical link, robots.txt and sitemap now use the live site,
  https://money-tweaks.barkgdd.workers.dev. The README's website section describes the current Cloudflare Worker setup.
- **Reality Lab: make your own experiment.** "+ Make your own set" opens the preset editor to pick any tweaks; the new
  preset shows up in the lab's list when you come back (only its safe, switchable tweaks are tested, as with any preset).

## 1.52 – 2026-10-22
- **Video tutorials are embedded in the page**: Potato Graphics and Fortnite Stretched show a "Video tutorial" card
  with a 16:9 player right on the page. Press play and YouTube's privacy-enhanced player plays it in place; it scrolls
  with the page like everything else. Nothing is loaded from YouTube until you press play, and leaving the page stops
  it. "Watch tutorial" at the top jumps to it and starts it; "Pop out" plays it in its own window (with fullscreen);
  "Open on YouTube" opens your browser.

## 1.51 – 2026-10-21
- **Fixed: updates could stop at an older version.** v1.48.0 and v1.49.0 were released at the same moment, v1.48.0
  finished last, and GitHub marks the most recently published release as "latest", so the update check (which asked
  GitHub for "latest") told 1.48 users they were up to date. The app now picks the highest version among the published
  releases itself, the release workflow runs one release at a time and always marks the highest version as latest.

## 1.50 – 2026-10-21
- **Tutorials play inside Money Tweaks**: "Watch tutorial" opens a player window (YouTube's privacy-enhanced
  youtube-nocookie.com player in Microsoft Edge WebView2) and starts the video; fullscreen works. "Open on YouTube"
  is still there, and links that leave the player open in your browser. Without the WebView2 runtime the video opens
  in your browser instead.

## 1.49 – 2026-10-21
- **Video tutorials**: Potato Graphics and Fortnite Stretched have a "Watch tutorial" button at the top of the page that
  opens the official Money Tweaks video on YouTube in your browser. Searching "tutorial" (Ctrl+K) finds both pages.

## 1.48 – 2026-10-21
- **New logo.** An original symbol: a forward-leaning "M" whose centre runs down into a stem under a separate crossbar,
  so it reads as both M and T, with one machined gap for precision and an angled leading edge for speed. Only the
  crossbar carries the app's blue-to-violet accent. A heavier cut with wider gaps is used at 16 to 32 px so it stays crisp.
- Logo system in `branding/` (vector SVG plus 1024 px transparent PNG): symbol, app icon, monochrome symbol, wordmark
  lockups for dark, light and single-colour use. All are generated from one definition by `branding/make_logo.py`.
- Replaced everywhere: app and taskbar icon, tray icon, installer icon, sidebar logo, website logo (now SVG), favicons
  (new SVG favicon) and the social sharing card.

## 1.47 – 2026-10-21
- **Reality Lab's virtual PC is now realistic**: a full ATX motherboard (circuit board with traces, vias, silkscreen and
  mounting holes, VRM heatsinks, capacitors, chipset heatsink, M.2 SSD, power connectors, SATA ports), a tower CPU cooler
  with copper heat pipes, an aluminium fin stack and a 120 mm fan, RAM modules with heat spreaders and light bars, and a
  triple-fan graphics card standing in its PCIe slot with backplate, fin edge and bracket. Brushed-metal and anodised
  textures, key/fill/rim lighting and soft contact shadows.
- The model shows **your real number of memory modules and slots** (read from the firmware), also listed under RAM.
- Heat now glows on the board around the CPU and GPU, the CPU fan spins with CPU load, the RAM light bars' length shows
  memory in use. Still drawn only when the page is open, at most 30 fps, and paused while measuring.

## 1.45 – 2026-10-21
- **Reality Lab** (Monitoring & Benchmarks): a 3D virtual PC (CPU and cooler, RAM, graphics card) driven by this PC's
  live readings: heat colour from the temperature sensor (or load when there is none), GPU fans spinning with GPU load,
  RAM sticks filling with memory in use, data pulses speeding up with CPU load. Drag to turn, scroll to zoom, click a part
  for its real readings. A part Windows gives no reading for stays grey and says so.
- **Safe experiments**: four curated sets (Lighter desktop, Power saving off, Quiet background, Foreground priority) or
  the safe, switchable tweaks of any preset. The preview lists every registry value, service or power setting that would
  change, the tweak's state now and how it's undone, plus an **estimate** per metric that is labelled as an estimate,
  explains its reasoning and never gives a number.
- **Measured, then restored**: Run switches the tweaks off and on in a counterbalanced order (off, on, on, off),
  measures idle resource use (and optionally the synthetic 3D FPS scene from Test & Benchmark), then puts every tweak back
  and reads it back. Interrupted runs are offered for restore on the next start. Results show each metric before/after
  with run-to-run variation, whether the change is meaningful, and whether the estimate matched. "Show without/with in
  3D" drives the model with the measured averages. Results are saved in Test & Benchmark › History.
- **Keep or roll back**: nothing is kept unless you choose Keep (restore point, a backup per tweak); kept experiments
  can be rolled back from the lab, reverting exactly those tweaks.
- Lightweight: the page and its 3D scene (about 600 triangles) are only built when opened, animate at up to 30 fps
  only while visible, and pause while measuring so the lab doesn't affect its own results. Reduced motion holds the
  model still.
- **Legacy UI** (Settings › Appearance): switch back to the original 1.0–1.3 look (green accents, flat cards, no
  ambient glow). Every page and feature stays the same; takes effect after a restart (button included).

## 1.44 – 2026-10-21
- **Sharp crosshair previews in the Community Hub.** Crosshairs on post cards and post pages are now drawn by the app
  with the same renderer as your overlay, enlarged to fit, instead of a small server-made picture that looked like
  32×32 pixel art. Circles and rotated lines are smooth; straight lines stay crisp.
- The server no longer makes or stores preview images (less work and storage on the free plan); old ones are removed
  automatically.

## 1.43 – 2026-10-21
- **Share in one click:** "Share to Community Hub" on Custom Crosshair (Profiles › Share) and on each of your Gaming
  Profiles opens the Hub's Publish form with that crosshair or profile already chosen, a title and category filled in;
  add a description or screenshot if you like and press Publish.
- **Get in one click:** every Community Hub post with a crosshair, gaming profile or preset has a **Get** button, on the
  card and on the post. It adds them to your library right away (crosshair into Custom Crosshair, profile into Gaming
  Profiles, preset into Presets) without a confirmation, because nothing is applied or activated. Tweaks that don't
  exist in your version are skipped and listed. After getting, an "Open" button takes you to it.

## 1.42 – 2026-10-20
- **Community Hub** page (Gaming tools): discover, search, like, bookmark, rate and report shared game settings,
  crosshairs, gaming profiles and presets; preview everything and import it into your library (never applied by itself);
  publish and edit your own posts with screenshots; moderation tools for moderators. Off until a server address is set.
- **Community Hub server** (`src/MoneyTweaks.Community.Server`): GitHub sign-in, SQLite, S3/R2 or disk image storage,
  2 image uploads per account per UTC day enforced by the database, safe re-encoding of PNG/JPEG/WebP, server-drawn
  crosshair previews, reports and review queues. See `COMMUNITY_RELEASE_CHECKLIST.md`.
- Custom Crosshair: "crosshair x" removed from the page's search keywords (another product's name).
- **Community Hub API inside the website's own Cloudflare Worker** (`worker/`), under `/api/community/`: accounts,
  posts, profiles, the 2-uploads-per-day limit and the images themselves are all stored in the existing D1 database
  (no R2, no billing, no second Worker). Website pages are still served directly from `docs/`. The app uses
  `/api/community/`; the self-hosted .NET server answers there too. Setup guide: `worker/README.md`.
- The Hub page says which server parts aren't set up yet instead of failing silently.
- 33 new tests (27 server, 6 import) + 38 Worker tests.

## 1.41 – 2026-10-20
- **Custom Crosshair rebuilt as a full editor** (Gaming tools › Custom Crosshair). Your saved crosshair becomes the
  first profile automatically and looks the same.
  - **Layers:** lines (each arm on/off, length, thickness, gap, round ends), centre dot (square/round), circle and your
    own PNG image, each with colour, opacity, outline (colour/thickness), X/Y offset and rotation. Add, duplicate,
    reorder, hide and delete layers.
  - **Live editing:** large preview (dark/grey/sky/grass or your own screenshot as background, 1×–8× zoom, pixel grid),
    exact value boxes with ± nudges next to every slider, undo/redo (Ctrl+Z / Ctrl+Y). Every change redraws the real
    overlay immediately.
  - **Pixel-exact drawing:** shapes are drawn in physical screen pixels at any Windows scaling and snapped so thin
    lines stay sharp and left/right stay mirror images.
  - **Firing animation:** spread (bloom), fade and colour flash while the left/right mouse button is held, with rise
    and recovery times; optional hide while aiming. Detected by reading the mouse button state through Windows only
    while the crosshair is shown (no hooks, nothing injected). It follows the button, not actual shots.
  - **Profiles:** unlimited, with templates, duplicate, rename, delete, export/import files (.mtcrosshair, images
    included) and shareable MTX1 codes. Imports are validated and clamped.
  - **Overlay:** click-through, always on top, never takes focus; monitor choice or follow the game's monitor,
    position offset, scale, opacity, optional show at start. Three global hotkeys (show/hide, next/previous profile),
    recorded by pressing them, now working from app start without opening the page; conflicts are refused and the
    old hotkey kept.
  - **Per-game profiles:** a listed game in front switches to its profile and can show the crosshair; it hides while
    the game isn't in front and the previous profile returns when the game closes. Exclusive fullscreen (where no
    overlay can be drawn) is reported instead of pretending it works.
- Saved in crosshair.json with atomic writes; a damaged file is kept aside and your previous crosshair restored.
- 19 new tests (geometry and snapping, bloom, migration of every old style, save/restart, share codes, import
  limits, PNG checks, undo/redo); the overlay was also checked on a real desktop by reading back screen pixels.

## 1.40 – 2026-10-20
- **Performance Genome is much simpler to use.** The page now starts with one card: 1) choose what you want (five
  plain goals), 2) press Start and wait (with the real time estimate), 3) apply the winner. Start, Stop and progress
  live in that card. When results exist, a "Your result" card says in plain words what's best for your goal (or that
  your current settings are already best) with one Apply button. Goal weights, limits, test length, workloads and the
  list of tested settings moved under a collapsed "More options"; the effect map, frontier, profiles and history are
  under Details and only appear once there's something to show.
- Without administrator rights, Start now offers to test the CPU settings only instead of stopping.

## 1.39 – 2026-10-19
- **Upgrade Advisor** (Monitoring & Benchmarks › Upgrade Advisor): "What are the three smartest ways to get closer to
  my FPS goal on my PC without wasting money?"
  - **Setup:** game (from your library), resolution, graphics settings, current FPS, target FPS and an optional budget.
    This PC from Windows: CPU and socket/platform, GPU and VRAM, RAM type/size/sticks/slots/channel mode and XMP/EXPO
    status, storage (SSD/HDD, NVMe/SATA, which disk the game is on), motherboard and BIOS. What Windows can't know
    (power supply, case clearance, cooler) is listed, never guessed.
  - **Measurement while playing** (frame timing from Present events, administrator) or with the built-in 3D benchmark:
    GPU load, clocks, temperature, power vs. limit and throttle reasons, VRAM; per-core CPU load, CPU clock vs. base,
    temperature; available and committed memory, hard page faults; disk response and queue; game vs. background CPU;
    FPS, 1% lows, 99th percentile frame time, stutter and frame caps. Lightweight (nvidia-smi every 2 s).
  - **Diagnosis** needs several agreeing signals: frame cap, GPU (with power/thermal limits), CPU (main thread or all
    cores), CPU below base clock, CPU heat, memory pressure, full VRAM, storage-related stutter, background apps, or
    "no clear hardware limit". Mixed results are called mixed. Evidence is shown in words for each.
  - **The three smartest moves**, ranked by expected benefit × confidence per euro: free fixes (remove a frame cap,
    lighter settings/upscaling, raise the GPU power limit, XMP/EXPO, close background apps, CPU clocks, free RAM,
    lower textures, move the game to an SSD), low-cost (cooling, a second RAM stick for dual channel) and hardware
    (GPU/CPU/RAM/SSD with socket, BIOS, RAM type, PSU and clearance requirements). Hardware is left out when a free fix
    is projected to reach the goal; over-budget options are listed separately; fewer than three only with a reason;
    nothing when you already reach the target. Each card links to the matching Money Tweaks feature.
  - **Prices are never fetched or invented:** cards open searches at reputable stores for your country (e.g.
    Verkkokauppa.com, Jimm's, Proshop, Hintaopas in Finland) and you enter the price you find, kept with source and date.
  - **Goal simulator and upgrade planner:** measured vs. projected (striped ranges from measured headroom, e.g. "the
    most a GPU upgrade can give before the CPU limits"), performance per €100 once prices are entered.
- **PC Cleaner: clear standby memory**, measured from Windows (standby cache and free memory before/after), with an
  option to do it after every cleanup. The page explains that standby memory already counts as available and that
  clearing it doesn't raise FPS by itself.
- 11 new tests: scenario captures (GPU-, CPU-, cap-, memory-, storage-limited, uncertain), ranking/budget/price rules,
  socket inference, and read-only checks of real hardware, telemetry counters and standby memory (326 total).

## 1.38 – 2026-10-19
- **PC Cleaner** (Optimization › PC Cleaner): Performance Cleanup and Storage Cleanup on one page, with live CPU/RAM,
  reclaimable space, quarantine and cleanup history.
  - **Performance Cleanup:** this user's running apps measured right now (CPU over 1.5 s, RAM), each classified Safe to
    Close (a curated list of background sync, updaters and optional extras), Review First (browsers, voice chat,
    launchers, music, peripheral software, unknown apps) or Protected (Windows, security software, anti-cheat, driver
    helpers, embedded browser engines, audio, controllers, networking/VPN, streaming/recording, installers, Money Tweaks,
    the game, and anything whose file can't be checked). Quick Clean, Custom Clean and Clean & Play (per-game cleanup
    saved per library game; optional cleaning whenever the game is launched from Money Tweaks). Apps are asked to close
    first (WM_CLOSE to their windows); force-close only if allowed and the app ignored it; one process at a time, never
    its children, only this session. Re-checked right before closing. CPU/RAM before and after are measured and the
    result says what each app did; no FPS claims. Exclusions per app.
  - **Storage Cleanup:** Scan → Review files → Clean selected. Categories with size, file count, locations, why it's
    safe and what was left out: old user/Windows temp files, large leftover temp files, crash dumps and error reports
    older than 14 days, rotated servicing logs, thumbnail cache, Delivery Optimization cache (Microsoft's own cmdlet),
    Windows Update component cleanup (DISM /StartComponentCleanup, with Analyze), Recycle Bin (shell API, separate
    confirmation) and optional GPU shader caches (off by default; rebuild stutter explained). Prefetch, browser data,
    personal folders, Installer/WinSxS, restore points and Windows.old are deliberately not offered.
  - **Safety:** approved roots only; links and junctions are never followed or deleted; a hard deny list applies to
    every file; hidden is never a reason to delete and system files are skipped; every file is re-checked at clean time
    (exists, unchanged, not read-only, not in use, inside its root, not excluded); files are quarantined for 7 days on
    the same drive (restorable) unless you turn it off; deletions are verified; cancellation is safe and reported.
- Game Booster now uses the same protection, so it can't close security software, anti-cheat, audio, controller,
  VPN, streaming or installer processes even if they're on its list.
- 10 new tests: real files, junctions, locked/changed/read-only files, quarantine restore/purge, cancellation, the
  deny list, classification and a real windowless process closed politely/forcibly (315 total).

## 1.37 – 2026-10-18
- **Intelligent Feature Discovery**, built on the existing search, tweak pages, compatibility engine and catalog review
  (no second search, no Recent tab, no new favourites):
  - **Natural-language search in Ctrl+K.** "make Fortnite smoother without increasing temperatures" is understood as
    smoother gameplay + Fortnite + no extra heat: Fortnite's own tweaks and pages come first, then heat-neutral gaming
    tweaks, and 6 tweaks that raise power/heat are left out (the palette says so). Ranking uses rare-word relevance,
    purpose, game, this PC's compatibility findings and your Performance Genome goal. Every result shows why it matched;
    known limits are stated ("limited effect: only changes TCP"). Only existing tweaks are ever returned.
  - **Related settings in every tweak's details:** conflicts and duplicates first, then order notes, same-feature
    overlaps, the same registry area, and tweaks serving the same purpose in other categories, each with the reason
    and whether it's applied right now. Click opens it where it lives.
  - **Tweak Map** (Optimization › Tweak Map): the whole library by purpose; category › group tiles sized by count;
    hover lights up connected groups; click lists the tweaks (each opens its page); lenses for recommended, applied and
    related; every conflict, duplicate, order note and overlap with both settings linked, and pairs that are both
    applied pinned to the top.
  - **Duplicate and conflict detection:** exact conflicts and duplicates from the compatibility engine (compared only
    within shared targets, never all pairs), the catalog review's notes, power-plan order notes, and cross-category
    overlaps of the same feature (e.g. the Superfetch tweak and the SysMain service). The real catalog has exactly the
    three DNS conflicts, matching the existing conflict audit.
  - **Scale:** a 1,200-entry library builds the index in about 80 ms; related lookups take 0.3 ms and searches 2 ms;
    the map aggregates by group and pages long lists, so it stays readable.
- 7 new tests including the 1,200-entry scale test.

## 1.36 – 2026-10-18
- **Performance Genome** (Optimize › Performance Genome): a performance-discovery engine that learns how this PC
  responds to combinations of supported settings from its own measurements.
  - **Goals with hard limits:** Competitive consistency, Maximum FPS, Minimum latency (a frame-time proxy, labelled
    as such), Lower temperatures, Quiet efficiency, or custom weights; limits for GPU/CPU temperature, FPS and 1%-low
    loss and GPU power are never broken by a recommendation.
  - **Adaptive experiments:** screens every setting level, then tests the most promising settings together to detect
    interactions, repeats promising candidates to beat noise, then explores by upper confidence bound. Baseline runs
    of your own settings at the start, after every 3 trials and at the end; each result is compared with a locally
    weighted trend through them, so warm-up and drift cancel out. Conditions (idle CPU, processes, power source) are
    recorded per run; the PC settles before each run.
  - **Model:** Bayesian ridge regression per metric with main effects and pairwise interactions; noise is measured from
    your baselines. Measured results (with 95% intervals, run counts, stability and confidence) are kept strictly
    apart from predictions, which are labelled and never recommended.
  - **Settings it varies:** CPU boost mode, energy preference and (optional) maximum state through Windows' power
    settings; NVIDIA power limit and clock cap through nvidia-smi (administrator); performance-related Windows tweaks
    you add (never security trade-offs or Experimental tweaks); game settings you change yourself when asked.
  - **Workloads:** the built-in 3D scene, calibrated so the display's refresh rate doesn't cap FPS, or a controlled
    manual comparison in a running game measured from its Present events (read-only, nothing injected).
  - **Safety and history:** every change is read back; your settings are restored and read back at the end, on Stop
    or after a failure, with crash recovery on the next start. Runs stop at 90 °C GPU / 95 °C CPU; a setting or
    combination that fails (refused, driver reset, WHEA error, crash) is never tried again. Every run is saved; a driver,
    Windows, BIOS or game update marks earlier results stale and screening starts again.
  - **UI:** this PC's effect map (what each setting does here), performance frontier with Pareto front and error bars,
    distinct recommended profiles with explanations, comparison chart, history and journal. Profiles apply through the
    CPU & GPU Tuning path (backup, read-back, Undo all) with one-click rollback.
- The synthetic benchmark's "wait for the PC to settle" step is now shared (BenchmarkSampler.SettleAsync).
- 12 new tests against a simulated PC with known effects, an interaction, noise, drift, a failing setting and a driver
  update (298 total).

## 1.35 – 2026-10-18
- **CPU auto tuning** on CPU & GPU Tuning:
  - **CPU boost scan.** Measures four Windows boost / energy-preference combinations under real load (one thread and
    all threads, with temperature and package power when MSI Afterburner or HWiNFO runs). Your current settings are
    measured first and last, so the run's own noise is known and smaller differences aren't shown as gains. It
    recommends the coolest (or lowest-power) option that is just as fast. Your settings are put back afterwards; you
    apply the one you want through the normal preview, backup and read-back.
  - **Curve Optimizer assistant (Ryzen) / undervolt assistant (unlocked Intel).** Tests every core on its own (a
    self-checking load pinned to that core, where too much undervolt shows), then tells you the next value per core:
    passed → one step further, failed → one step back and never past it again, until every core is done. Fails on
    wrong results, WHEA errors, 95 °C, or a crash/restart during the test (it remembers which core was running). You
    set the values in Ryzen Master or XTU, because AMD and Intel let no other app change them.
- 7 new tests (286 total).

## 1.34 – 2026-10-18
- **CPU temperature from HWiNFO.** Windows has no public way for apps to read the CPU temperature, so Money Tweaks now
  also reads it from the free HWiNFO (its documented shared memory, read-only; HWiNFO uses its own signed driver and
  Money Tweaks installs none). Order: firmware, then MSI Afterburner, then HWiNFO. The source is shown next to the
  value. CPU & GPU Tuning › Everything else has the setup steps and a link to HWiNFO's official site. In the free
  version, Shared Memory Support switches itself off after 12 hours.
- 3 new tests with byte-exact HWiNFO memory images (279 total).

## 1.33 – 2026-10-18
- **Automatic GPU overclock scan.** Auto tuning now finds your best GPU overclock: it raises the core clock offset step
  by step under the 3D stress scene until a step fails (GPU driver reset, the load crashing, the GPU no longer
  responding), refines once, takes a 30 MHz safety margin off, does the same for the memory clock (250 MHz margin) and
  confirms the result with a 3-minute run (backing off once more if that fails). Fails at stock: nothing is
  overclocked; 87 °C: the scan stops. Runs through MSI Afterburner (started for you), needs administrator rights,
  refuses to start while a game is running, and stopping or a failed scan restores the settings from before the scan.
  The result stays unconfirmed until an extended stability test passes.
- CPUs: AMD and Intel offer no supported way for other apps to overclock them, so the CPU's automatic overclock stays a
  guided step in Ryzen Master or Intel XTU.
- 7 new tests for the scan logic (276 total).

## 1.32 – 2026-10-18
- **Money Tweaks starts MSI Afterburner for you.** The Afterburner section on CPU & GPU Tuning now has **Connect**: it
  asks once, starts Afterburner minimized, waits until its control interface is ready and shows the controls. Tick
  "Don't ask again" and it connects by itself whenever you open those controls; a checkbox in the section turns that
  off again. It is never started when Money Tweaks starts, and if Money Tweaks isn't running as administrator (needed
  to control Afterburner) it offers that restart instead. Not installed → a link to MSI's official page (nothing is
  downloaded for you).
- 5 new tests for the launcher (269 total).

## 1.31 – 2026-10-18
(1.30 is skipped: an old v1.30.0 tag already exists on an earlier commit.)
- **GPU clock offsets, undervolt, temperature limit, fan and fan curve** on the CPU & GPU Tuning page, through MSI
  Afterburner's documented third-party control interface (MACMSharedMemory.h in Afterburner's SDK, same protocol as its
  official sample: SDK mutex, write, FLUSH, notify, wait, read back). Needs Afterburner running and Money Tweaks as
  administrator; otherwise the page says so and offers to start Afterburner instead of showing dead controls.
  - Core and memory clock offsets, temperature limit and fixed/automatic fan with Afterburner's own ranges.
  - Undervolt: pick a voltage point and a target clock; that point reaches the clock and every higher-voltage point is
    capped at it. "Reset curve" clears the offsets.
  - Every value is read back from Afterburner; a mismatch undoes the change. Afterburner's settings from before Money
    Tweaks' first change are backed up and restored by Undo all.
  - **Fan curve** (°C → %) driven by Money Tweaks while it's open: must rise with temperature, stay at or above the fan's
    minimum and reach 100% by 85 °C. If the temperature can't be read or a write fails, the fan goes back to automatic;
    closing Money Tweaks does the same, and after a crash the next start puts the fan back on automatic first.
- **CPU temperature** where the firmware doesn't report it (common on desktop boards, including this ASUS B650): read
  from MSI Afterburner's documented monitoring memory (MAHMSharedMemory.h) while Afterburner runs, so the dashboard,
  monitor, tuning page and stability tests (95 °C limit) can use it. Money Tweaks still installs no driver.
- **Per-game tuning**: Gaming Profiles › Edit › "CPU & GPU tuning while active" (Efficiency, Quiet or Maximum headroom).
  Applied when the profile starts, put back exactly (AC and DC values, power limit, clock lock) when it ends, also after
  a crash through the profile session's recovery. Without administrator rights only the CPU part is applied and the
  GPU part is reported as skipped.
- AMD's Ryzen Master libraries were checked and are not used: their exports (e.g. SetPPTLimit, SetCurrentVoltage) are
  undocumented internals, and the documented Monitoring SDK's headers aren't part of the install.
- 11 new tests against byte-exact replicas of the SDK layouts and a fake Afterburner (264 total).

## 1.29 – 2026-10-18
- **New page: CPU & GPU Tuning** (Optimization group; Money Tweaks had no overclocking page before). Built only on
  interfaces the vendors document for other programs; everything else is a guided handoff, never a fake control:
  - **This PC**: CPU, cores/threads, motherboard, BIOS, every GPU with driver version/date, each memory module with its
    running and reported speed, and the vendor tuning tools installed (Ryzen Master, Intel XTU, NVIDIA App, MSI
    Afterburner, AMD Adrenalin). Detected once in the background and cached, so the page opens instantly; Rescan on demand.
  - **Direct controls** (live values, the driver's/Windows' own ranges and steps, tooltips, Apply / Cancel / Reset):
    NVIDIA power limit and GPU clock cap through nvidia-smi (NVIDIA's documented tool; on an RTX 4070 SUPER: 100–242 W,
    210–3105 MHz in 15 MHz steps), and CPU boost mode, maximum processor state and energy performance preference through
    Windows' documented power settings (any Intel or AMD CPU).
  - **Auto tuning**: a 10-second read-only assessment (temperatures, laptop or desktop) recommends a profile;
    Efficiency, Quiet and Maximum headroom profiles are computed from this card's real limits.
  - **Guided handoffs** with exact steps and an Open/Get button: AMD PBO, boost override, PPT/TDC/EDC, Curve Optimizer and
    Eco Mode (Ryzen Master/BIOS), Intel multiplier, voltage offset, PL1/PL2 and BCLK (XTU/BIOS; locked non-K CPUs are
    shown as not available), NVIDIA automatic tuning (NVIDIA App), clock offsets/voltage-frequency curve/fan curve
    (NVIDIA App/Afterburner), AMD Radeon tuning (Adrenalin), XMP/EXPO (BIOS, with the current speed check). Memory
    timings and PBO/Curve Optimizer status are shown as not readable instead of guessed.
  - **Safety**: original values backed up before the first change (and never overwritten), preview of every change,
    confirmation with crash/data-corruption warnings, each value read back (Configured → Applied → Verified; a clock cap
    is verified under load), all-or-nothing rollback, one-click Undo all. If the app closes or crashes before you confirm,
    the next start offers to undo; nothing is ever applied at startup. GPU changes also reset when Windows restarts.
  - **Stability tests**: Quick scan (1 min) and Extended (10/30/60 min), with optional GPU load from the 3D stress scene.
    The CPU load checks its own answers (wrong results = instability), and new WHEA hardware errors, GPU driver resets,
    GPU ≥ 87 °C or CPU ≥ 95 °C fail the test and undo the tuning automatically. A quick scan is reported as preliminary
    only; nothing is ever called "stable".
  - No undocumented MSR, SMU, SMBus or BIOS writes, no arbitrary voltages, no kernel drivers.
- 13 new tests (fake backend for all writes; the real hardware is only read).

## 1.28 – 2026-10-18
- **Smart Tweak Compatibility Engine.** Before any tweak is applied (tweak pages, presets, profiles, scanner, benchmarks:
  everything goes through the engine) Money Tweaks now checks it against this PC:
  - **Real detection** (read-only, once per run, in the background): Windows build and edition, battery, RAM, whether the
    Windows drive is an SSD or a hard disk, real GPUs (virtual/remote adapters ignored), physical printers, fingerprint/face
    sensors, Bluetooth, Remote Desktop, wired network adapters.
  - **Blocks** what can't work or would do nothing: Windows too old (e.g. GPU scheduling before 2004), the two-GPU Fortnite
    setting on a one-GPU PC, Energy-Efficient Ethernet without a wired adapter, and security-weakening tweaks. Unknown
    hardware never blocks.
  - **Blocks dangerous combinations:** a tweak that sets the same setting differently from one that's applied (today: the
    three DNS providers) must wait until the other is undone, because applying on top would make the other's Undo
    restore the wrong value. Overlapping tweaks with the same value are shown as such.
  - **Warns** before risky changes on this hardware: power tweaks on laptops, kernel-memory tweaks under 16 GB RAM,
    prefetch/SysMain off on a hard disk, Print Spooler with printers installed, Windows Hello and Bluetooth services when
    those devices exist, one-way undos, Store app removal.
  - **Already applied** tweaks are detected and not written again (no duplicate change, no second backup).
- **Safe / Advanced / Experimental tiers**: Experimental = boot configuration (bcdedit), device interrupts (MSI mode) and
  kernel memory management; Advanced = Caution tweaks; the rest Safe. Every Recommended tweak is Safe (checked by a test).
  Tweak lists get an Experimental badge and Advanced/Experimental filters.
- **Pre-apply report** for Experimental tweaks, anything with a warning, and Caution tweaks (when confirmation is on):
  tier, findings on this PC, the exact changes, the current values read from Windows, expected effect with its confidence
  (no FPS gain is promised), risk, restart need and how it's undone. Blocked tweaks explain why and change nothing.
  Bulk applies ask separately before including Experimental tweaks. "What changes" shows the findings for this PC.
- Backups before changes, read-back verification and automatic rollback are unchanged and now covered by tests for
  conflicts, unsupported settings, failed steps and rollback (12 new tests; 241 total).

## 1.27 – 2026-10-18
Functional audit fixes (no features removed):
- **No more false "Applied and verified"** for tweaks whose target doesn't exist on the PC (service not installed, task or
  app missing). Presets, profiles and the scanner apply through the engine, which saved a backup, changed nothing and then
  read back as applied because a backup existed. It now refuses with "Not applicable on this PC" and saves no backup.
- **Presets › Undo no longer drops the run from history when nothing was undone.** Declining the administrator restart
  returned "0 reverted, 0 failed", which counted as success. Every bulk apply/undo now reports those tweaks as not changed
  ("Needs administrator rights; nothing was changed") so Presets, Scanner, Dashboard and tweak pages keep their undo state.
- **Undo everything** (Backup & Restore) says what it didn't touch and why: tweaks Windows already changed back, BIOS guides
  and backups of tweaks this version no longer has. It no longer says "Undo complete" when the administrator restart was
  declined or when undos failed.
- **Dashboard and category "Apply recommended"** now include partly applied tweaks, the same set the button counts (it
  could say "Apply recommended (16)" and then "every recommended tweak is already applied").
- **Startup Apps** lists shortcuts in the Startup folders (yours and all users'), like Task Manager; before, the page could
  say "Nothing starts with Windows" while apps did. Each switch is read back from Windows after changing it.
- **Game Booster:** "End boost" checks that Windows really switched back to your previous power plan (it always said
  "restored") and puts the game's priority back; the start toast says when steps failed instead of always "Boost active";
  a failed standby-memory clear is reported as a failure.
- **Fortnite auto-switch off** only says "Your resolution is back to normal" when Windows actually switched back.
- NVIDIA Fortnite restore checks that the profile Money Tweaks created is really gone before reporting "verified".
- Settings › About links show an error with the address if the browser can't be opened (they silently did nothing).
- 10 regression tests.

## 1.26 – 2026-10-17
- Re-release of the safety fixes below under a correct version. They were first tagged v1.2.5, which is lower than 1.25.1,
  so GitHub listed it as the latest release while the app (correctly) saw it as older and offered no update.
- The release workflow now refuses any tag that isn't newer than the newest published release.

Security and safety audit fixes (no features removed; nothing weakens Windows security):
- **Backups can't be destroyed by a crash.** Tweak backups, the NVIDIA backups, the Health Center's update-service backup,
  the benchmark recovery file, settings and the Fortnite/game settings files Money Tweaks edits are now written to a temp
  file and swapped in, so a crash or power cut leaves the old or new file, never a half-written one.
- **A damaged tweak backup is no longer treated as "no backup".** Before, Apply would overwrite it with the already-tweaked
  values and Undo would write Windows defaults over your real originals while reporting success. Both now stop and say
  which file is damaged. Undo without any backup now says it used the usual Windows defaults, not "your" values.
- Damaged NVIDIA/Health state files are kept as .bad copies instead of being overwritten on the next save.
- **Updates:** the downloaded installer is hashed again and locked against changes right before it starts (it waited in a
  user-writable temp folder after verification); download and release-page links must be this repository's GitHub
  releases over HTTPS; the release tag is sanitized before it's used in a folder name.
- **Game Booster** no longer force-closes a chosen app's whole process tree (closing a launcher could kill the game it
  started); it only closes your own session's processes, enforces the protected list when closing, and never closes the
  game being boosted.
- **Macros** re-check the focused app every 100 ms during holds and waits: before, "hold W, wait 60 s" kept the key held
  for up to 60 s after a blocked competitive game took focus. The macro now stops and releases every key.
- **NVIDIA Profile Inspector** is pinned to the file you chose (SHA-256): a different or replaced exe at that path is never
  started. Choose it again once after updating. The older Fortnite NVIDIA path now has the same global/base-profile guard
  as the preset path.
- Health Center links only open HTTPS pages, ms-settings: pages and the two Windows tools the checks use (links are reloaded
  from health.json and were started as-is).
- "Disable dynamic tick" no longer shows as applied when bcdedit reports disabledynamictick No.
- Undoing the Windows 11 classic context menu tweak also removes the empty CLSID key it created.
- 12 regression tests (temp folders and a scratch HKCU key only).

## 1.25.1 – 2026-10-17
- Settings › Hotkeys: shortcuts were drawn as one stretched box with the text stuck to its top edge, so they looked cut
  off and had different widths. Each key is now its own keycap ([Ctrl] + [Shift] + [F10]), vertically centred, in a
  shared column so the descriptions line up. The overlay row no longer repeats its hotkey in the description; it only
  adds a status when the hotkey couldn't be registered.

## 1.25 – 2026-10-17
- **Two Dashboards fixed**: the sidebar's Recent list showed every page except the open one, so Dashboard (always at the
  top) appeared a second time as soon as another page was open. The Recent list is removed from the sidebar (it also
  reshuffled on every page change); recent pages are still offered by the empty Ctrl+K search. Dashboard can't be
  starred any more and is skipped in saved favourites and dashboard shortcuts.
- **Custom sort picker** in All Tweaks and every category page, replacing the Sort drop-down: all options (Default,
  Name A–Z, Caution first, Applied first, Not applied first) in one rounded bar with a highlight that slides to the
  chosen one. All Tweaks' Category drop-down is now a row of category chips with counts.
- Checked off-screen: the highlight moves mid-slide and lands exactly on the option (x and width), the list re-sorts;
  group folding checks still pass.

## 1.24 – 2026-10-17
- **Collapsible tweak groups** (every category page and All Tweaks): click a group heading to fold its tweaks away or
  bring them back, with a sliding height + fade animation and a rotating chevron. Folded groups show their count and
  "Hidden: click to show", are remembered per page, and Collapse all / Expand all sit above the list. While you search,
  every group with matches opens so nothing is hidden behind a folded heading.
- **Closing animations**: the window fades and settles back when you close it, hide it to the tray (Run in the
  background, or closing while running in the background) and eases back in when it's restored from the taskbar or
  tray. Dialogs shrink and fade out instead of vanishing. "What changes" details fade in when opened.
- **Drop-downs are themed**: every drop-down (Game Library profiles, All Tweaks category/sort, overlay app picker) used
  the white Windows control; they now match the app, with a hover glow, a rotating chevron and a drop-down that slides
  and fades open.
- **Text boxes fixed**: the theme applied the inner padding twice and let boxes stretch, so search fields were 51 px
  tall empty blocks; they are now 35 px (same as buttons/drop-downs), centred, and show placeholders ("Search tweaks…",
  "Search games or .exe names…", the booster's process example, the overlay's exe field).
- Tweak cards no longer repeat their notes in gold under every card; the gold "N notes" badge shows them on hover and
  they open with "What changes".
- Page labels above titles follow the sidebar groups (e.g. "GAMING", "TOOLS", "OPTIMIZATION") instead of old section
  names; descriptive labels stay.
- Dashboard: the overview card lines up with the cards below; the tag now reads "N changes by Money Tweaks" (it counts
  Money Tweaks' own backups, which the 53 % ring doesn't); live tiles show guide lines while their first minute fills.
- Ping Stabilizer: the idle chart has guide lines and explains what will appear; the status line says it isn't
  monitoring yet. Fortnite Stretched: the auto-switch status is a sentence instead of a lone "Off.".
- Start with Windows: the Run entry only follows an executable that is Money Tweaks itself, so another program loading
  Money Tweaks' code can't redirect it.
- Checked off-screen with the real pages: a group folds (708 → 0 px, height shrinking mid-animation), unfolds and hands
  its height back to layout, Collapse all folds all 7 General groups (also containers created while scrolling),
  search reopens matching groups, Expand all clears the remembered state; themed TextBox 35 px, ComboBox 36 px.

## 1.23 – 2026-10-17
- **New version numbers**: an update adds 0.1 (1.23 → 1.33) and a minor update adds 0.01 (1.23 → 1.24). The app shows
  two decimals ("1.24"); tags keep a trailing .0 (v1.24.0) so copies up to 1.22.1 can still update in-app.
  `scripts/bump-version.ps1 -Kind update|minor` sets the next version.
- Release notes on GitHub now come from this changelog for each release (they had been stuck on the 1.4.1 text).
- **Smoother everywhere.** Smooth wheel scrolling in every scrollable area (glides over ~250 ms instead of jumping; nested
  lists scroll themselves first). Pages build in with a short staggered rise of their header and cards; the window fades
  in when it opens (also when reopened from the tray). Sidebar groups slide open and closed with a rotating chevron, the
  compact sidebar glides between widths, and the selection pill follows along. Dialogs pop in. Clickable cards lift on
  hover. Tweak cards fade in as they appear.
- Theme-wide control feedback: buttons fade their glow and sheen on hover and squeeze/spring back when pressed; sidebar
  items, chips and window buttons fade their hover; checkbox ticks pop in; tooltips fade in.
- Reduced motion (Settings › Appearance, or Windows' animation setting) turns off the movement (entrances, sliding,
  smooth scrolling, pop-ins); hover feedback stays as short fades.
- Checked on the real main window: every page's entrance finishes fully visible, groups collapse/expand to the exact
  height, compact mode lands on 72/256 px, and a wheel step glides 33 → 78 → 100 → 108 px.

## 1.22.1 – 2026-10-17
- **Run in the background** button in the title bar (next to Minimize), also in Settings › Start with Windows and in search:
  hides Money Tweaks to the tray without exiting. Gaming Profiles auto-switch, Fortnite auto resolution, the overlay
  hotkey/game watcher and the macro emergency stop keep working; pages stop their live sampling while hidden. Open it
  again by double-clicking the tray icon (or starting Money Tweaks); after that, closing the window also hides it to the
  tray until you choose Exit from the tray menu.

## 1.22.0 – 2026-10-17
- **Reorganized app** (every feature kept; all earlier page links and saved data still work). The sidebar is grouped into
  Dashboard, Optimization, Gaming, Monitoring & Benchmarks, Tools, Backup & Restore and Settings. Groups collapse with a
  click, the whole sidebar can be compact (icons with tooltips), Favourites (☆ next to the page name) and Recent pages
  sit at the top, and the breadcrumb shows Group › Page. "Driver & Windows Health" opens the Hardware Monitor at the
  Health Center; Settings entries open their section.
- **Global search (Ctrl+K)** now covers pages, sections, all tweaks, every Game Library game and in-page settings (hotkeys,
  restore points, motion, updates, overlay, Fortnite auto resolution); an empty search shows favourites and recent pages.
- **All Tweaks** library: every tweak in one list with search (name, description, id, group), category filter, sorting
  (name, risk, status), Applied/Not applied/Safe/Caution/Recommended/Has-notes filters, the catalogue audit's known
  limitations and conflicts (with the related tweak) on each card, and a per-tweak Undo that restores the backup and
  reads it back. Category pages get the same sorting, filters, notes and Undo.
- **Dashboard** keeps the overview, live rings, toolkit and PC/applied sections and adds Shortcuts (your favourites),
  Active now (gaming profile, applied tweaks, overlay, Fortnite auto resolution, start with Windows, timer), Games
  (running/installed from the Game Library detector), PC health (last Health Center scan), Recent activity (activity log)
  and Backups (tweak backups, NVIDIA profile backups, latest restore point). Customize hides or shows any card.
- **Settings**: new Appearance card (motion & effects moved here, compact sidebar, dashboard) and Hotkeys card (Ctrl+K,
  emergency stop, overlay hotkey); "Not included" no longer says there's no FPS counter.
- Verified with the real main window (not shown): all 34 navigation entries open the right page and breadcrumb, all 29
  pre-reorganization page tags still create their page, favourites/recent/search work, and section links scroll to the
  section. Fixed during testing: group labels overwritten by icons, and section links before a page's first layout.

## 1.21.0 – 2026-10-17
- **Game Library** (new page, also linked from Gaming Profiles): an editable library of 42 catalogue games plus any game
  you add by its exe or installation folder, with search (name or exe), favourites, category filters and "hide not
  installed". Includes Fortnite, Geometry Dash, Roblox Player and Roblox Studio (separate), MECCHA CHAMELEON, Minecraft
  Java and Bedrock, GTA V Legacy and Enhanced, Rocket League, Brawlhalla, Fall Guys, Among Us, Phasmophobia, Lethal
  Company, Counter-Strike 2, VALORANT, Apex Legends, Marvel Rivals and other multiplayer, party, co-op and indie games.
- **Real detection** (root cause: games were matched by process name only and installs weren't detected at all):
  installs come from Steam's libraryfolders.vdf and app manifests, Epic's .item manifests (DLC ignored), Riot's product
  settings, Roblox/Bloxstrap version folders (newest build), the Minecraft launcher and runtimes, the Bedrock package or
  XboxGames folder, Rockstar's registry keys and Apps & features. An install only counts if the game's own exe is in it.
  Running games are matched by real executable path (QueryFullProcessImageName, no memory access): the process must run
  from the game's install, and generic exes need identity rules (javaw.exe is Minecraft only from Minecraft's runtime or
  with a "Minecraft" window). Statuses: Running, Installed, Not installed (every launcher it can come from was checked),
  Not detected (a launcher couldn't be checked). Test Detection shows every source, the matched exe and full path, and
  why other processes were rejected (e.g. Roblox Studio is not Roblox Player).
- Remove from Library for catalogue and your own games: only Money Tweaks' entry is removed (never uninstalls or deletes
  anything); catalogue games can be restored. Linked custom profiles are offered for export, then deletion is confirmed
  separately. Set/Change exe handles moved installs and updated executables.
- Profiles per game use the existing Gaming Profiles engine (preview, backup, verified restore): choose a profile,
  optionally auto-activate it while the game runs (identity-checked, restored when it closes), and optionally raise only
  the verified game process to High priority (runtime only). Links to real game settings (Potato Graphics, Fortnite
  Stretched, Performance Overlay, CPU priority tweak). Every game can be launched (Steam/Epic URIs, Riot Client, exe).
- Auto-switch, the overlay's game watcher and the benchmark's "game running" warning now use the same identity-checked
  detection.
- Game Profiles category trimmed to the main games (Apex now covers r5apex.exe and r5apex_dx12.exe; GTA V Enhanced
  added). Removed entries stay listed as "undo only" if their setting is still applied on the PC; the Minecraft one
  raised every Java program (javaw.exe) and is no longer offered.

## 1.20.0 – 2026-10-17
- **Live Performance Overlay** (new Tools page "Performance Overlay"): a small transparent, click-through, topmost window
  that never takes focus, showing FPS, frame time, 1% low, a frame-time graph, CPU/GPU load, RAM and temperatures.
  - FPS/frame time from a real-time ETW session on the same Present events PresentMon uses (Microsoft-Windows-DXGI
    Present_Start for DirectX 10/11/12, Microsoft-Windows-D3D9 for DirectX 9), measured as time between Present calls.
    Works in windowed, borderless and exclusive fullscreen; Vulkan/OpenGL are reported as "no frames", not guessed.
    Written directly against the Windows ETW API: no new dependency, no injection, no game memory access.
  - Needs administrator rights or the Performance Log Users group (Windows' rule for ETW); otherwise "needs admin"
    with the other readings still working. Stale sessions are cleaned up; the session only runs while the overlay is shown.
  - CPU/GPU/RAM/CPU temperature reuse the shared LiveStats/HardwareMonitor sampler; GPU temperature added to it through
    NVAPI (in-process, unlike nvidia-smi which causes frame-time spikes). Missing sensors show n/a.
  - Configurable metrics, text size, text/accent colours, background opacity, corner and margin, with a live preview;
    global show/hide hotkey (default Ctrl+Shift+F10, works in background mode); per-game profiles with their own
    layouts; optional automatic monitoring that shows the overlay when a listed game starts and hides it, stopping
    frame timing, when it closes (Fortnite listed by default, off until enabled).
  - Window mode detection (exclusive fullscreen via SHQueryUserNotificationState, borderless vs windowed) explains
    when an overlay can't be drawn on top; honest notes on anti-cheat and games that hide overlays.
  - "Test frame capture" lists the apps drawing frames right now with their FPS. Verified on this PC: DXGI frames at
    240.0 FPS / 4.17 ms (240 Hz display), NVAPI GPU temperature within 1 °C of nvidia-smi, session removed after stop.

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
