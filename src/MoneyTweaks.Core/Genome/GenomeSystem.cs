using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Core.Genome;

/// <summary>
/// The real backend. Every factor goes through an interface Money Tweaks already uses and verifies: Windows processor
/// power settings (powercfg, Microsoft-documented), NVIDIA's nvidia-smi (power limit, clock cap, within the driver's
/// ranges) and the tweak engine (verified toggles with backups). No voltages, no clock offsets, no undocumented writes,
/// nothing that touches a game process.
/// </summary>
public sealed class GenomeSystem : IGenomeBackend
{
    public static string PendingPath => Path.Combine(AppPaths.Root, "genome-restore.json");
    private static int? _capApplied;   // nvidia-smi can't read a clock lock back; remember what was set this run
    private static (DateTime at, string driver)? _driver;

    // ---- discovery ----------------------------------------------------------------------------------------------------

    /// <summary>Adds or refreshes the factors this PC supports. Existing factors keep their levels (past trials refer to
    /// them) and the user's on/off choice. Returns notes about what isn't available.</summary>
    public static List<string> Discover(GenomeStore s)
    {
        var notes = new List<string>();
        void Merge(GenomeFactor f)
        {
            var old = s.Factor(f.Id);
            if (old is null) { s.Factors.Add(f); return; }
            old.Name = f.Name; old.Interface = f.Interface; old.NeedsAdmin = f.NeedsAdmin; old.GpuIndex = f.GpuIndex;
            foreach (var l in f.Levels.Where(l => old.Level(l.Id) is null)) old.Levels.Add(l);
        }
        FactorLevel L(string id, string label, double v) => new() { Id = id, Label = label, Value = v };

        var boost = CpuPowerSettings.Read(CpuPowerSettings.BoostMode);
        if (boost.Readable)
        {
            var levels = new[] { (2, "Aggressive"), (4, "Efficient Aggressive"), (3, "Efficient Enabled"), (1, "Enabled") }
                .Where(x => x.Item1 <= boost.Max).Select(x => L("b" + x.Item1, x.Item2, x.Item1)).ToList();
            Merge(new GenomeFactor { Id = "cpu.boost", Name = "CPU boost mode", Kind = FactorKind.CpuBoostMode, Levels = levels, Interface = "Windows processor power settings (powercfg)" });
        }
        else notes.Add("CPU boost mode: Windows doesn't report it for the active power plan.");
        if (CpuPowerSettings.Read(CpuPowerSettings.Epp).Readable)
            Merge(new GenomeFactor { Id = "cpu.epp", Name = "CPU energy preference", Kind = FactorKind.CpuEpp, Interface = "Windows processor power settings (powercfg)",
                Levels = [L("e0", "0% (performance)", 0), L("e25", "25%", 25), L("e50", "50%", 50), L("e80", "80% (saving)", 80)] });
        if (CpuPowerSettings.Read(CpuPowerSettings.MaxState).Readable && s.Factor("cpu.maxstate") is null)
            Merge(new GenomeFactor { Id = "cpu.maxstate", Name = "CPU maximum state", Kind = FactorKind.CpuMaxState, Enabled = false, Interface = "Windows processor power settings (powercfg)",
                Levels = [L("m100", "100%", 100), L("m99", "99% (turbo off on most CPUs)", 99)] });

        var gpus = NvidiaSmi.Available ? NvidiaSmi.Limits() : [];
        if (gpus.FirstOrDefault() is { } g)
        {
            if (g.PowerLimitSupported && g.DefaultPowerLimitW is { } def)
            {
                var levels = new List<FactorLevel>();
                foreach (var pct in new[] { 70, 80, 90, 100 })
                {
                    double w = Math.Round(Math.Clamp(def * pct / 100, g.MinPowerLimitW!.Value, g.MaxPowerLimitW!.Value));
                    if (levels.All(l => Math.Abs(l.Value - w) > 1)) levels.Add(L("pl" + pct, $"{pct}% ({w:0} W)", w));
                }
                if (g.MaxPowerLimitW > def + 2) levels.Add(L("plmax", $"Max ({g.MaxPowerLimitW:0} W)", Math.Round(g.MaxPowerLimitW!.Value)));
                Merge(new GenomeFactor { Id = "gpu.power", Name = "GPU power limit", Kind = FactorKind.GpuPowerLimit, GpuIndex = g.Index, NeedsAdmin = true, Levels = levels, Interface = "nvidia-smi -pl (NVIDIA, within the driver's range)" });
            }
            if (g.ClockLockSupported && g.MaxGraphics is { } max)
            {
                int Snap(double target) => g.GraphicsClocks.Where(c => c <= target).DefaultIfEmpty(g.GraphicsClocks.Min()).Max();
                int c90 = Snap(max * 0.9), c80 = Snap(max * 0.8);
                Merge(new GenomeFactor { Id = "gpu.cap", Name = "GPU clock cap", Kind = FactorKind.GpuClockCap, GpuIndex = g.Index, NeedsAdmin = true, Interface = "nvidia-smi -lgc/-rgc (NVIDIA)",
                    Levels = [L("none", "No cap", 0), L("c90", $"{c90} MHz (90%)", c90), L("c80", $"{c80} MHz (80%)", c80)] });
            }
        }
        else notes.Add("GPU factors need an NVIDIA GPU with nvidia-smi; other GPUs are tuned in their vendor's app.");
        return notes;
    }

