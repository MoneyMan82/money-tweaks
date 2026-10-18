namespace MoneyTweaks.Core.Tuning;

/// <summary>Direct = Money Tweaks changes it through a documented interface; Handoff = do it in the vendor's tool or BIOS
/// (guided); Unavailable = this hardware doesn't have it, or nothing supported can change it.</summary>
public enum Support { Direct, Handoff, Unavailable }

public sealed record TuningCapability(string Area, string Name, Support Support, string Why, string? Tool = null, IReadOnlyList<string>? Steps = null);

/// <summary>
/// What can be tuned on this PC and how. Money Tweaks only changes settings through interfaces the vendors document for
/// third parties (nvidia-smi, Windows power settings). Everything else (PBO, Curve Optimizer, Intel multipliers/voltages,
/// V/F curves, fans, memory timings) is a guided handoff to the official tool or the BIOS: no undocumented MSR, SMU, I2C
/// or BIOS writes, ever.
/// </summary>
public static class TuningCapabilities
{
    private const string NoPublicApi = "the vendor offers no public interface for other programs to change it";

    public static List<TuningCapability> For(TuningInventory inv, IReadOnlyList<NvidiaGpuLimits> nv, bool afterburnerConnected = false)
    {
        var l = new List<TuningCapability>();
        void Add(string area, string name, Support s, string why, string? tool = null, params string[] steps) => l.Add(new(area, name, s, why, tool, steps.Length == 0 ? null : steps));

        // ---- CPU ---------------------------------------------------------------------------------------------------
        Add("CPU", "Boost mode, maximum processor state, energy preference", Support.Direct,
            "Windows power settings (powercfg), documented by Microsoft, on every Intel and AMD CPU. Used for efficiency and quiet profiles.");
        if (inv.IsRyzen)
        {
            var rm = inv.Tool("AMD Ryzen Master");
            string[] rmSteps = ["Open AMD Ryzen Master (Advanced View).", "Pick a profile slot, choose Precision Boost Overdrive or Curve Optimizer.",
                "Curve Optimizer: start at -10 all-core, run Ryzen Master's own Curve Optimizer test or a long stability test, then lower in steps of 5.",
                "Apply & Test, then save the profile. To make it permanent, set the same values in the BIOS (AMD Overclocking › Precision Boost Overdrive)."];
            Add("CPU", "Precision Boost Overdrive (PBO), boost override, PPT/TDC/EDC", Support.Handoff, $"AMD documents these only in Ryzen Master and the BIOS; {NoPublicApi}.", "AMD Ryzen Master", rmSteps);
            Add("CPU", "Curve Optimizer (all-core and per-core undervolt)", Support.Handoff, $"Only Ryzen Master or the BIOS can set it; {NoPublicApi}.", "AMD Ryzen Master", rmSteps);
            Add("CPU", "Eco Mode (lower power limit)", Support.Handoff, "A one-click preset in Ryzen Master (and in the BIOS on most boards).", "AMD Ryzen Master",
                "Ryzen Master › Basic or Advanced View › Eco-Mode, then restart.", "Or BIOS › AMD Overclocking › Eco Mode.");
            Add("CPU", "Status of PBO / Curve Optimizer on this PC", Support.Unavailable, "AMD exposes these values only to Ryzen Master; Money Tweaks can't read them, so it doesn't guess.");
            if (rm?.Path is null) l[^1] = l[^1] with { Why = l[^1].Why + " Ryzen Master isn't installed." };
        }
        else if (inv.IsAmdCpu)
            Add("CPU", "Overclocking / undervolting", Support.Unavailable, "This AMD CPU isn't a Ryzen desktop part supported by Ryzen Master.");
        if (inv.IsIntelCpu)
        {
            string[] xtu = ["Open Intel XTU › Advanced Tuning (or Basic Tuning for one-click options).",
                "Change one value at a time; XTU's own Stress Test and Benchmark verify it.", "Save as a profile. Permanent changes belong in the BIOS."];
            Add("CPU", "Multiplier (ratio) overclocking", inv.IntelUnlocked ? Support.Handoff : Support.Unavailable,
                inv.IntelUnlocked ? $"Intel supports this in XTU and the BIOS; {NoPublicApi}." : "This Intel model has a locked multiplier (only K/KF/KS/HX models are unlocked).", inv.IntelUnlocked ? "Intel XTU" : null, inv.IntelUnlocked ? xtu : []);
            Add("CPU", "Core voltage offset / undervolt", Support.Handoff, $"Only in XTU or the BIOS, and many systems lock undervolting (\"undervolt protection\") after the Plundervolt fixes; {NoPublicApi}.", "Intel XTU", xtu);
            Add("CPU", "Power limits (PL1/PL2) and turbo time", Support.Handoff, $"In XTU or the BIOS; {NoPublicApi}.", "Intel XTU", xtu);
            Add("CPU", "BCLK", Support.Handoff, "Only on boards and CPUs that support it, in the BIOS. Changing BCLK can affect PCIe and storage stability.", null,
                "Use the BIOS only, change it in small steps, and back up important data first.");
        }

        // ---- GPU ---------------------------------------------------------------------------------------------------
        foreach (var g in nv)
        {
            Add("GPU", $"Power limit ({g.Name})", g.PowerLimitSupported ? Support.Direct : Support.Unavailable,
                g.PowerLimitSupported ? $"nvidia-smi -pl, NVIDIA's documented tool. Range {g.MinPowerLimitW:0}–{g.MaxPowerLimitW:0} W (default {g.DefaultPowerLimitW:0} W). Resets when Windows restarts."
                                      : "The driver reports no adjustable power limit for this GPU.");
            Add("GPU", $"GPU clock limit ({g.Name})", g.ClockLockSupported ? Support.Direct : Support.Unavailable,
                g.ClockLockSupported ? $"nvidia-smi -lgc locks the clock range ({g.MinGraphics}–{g.MaxGraphics} MHz). Capping the top clock is the supported way to cut power and heat. Resets at restart."
                                     : "The driver reports no lockable clock range for this GPU.");
            Add("GPU", $"Memory clock limit ({g.Name})", Support.Unavailable,
                g.MemoryLockSupported ? "The driver supports locking it (nvidia-smi -lmc), but a lower memory clock gives no gaming benefit, so Money Tweaks only resets an existing lock."
                                      : "The driver doesn't support memory clock locking on this GPU.");
        }
        if (inv.HasNvidia)
        {
            var app = inv.Tool("NVIDIA App");
            Add("GPU", "Automatic overclock (NVIDIA)", Support.Handoff, "NVIDIA's own automatic tuning scans your card and applies a tested overclock.", "NVIDIA App",
                "NVIDIA App › System › Performance › Automatic Tuning › Start Scan (takes about 20 minutes; don't use the PC meanwhile).", "Turn it off there to go back to stock.");
            if (afterburnerConnected)
            {
                Add("GPU", "Core/memory clock offsets, voltage-frequency curve (undervolt), fan and fan curve", Support.Direct,
                    "Through MSI Afterburner's documented control interface (its SDK's MACM shared memory), with Afterburner's own limits, read-back and rollback.");
                Add("GPU", "Temperature target", Support.Direct, "Through MSI Afterburner, where the card supports it.");
            }
            else
            {
                Add("GPU", "Core/memory clock offsets, voltage-frequency curve (undervolt), fan and fan curve", Support.Handoff,
                    "NVIDIA only exposes these to partner tools. Start MSI Afterburner (and run Money Tweaks as administrator) to control them here through Afterburner's documented interface, or use NVIDIA App for offsets.", "MSI Afterburner",
                    "Press Connect in the MSI Afterburner section above: Money Tweaks starts Afterburner (asking first) and the controls appear.",
                    "Or in Afterburner itself: Ctrl+F › pick a point (e.g. 950 mV) › raise it to your target clock › flatten everything right of it › Apply.");
                Add("GPU", "Temperature target", Support.Handoff, "nvidia-smi has no temperature target for GeForce; MSI Afterburner has one where the card supports it.", "MSI Afterburner");
            }
            if (!NvidiaSmi.Available) Add("GPU", "NVIDIA controls", Support.Unavailable, "nvidia-smi wasn't found; reinstall the NVIDIA driver.");
        }
        if (inv.HasAmdGpu)
            Add("GPU", "Clocks, undervolt, power limit, fan curve, auto-tuning (AMD)", Support.Handoff,
                "AMD's ADLX interface needs AMD's native libraries; Adrenalin has all of these including Auto Undervolt / Auto Overclock.", "AMD Software: Adrenalin",
                "Adrenalin › Performance › Tuning › Manual or Automatic (Undervolt GPU / Overclock GPU / Overclock VRAM).", "Use Stress Test there, then save the profile.");

        // ---- RAM ---------------------------------------------------------------------------------------------------
        var xmp = inv.RamRatedMts is { } rated && inv.RamConfiguredMts is { } conf
            ? conf >= rated ? $"Running at its reported speed ({conf} MT/s)." : $"Running at {conf} MT/s, below the {rated} MT/s the modules report: XMP/EXPO is probably off."
            : "Speed couldn't be read.";
        Add("RAM", "XMP / EXPO profile", Support.Handoff, xmp + " Memory profiles can only be enabled in the BIOS.", null,
            "Restart into the BIOS (usually Del or F2).", "Enable XMP (Intel boards) or EXPO/DOCP (AMD boards), profile 1. Save and exit.",
            "First boot can take several minutes while the memory trains. If it doesn't boot, the BIOS falls back after a few tries or clear CMOS.");
        Add("RAM", "Memory timings (CL, tRCD, tRP, tRAS…)", Support.Unavailable, "Reading or setting timings needs a kernel driver on the SMBus. Money Tweaks doesn't install kernel drivers; change timings only in the BIOS.");
        return l;
    }
}
