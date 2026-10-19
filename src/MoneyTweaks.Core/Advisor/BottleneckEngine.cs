namespace MoneyTweaks.Core.Advisor;

/// <summary>
/// Turns a capture into a diagnosis. Every limiter needs several agreeing signals (never one utilization number): a GPU
/// limit needs the GPU busy most of the time and the frame rate under target without a cap; a CPU limit needs the GPU
/// clearly under-used and a core or the whole CPU saturated; a frame cap needs a flat frame rate at a known cap with
/// neither side saturated; stutter from storage needs stutter and slow disk response at the same time; and so on.
/// When the signals disagree, the result says mixed or uncertain instead of picking one.
/// </summary>
public static class BottleneckEngine
{
    public static readonly int[] CommonCaps = [30, 60, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 280, 300, 360];

    public static double? Median(IEnumerable<double?> v) { var l = v.Where(x => x is not null && !double.IsNaN(x.Value)).Select(x => x!.Value).OrderBy(x => x).ToList(); return l.Count == 0 ? null : l[l.Count / 2]; }
    public static double? Pct(IEnumerable<double?> v, double p) { var l = v.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList(); return l.Count == 0 ? null : l[Math.Min(l.Count - 1, (int)(p / 100 * l.Count))]; }
    public static double? Max(IEnumerable<double?> v) { var l = v.Where(x => x is not null).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Max(); }
    public static double? Min(IEnumerable<double?> v) { var l = v.Where(x => x is not null).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Min(); }
    private static double Share<T>(IReadOnlyList<T> s, Func<T, bool> f) => s.Count == 0 ? 0 : s.Count(f) / (double)s.Count;

    public static FrameStats? Frames(IReadOnlyList<double> ms, IReadOnlyList<TelemetrySample> samples)
    {
        if (ms.Count < 30) return null;
        var sorted = ms.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double avgFps = ms.Count * 1000.0 / ms.Sum();
        var slow = sorted.Skip((int)(sorted.Count * 0.99)).ToList();
        double low1 = slow.Count > 0 ? 1000.0 / slow.Average() : avgFps;
        double p99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
        double stutter = ms.Count(x => x > Math.Max(2.5 * median, median + 8)) * 100.0 / ms.Count;
        var perSecond = samples.Skip(2).Select(s => (double)s.Frames).Where(f => f > 0).ToList();
        double cv = perSecond.Count > 2 ? Math.Sqrt(perSecond.Sum(f => (f - perSecond.Average()) * (f - perSecond.Average())) / (perSecond.Count - 1)) / perSecond.Average() : 0;
        return new(avgFps, low1, p99, median, stutter, cv, ms.Count);
    }