    private static readonly HashSet<string> PerfGroups = ["GPU", "GPU preference", "Gaming", "Power", "Scheduler", "Timers"];
    private static readonly HashSet<string> GameOnlyGroups = ["Input", "Adapter"];

    /// <summary>
    /// Windows tweaks that can be a factor: performance-related, switchable without a restart, measurable by this workload,
    /// and never a security trade-off or an Experimental-tier tweak (experiments must not weaken Windows security).
    /// </summary>
    public static List<Tweak> TweakCandidates(GenomeWorkload w) =>
        TweakCatalog.All.Where(t => TweakStateGuard.Eligible(t, out _)
            && (PerfGroups.Contains(t.Group ?? "") || (w.IsGame && GameOnlyGroups.Contains(t.Group ?? "")))
            && (w.IsGame || SyntheticPlan.NotMeasurable(t) is null)
            && Compatibility.TierOf(t) != TweakTier.Experimental).OrderBy(t => t.Name).ToList();

    public static GenomeFactor TweakFactor(Tweak t) => new()
    {
        Id = "tweak." + t.Id, Name = t.Name, Kind = FactorKind.Tweak, TweakId = t.Id, NeedsAdmin = t.RequiresAdmin,
        Interface = "Money Tweaks tweak engine (backed up, verified)",
        Levels = [new() { Id = "off", Label = "Off", Value = 0 }, new() { Id = "on", Label = "On", Value = 1 }],
    };

    // ---- IGenomeBackend --------------------------------------------------------------------------------------------------

    private static string? Alias(GenomeFactor f) => f.Kind switch
    {
        FactorKind.CpuBoostMode => CpuPowerSettings.BoostMode, FactorKind.CpuEpp => CpuPowerSettings.Epp, FactorKind.CpuMaxState => CpuPowerSettings.MaxState, _ => null,
    };

