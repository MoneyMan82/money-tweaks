# Hardware knowledge

Owned by hardware-specialist. Add durable facts with a source link and date.

## What the app uses today (from the code, 2026-11-07)
- Sensors: LibreHardwareMonitorLib 0.9.6 (`Monitoring/LhmSource.cs`), elevated sensor helper
  (`ElevatedSensors.cs`), native GPU queries (`GpuNative.cs`), `nvidia-smi` (`Tuning/NvidiaSmi.cs`), WMI
  (System.Management), performance counters. All flow through `SensorHub` (Acquire/Release).
- Tuning: `TuningEngine`, MSI Afterburner integration (`Afterburner.cs`, `AfterburnerLauncher.cs`), CPU auto-tune and
  power settings, fan curves, stability test, auto-OC scanner, curve assistant. Tuning profiles restore on session end.
- GPU 3D models in `src/MoneyTweaks/Assets/Gpu` (only verified files; `gpusByMe/` is excluded from the repo).
- BIOS: guides only (Resizable BAR, XMP/EXPO, C-States) plus an in-app RAM speed check - Money Tweaks never changes
  firmware settings.

## Safety rules
- No kernel drivers shipped. Anything needing one is not feasible.
- Clock/voltage/power/fan changes: bounded to vendor-safe ranges, checked against temperatures, restored on session
  end and after crashes; never automated without the user's approval.
- Laptops: respect battery vs AC; hybrid graphics need per-app GPU preference rather than global switches.