    public static Diagnosis Diagnose(Capture c, HardwareFacts hw, AdvisorGoal goal)
    {
        var s = c.Samples.Skip(Math.Min(2, Math.Max(0, c.Samples.Count - 5))).ToList(); // first seconds are scene/probe warm-up
        var frames = Frames(c.FrameMs, c.Samples);
        double? fps = frames?.AvgFps ?? goal.CurrentFps;
        var notes = new List<string>();
        if (frames is null) notes.Add(c.FramesUnavailable is { } why ? $"Frame timing wasn't available ({why}). FPS is the number you entered; frame-cap and stutter checks need frame timing." : "No frame timing; FPS is the number you entered.");
        if (c.Source == "benchmark") notes.Add("Measured with the built-in synthetic 3D scene, not your game: it shows how this PC's GPU/CPU behave under load, but your game's balance can differ. Measure while playing for a game-specific diagnosis.");

        double? gpu = Median(s.Select(x => x.GpuUtil)), core = Median(s.Select(x => (double?)x.CpuBusiestCore)), cpuTot = Median(s.Select(x => (double?)x.CpuTotal));
        double? perf = Median(s.Select(x => x.CpuPerfPct)), cpuT = Max(s.Select(x => x.CpuTempC)), gpuT = Max(s.Select(x => x.GpuTempC));
        double? availMin = Min(s.Select(x => (double?)x.RamAvailableMb)), commit = Max(s.Select(x => x.CommitPct)), faults = Median(s.Select(x => x.HardFaultsPerSec));
        double? latP95 = Pct(s.Select(x => x.DiskLatencyMs), 95), queueP95 = Pct(s.Select(x => x.DiskQueue), 95), other = Median(s.Select(x => x.OtherCpuPct)), gameCpu = Median(s.Select(x => x.GameCpuPct));
        double? vramUsed = Max(s.Select(x => x.VramUsedMb)), vramTotal = Max(s.Select(x => x.VramTotalMb)) ?? (hw.VramGb is { } vg ? vg * 1024 : null);
        double powerShare = Share(s, x => x.GpuPowerCapped), thermalShare = Share(s, x => x.GpuThermalLimited);
        double totalRamMb = s.Count > 0 ? s[0].RamTotalMb : hw.RamGb * 1024;

        // Frame cap: flat frame rate sitting on a known cap (or the refresh rate) while neither side is saturated.
        double? cap = null;
        if (frames is { } f)
        {
            var candidates = CommonCaps.Select(x => (double)x).Append(c.RefreshHz).Where(x => x > 0);
            var hit = candidates.Where(x => Math.Abs(f.AvgFps - x) / x < 0.025).OrderBy(x => Math.Abs(f.AvgFps - x)).FirstOrDefault();
            if (hit > 0 && f.FpsCv < 0.05 && (gpu ?? 0) < 90 && (core ?? 0) < 92) cap = hit;
        }

        var factors = new List<LimiterFactor>();
        void Add(Limiter k, string label, double score, IEnumerable<string> ev)
        {
            if (score < 0.3) return;
            factors.Add(new(k, label, Math.Min(1, score), score >= 0.75 ? "High" : score >= 0.5 ? "Medium" : "Low", ev.Where(e => e.Length > 0).ToList()));
        }
        string Fmt(double? v, string unit = "%", string fmt = "0") => v is { } x ? x.ToString(fmt) + unit : "n/a";

        if (cap is { } cp)
            Add(Limiter.FrameCap, $"Frame cap at {cp:0} FPS", 0.9,
                [$"Average {frames!.AvgFps:0.0} FPS stays within 2.5% of {cp:0}{(Math.Abs(cp - c.RefreshHz) < 1 ? " (your display's refresh rate: V-Sync or a refresh-rate limit)" : "")}, varying only {frames.FpsCv * 100:0.#}% second to second.",
                 $"Neither side is maxed out: GPU {Fmt(gpu)} busy, busiest CPU core {Fmt(core)}.", "So the frame rate is being held back on purpose (V-Sync, an in-game limit or a driver limit), not by the hardware."]);

        bool belowTarget = fps is { } fv && fv < goal.TargetFps * 0.98;
        if (gpu is { } g && cap is null)
        {
            double share95 = Share(s, x => x.GpuUtil >= 95);
            double score = g >= 95 ? 0.9 : g >= 90 ? 0.75 : g >= 85 ? 0.5 : 0;
            if (!belowTarget) score *= 0.6;
            Add(Limiter.Gpu, "Graphics card (GPU) limit", score,
                [$"GPU busy {g:0}% (median), at 95%+ in {share95 * 100:0}% of the seconds measured.",
                 core is { } cr ? $"The CPU has headroom: busiest core {cr:0}%, all cores {Fmt(cpuTot)}." : "",
                 frames is { } fr ? $"{fr.AvgFps:0} FPS average with no frame cap detected." : ""]);
            if (score >= 0.5 && powerShare >= 0.5)
                Add(Limiter.GpuPower, "GPU held back by its power limit", score * 0.85,
                    [$"The driver reports the power limit as the active clock limiter in {powerShare * 100:0}% of samples ({Fmt(Median(s.Select(x => x.GpuPowerW)), " W")} of {Fmt(Median(s.Select(x => x.GpuPowerLimitW)), " W")}).",
                     hw.GpuMaxPowerLimitW is { } mx && hw.GpuPowerLimitW is { } pl && mx > pl + 2 ? $"The card allows up to {mx:0} W (now {pl:0} W)." : "The limit is already at the card's maximum."]);
            if (thermalShare >= 0.2 || gpuT >= 87)
                Add(Limiter.GpuThermal, "GPU running hot / thermal limiting", thermalShare >= 0.2 ? 0.75 : 0.55,
                    [$"Peak GPU temperature {Fmt(gpuT, " °C")}; thermal slowdown reported in {thermalShare * 100:0}% of samples."]);
        }
        if (cap is null && gpu is { } g2 && g2 < 88)
        {
            double busiest = core ?? 0, total = cpuTot ?? 0;
            double score = busiest >= 90 || total >= 90 ? 0.85 : busiest >= 75 ? 0.6 : 0;
            if (!belowTarget) score *= 0.6;
            Add(Limiter.Cpu, total >= 90 ? "CPU limit (all cores busy)" : "CPU limit (main game thread)", score,
                [$"The GPU is under-used: {g2:0}% busy (median), so it's waiting for frames.",
                 $"Busiest CPU core {busiest:0}% (median), all cores {total:0}%.{(busiest is >= 75 and < 90 ? " Windows moves busy threads between cores, so one saturated thread can show as 75–90% on any single core." : "")}",
                 gameCpu is { } gcpu ? $"The game itself uses {gcpu:0}% of all cores (about {gcpu / 100 * hw.Threads:0.#} threads' worth)." : ""]);
            if (score < 0.3 && g2 < 80 && busiest < 75 && fps is not null && belowTarget)
                Add(Limiter.Uncertain, "No clear hardware limit", 0.5,
                    [$"Neither side is saturated (GPU {g2:0}%, busiest core {busiest:0}%) and no frame cap was detected.",
                     "Likely causes: the game engine's own limit, an in-game frame limit that varies, waiting on network/streaming, or a short or changing scene. Hardware upgrades are unlikely to help until this is understood."]);
        }
        if (perf is { } pf && pf < 75 && (cpuTot ?? 0) >= 25)
            Add(Limiter.CpuClocks, "CPU running below its base clock", 0.55,
                [$"Under load the CPU ran at {pf:0}% of its base clock (median): power saving, a power limit or heat is holding it back.",
                 cpuT is { } ct ? $"Peak CPU temperature {ct:0} °C." : "CPU temperature isn't available (start MSI Afterburner or HWiNFO to include it)."]);
        if (cpuT >= 95)
            Add(Limiter.CpuThermal, "CPU at its temperature limit", 0.8, [$"Peak CPU temperature {cpuT:0} °C: modern CPUs slow down at about 95–100 °C."]);
        if (availMin is { } am && (am < Math.Max(1024, totalRamMb * 0.08) || commit >= 90))
            Add(Limiter.Memory, "Not enough RAM (memory pressure)", faults >= 200 ? 0.8 : 0.65,
                [$"Lowest available RAM {am / 1024:0.0} GB of {totalRamMb / 1024:0} GB; committed memory up to {Fmt(commit)}.",
                 faults is { } fl ? $"Hard page faults (reading memory back from disk): {fl:0}/s median." : ""]);
        if (vramUsed is { } vu && vramTotal is { } vt && vu / vt >= 0.95)
            Add(Limiter.Vram, "Graphics memory (VRAM) full", 0.65,
                [$"VRAM use peaked at {vu / 1024:0.0} of {vt / 1024:0.0} GB. When it's full, textures stream from system RAM, which shows up as stutter and lower 1% lows."]);
        if (frames is { StutterPct: >= 1 } st && (latP95 >= 50 || queueP95 >= 4))
            Add(Limiter.Storage, "Storage-related stutter", 0.55 + (hw.GameDisk?.Media == "HDD" ? 0.2 : 0),
                [$"{st.StutterPct:0.#}% of frames were long spikes, while disk response reached {Fmt(latP95, " ms")} (95th percentile) with queue {Fmt(queueP95, "", "0.0")}.",
                 hw.GameDisk is { } gd ? $"The game is on {gd.Name} ({gd.Media}, {gd.Bus})." : "Couldn't tell which disk the game is on."]);
        if (other is { } ot && ot >= 15)
            Add(Limiter.Background, "Background apps competing for the CPU", factors.Any(x => x.Kind == Limiter.Cpu) ? 0.6 : 0.4,
                [$"Apps other than the game used {ot:0}% of all CPU cores (median).", c.TopBackground is { } tb ? $"Biggest: {tb}." : ""]);

        factors = factors.OrderByDescending(x => x.Score).ToList();
        bool mixed = factors.Count >= 2 && factors[0].Score - factors[1].Score < 0.1 && factors[1].Score >= 0.5
                     && factors[0].Kind is not (Limiter.GpuPower or Limiter.GpuThermal) && factors[1].Kind is not (Limiter.GpuPower or Limiter.GpuThermal);
        if (frames is { FpsCv: > 0.25 }) notes.Add($"The frame rate changed a lot during the capture ({frames.FpsCv * 100:0}% second to second): the scene wasn't steady, so projections are less reliable. Capture a steadier stretch for a sharper result.");

        // Ceilings: what the other side could deliver, from measured headroom (a projection, not a measurement).
        Projection? cpuCeil = null, gpuCeil = null;
        if (fps is { } fnow && core is { } co && co >= 15)
        {
            double load = Math.Max(co, cpuTot ?? 0) / 100;
            double x = Math.Min(fnow / load, fnow * 3);
            cpuCeil = new(x * 0.75, x * 1.1, "From CPU headroom: busiest core and total CPU load scaled to 100%. Real scaling is usually lower (threads, memory waits), so the low end is weighted.");
        }
        if (fps is { } fnow2 && gpu is { } gu && gu >= 15)
        {
            double x = Math.Min(fnow2 / (gu / 100), fnow2 * 3);
            gpuCeil = new(x * 0.8, x * 1.05, "From GPU headroom: GPU load scaled to 100%. Real scaling is usually a bit lower.");
        }
        var summary = new Dictionary<string, double>();
        void S(string k, double? v) { if (v is { } x) summary[k] = x; }
        S("Average FPS", frames?.AvgFps); S("1% low FPS", frames?.Low1Fps); S("99th pct frame time (ms)", frames?.P99Ms); S("Stutter frames (%)", frames?.StutterPct);
        S("GPU busy (median %)", gpu); S("Busiest CPU core (median %)", core); S("All CPU cores (median %)", cpuTot); S("CPU clock vs base (%)", perf);
        S("GPU temp (max °C)", gpuT); S("CPU temp (max °C)", cpuT); S("Lowest available RAM (GB)", availMin / 1024); S("Committed memory (max %)", commit);
        S("VRAM used (max %)", vramUsed is { } vu2 && vramTotal is { } vt2 ? vu2 / vt2 * 100 : null); S("Disk response (95th pct ms)", latP95); S("Background CPU (median %)", other);
        return new Diagnosis { Frames = frames, MeasuredFps = frames?.AvgFps, FpsMeasured = frames is not null, CapFps = cap, RefreshHz = c.RefreshHz, Factors = factors, Mixed = mixed, CpuCeiling = cpuCeil, GpuCeiling = gpuCeil, Summary = summary, Notes = notes };
    }
}