    public Dictionary<string, string> ReadCurrent(IReadOnlyList<GenomeFactor> factors)
    {
        var d = new Dictionary<string, string>();
        NvidiaGpuReading? gpu = null;
        string Match(GenomeFactor f, double value, double tol, string label)
        {
            if (f.Levels.FirstOrDefault(l => Math.Abs(l.Value - value) <= tol) is { } hit) return hit.Id;
            var id = "cur" + Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (f.Level(id) is null) f.Levels.Add(new FactorLevel { Id = id, Label = $"Your setting ({label})", Value = value });
            return id;
        }
        foreach (var f in factors)
        {
            try
            {
                switch (f.Kind)
                {
                    case FactorKind.CpuBoostMode or FactorKind.CpuEpp or FactorKind.CpuMaxState:
                        var st = CpuPowerSettings.Read(Alias(f)!);
                        if (st.Ac is { } ac) d[f.Id] = Match(f, ac, 0.1, st.Describe(ac));
                        break;
                    case FactorKind.GpuPowerLimit:
                        gpu ??= NvidiaSmi.Read(f.GpuIndex);
                        if (gpu?.PowerLimitW is { } w) d[f.Id] = Match(f, w, 1.5, $"{w:0} W");
                        break;
                    case FactorKind.GpuClockCap:
                        var ts = TuningState.Load();
                        double cap = _capApplied ?? (ts.GpuClocksLocked ? ts.LockedMaxGpuMhz ?? 0 : 0);
                        d[f.Id] = Match(f, cap, 1, cap == 0 ? "no cap" : $"{cap:0} MHz");
                        break;
                    case FactorKind.Tweak when TweakCatalog.Find(f.TweakId!) is { } t:
                        d[f.Id] = TweakEngine.GetState(t) == TweakState.Applied ? "on" : "off";
                        break;
                }
            }
            catch (Exception ex) { Log.Warn($"genome read {f.Id}: {ex.Message}"); }
        }
        return d;
    }

    public (bool ok, string message) Apply(GenomeFactor f, FactorLevel level)
    {
        try
        {
            if (f.NeedsAdmin && !Admin.IsElevated) return (false, "needs administrator rights");
            switch (f.Kind)
            {
                case FactorKind.CpuBoostMode or FactorKind.CpuEpp or FactorKind.CpuMaxState:
                {
                    var alias = Alias(f)!;
                    int v = (int)level.Value;
                    var cur = CpuPowerSettings.Read(alias);
                    var (ok, msg) = CpuPowerSettings.Set(alias, v, cur.Dc ?? v); // plugged-in value only; battery stays yours
                    if (!ok) return (false, msg);
                    return CpuPowerSettings.Read(alias).Ac == v ? (true, "read back") : (false, "Windows reports a different value after setting it");
                }
                case FactorKind.GpuPowerLimit:
                {
                    var (ok, msg) = NvidiaSmi.SetPowerLimit(f.GpuIndex, level.Value);
                    if (!ok) return (false, msg);
                    var r = NvidiaSmi.Read(f.GpuIndex);
                    return r?.PowerLimitW is { } w && Math.Abs(w - level.Value) <= 1.5 ? (true, "read back") : (false, $"driver reports {r?.PowerLimitW:0} W");
                }
                case FactorKind.GpuClockCap:
                {
                    var g = NvidiaSmi.Limits().FirstOrDefault(x => x.Index == f.GpuIndex);
                    var (ok, msg) = level.Value <= 0 ? NvidiaSmi.ResetGpuClocks(f.GpuIndex) : NvidiaSmi.LockGpuClocks(f.GpuIndex, g?.MinGraphics ?? 210, (int)level.Value);
                    if (ok) _capApplied = (int)level.Value;
                    return ok ? (true, "accepted (verified under load by the clock reading)") : (false, msg);
                }
                case FactorKind.Tweak:
                {
                    if (TweakCatalog.Find(f.TweakId!) is not { } t) return (false, "tweak not found");
                    var problems = TweakStateGuard.SetAll([t.Id], level.Id == "on");
                    if (problems.Count > 0) return (false, string.Join("; ", problems));
                    return (TweakEngine.GetState(t) == TweakState.Applied) == (level.Id == "on") ? (true, "read back") : (false, "state didn't change");
                }
                default: return (false, "this setting is changed by you in the game");
            }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public Fingerprint Fingerprint(GenomeWorkload w)
    {
        var inv = TuningDetector.Cached();
        if (_driver is not { } d || DateTime.Now - d.at > TimeSpan.FromMinutes(2))
        {
            var drv = NvidiaSmi.Available ? NvidiaSmi.Limits().FirstOrDefault()?.Driver : null;
            _driver = (DateTime.Now, drv ?? inv?.Gpus.FirstOrDefault()?.DriverVersion ?? "");
        }
        return new Fingerprint
        {
            Cpu = inv?.Cpu ?? "", Gpu = inv?.Gpus.FirstOrDefault(x => x.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))?.Name ?? inv?.Gpus.FirstOrDefault()?.Name ?? "",
            GpuDriver = _driver!.Value.driver, OsBuild = OsBuild(), Bios = inv?.Bios ?? "", Workload = GameVersion(w),
        };
    }

    public static string OsBuild()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return $"{OsInfo.Build}.{k?.GetValue("UBR") ?? 0}";
        }
        catch { return OsInfo.Build.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }

