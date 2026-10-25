Money Tweaks for Linux (preview)
================================

A preview: it was built without being run on Linux hardware. Please report anything that looks wrong through
About & feedback -> Report a problem in the app, or at https://github.com/MoneyMan82/money-tweaks/issues.

Run
  tar -xzf MoneyTweaks-Linux-*-preview-x64.tar.gz
  cd money-tweaks-*
  ./money-tweaks

Needs
  - x86-64 Linux with an X11 desktop or Wayland with XWayland (Avalonia draws the window through X11).
  - fontconfig, libICE and libSM (installed on most desktops; on minimal systems install them with your package manager).
  - polkit (pkexec) to apply tweaks: each change asks for your password. Or start the app with sudo.

What it does
  - Monitor: CPU usage per core, CPU temperature (k10temp, zenpower or coretemp), clocks, memory, GPU (amdgpu sysfs or
    nvidia-smi), network and uptime, read from /proc and /sys.
  - Tweaks: CPU governor, CPU energy preference, swappiness and the memory map limit. Each is backed up before it changes,
    read back afterwards, rolled back if the kernel doesn't keep it, and lasts until the next restart. "Inspect changes"
    shows every file, its value now and after applying, and what Undo restores.

Files
  ~/.local/state/money-tweaks/money-tweaks.log   log
  ~/.local/state/money-tweaks/backups/           original values of applied tweaks

Free and open source (MIT). Interface: Avalonia (MIT). Third-party notices: THIRD-PARTY-NOTICES-LINUX.txt.