    /// <summary>The game's file version (or its exe's date when it has none): a game update makes results stale.</summary>
    public static string GameVersion(GenomeWorkload w)
    {
        if (!w.IsGame || w.GameExe is not { Length: > 0 } path || !File.Exists(path)) return w.IsGame ? "" : "synthetic-1";
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.ProductVersion ?? v.FileVersion) is { Length: > 0 } ver ? ver : File.GetLastWriteTimeUtc(path).ToString("yyyyMMdd-HHmm");
        }
        catch { return ""; }
    }

    public (double idleCpu, int processes, bool? onAc) Conditions()
    {
        try
        {
            using var hm = new HardwareMonitor();
            Thread.Sleep(500);
            var s = hm.Sample();
            return (s.CpuPercent, s.ProcessCount, BenchmarkSampler.OnAcPower());
        }
        catch { return (0, 0, BenchmarkSampler.OnAcPower()); }
    }

    public Task SettleAsync(Action<string> status, CancellationToken ct) => BenchmarkSampler.SettleAsync(status, ct);

    public void SavePending(Dictionary<string, string> original)
    {
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(PendingPath, JsonSerializer.Serialize(original));
    }

    public void ClearPending() { try { File.Delete(PendingPath); } catch { } }

    public static Dictionary<string, string>? LoadPending()
    {
        try { return File.Exists(PendingPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PendingPath)) : null; }
        catch (Exception ex) { Log.Warn("genome-restore.json unreadable: " + ex.Message); return null; }
    }

    // ---- applying a profile for everyday use ---------------------------------------------------------------------------

    /// <summary>
    /// The CPU/GPU part of a configuration as tuning settings, so it is applied through the normal tuning path (preview,
    /// backup of originals, read-back, rollback, "Undo all tuning") rather than silently. Only differences from now.
    /// </summary>
    public static TuningSettings ToTuning(GenomeStore s, IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> now)
    {
        var t = new TuningSettings();
        foreach (var f in s.Factors.Where(f => config.ContainsKey(f.Id) && config[f.Id] != now.GetValueOrDefault(f.Id)))
        {
            if (f.Level(config[f.Id]) is not { } l) continue;
            t = f.Kind switch
            {
                FactorKind.CpuBoostMode => t with { CpuBoostMode = (int)l.Value },
                FactorKind.CpuEpp => t with { CpuEpp = (int)l.Value },
                FactorKind.CpuMaxState => t with { CpuMaxState = (int)l.Value },
                FactorKind.GpuPowerLimit => t with { GpuPowerLimitW = l.Value },
                FactorKind.GpuClockCap => t with { GpuClockLock = l.Value <= 0 ? (0, 0) : (NvidiaSmi.Limits().FirstOrDefault()?.MinGraphics ?? 210, (int)l.Value) },
                _ => t,
            };
        }
        return t;
    }

    /// <summary>Tweak changes of a configuration (tweak id → on), only differences from now.</summary>
    public static Dictionary<string, bool> TweakChanges(GenomeStore s, IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, string> now) =>
        s.Factors.Where(f => f.Kind == FactorKind.Tweak && config.ContainsKey(f.Id) && config[f.Id] != now.GetValueOrDefault(f.Id))
            .ToDictionary(f => f.TweakId!, f => config[f.Id] == "on");
}
