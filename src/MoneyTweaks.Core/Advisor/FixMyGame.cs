using System.Globalization;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Health;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Scanner;

namespace MoneyTweaks.Core.Advisor;

public enum GameProblem { LowFps, Stutter, InputDelay, HighPing, Overheating, Crashes, Everything }

public enum FixKind
{
    /// <summary>A catalog tweak: previewed, applied only after approval, backed up and undoable.</summary>
    Tweak,
    /// <summary>Something the player does (a game setting, a cable, cleaning a fan). Money Tweaks changes nothing.</summary>
    Step,
    /// <summary>Opens an existing Money Tweaks tool, which has its own confirmations.</summary>
    Tool,
    /// <summary>Turns on the game's own Gaming Profile (ProfileService: confirmation, admin, restore when turned off).</summary>
    Profile,
}

public enum FixSeverity { Info, Medium, High, Critical }

/// <summary>How a finding is known. Only Confirmed findings are facts read from this PC; the rest are explanations.</summary>
public enum FixStatus { Confirmed, Likely, Possible, NotEnoughEvidence }

/// <summary>A finding: a confirmed fact or a likely cause, in plain words, with the measurements that support it.</summary>
public sealed record FixCause(string Key, string Title, string Plain, IReadOnlyList<string> Evidence, string Confidence, double Score)
{
    public FixSeverity Severity { get; init; } = FixSeverity.Medium;
    public FixStatus Status { get; init; } = FixStatus.Likely;
    /// <summary>Other things that would produce the same readings.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];
    public string? Technical { get; init; }
}

/// <summary>One recommended action. <see cref="Expect"/> never promises a number.</summary>
public sealed record FixAction(string Id, FixKind Kind, string Title, string Plain, string Expect, string? TweakId = null, string? PageTag = null, double Score = 0)
{
    public string Risk { get; init; } = "";
    public bool Restart { get; init; }
    public string Reversible { get; init; } = "";
    public string? ProfileId { get; init; }
    /// <summary>What happened when this was tried before on this PC, and whether that is still current.</summary>
    public string? History { get; init; }
    public string? ForCause { get; init; }
}

/// <summary>The measurement a plan and a re-test are based on (key numbers only; null = not available).</summary>
public sealed class FixMeasurement
{
    public GameProblem Problem { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
    public string What { get; init; } = "";
    public Dictionary<string, double> Metrics { get; init; } = [];
    /// <summary>Max − min of each metric across repeated runs (empty for a single run).</summary>
    public Dictionary<string, double> Spread { get; init; } = [];
    public int Runs { get; init; } = 1;
    public List<string> Limits { get; init; } = [];
    public Diagnosis? Diagnosis { get; init; }
    public DiagnosticReport? Network { get; init; }
    public double TargetFps { get; init; }
    /// <summary>Quick Scan: settings, logs and history only, no live measurement.</summary>
    public bool Live => Diagnosis is not null || Network is not null;
}

public sealed record FixPlan(GameProblem Problem, IReadOnlyList<FixCause> Causes, IReadOnlyList<FixAction> Actions, IReadOnlyList<string> Notes)
{
    /// <summary>Further actions after the best three, for players who want to go on.</summary>
    public IReadOnlyList<FixAction> More { get; init; } = [];
}

public enum FixVerdict { Improved, Same, Worse, Inconclusive }

/// <summary>Re-test result: the verdict on the main number, every number side by side, and whether the problem is still there.</summary>
public sealed record FixComparison(FixVerdict Verdict, bool StillAProblem, string Summary, IReadOnlyList<(string metric, double? before, double? after, string unit, bool higherIsBetter)> Rows, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the first measurement showed the problem at all.</summary>
    public bool HadProblem { get; init; } = true;
    public string VerdictLabel => Verdict switch
    {
        FixVerdict.Improved => "Improved", FixVerdict.Worse => "Worse", FixVerdict.Same => "No measurable improvement", _ => "Inconclusive",
    };
}

/// <summary>
/// Fix My Game: from one problem the player picks to the best 1–3 actions, using only what was read or measured on this
/// PC: the Upgrade Advisor's game capture and bottleneck diagnosis (frame times, CPU/GPU load, clocks, temperatures,
/// throttling, memory, VRAM, disk, background CPU), the network diagnostic (router vs internet ping, jitter, loss),
/// Smart Scanner's findings, Crash Detective's incidents, the display's refresh modes and this game's local history.
/// A cause is only named when that evidence supports it, labelled Confirmed (read from the PC), Likely or Possible,
/// with the other explanations that fit the same readings; otherwise it says there isn't enough evidence. Actions are
/// existing undoable tweaks, the game's Gaming Profile, existing tools, or plain steps. Nothing here changes the system.
/// </summary>
public static class FixMyGame
{
    public static string Label(GameProblem p) => p switch
    {
        GameProblem.LowFps => "Low or unstable FPS",
        GameProblem.Stutter => "Stuttering",
        GameProblem.InputDelay => "Input delay",
        GameProblem.HighPing => "High ping or lag",
        GameProblem.Overheating => "Overheating",
        GameProblem.Crashes => "Crashes or won't start",
        _ => "Not sure: check everything",
    };

    public static string Blurb(GameProblem p) => p switch
    {
        GameProblem.LowFps => "Fewer frames than you want, or FPS that jumps around.",
        GameProblem.Stutter => "Hitches, freezes, uneven frames or slow loading.",
        GameProblem.InputDelay => "Aim or controls feel sluggish or floaty.",
        GameProblem.HighPing => "Lag, rubber-banding, packet loss or high ping.",
        GameProblem.Overheating => "Hot or loud PC, or it slows down after a while.",
        GameProblem.Crashes => "The game or PC crashes, freezes or the game won't launch.",
        _ => "Runs every check that applies and ranks what it finds.",
    };

    /// <summary>Whether a Deep Scan measures the game while it runs.</summary>
    public static bool NeedsGame(GameProblem p) => p is not (GameProblem.HighPing or GameProblem.Crashes);
    /// <summary>Whether a Deep Scan includes the network test.</summary>
    public static bool NeedsNetwork(GameProblem p) => p is GameProblem.HighPing or GameProblem.Everything;
    /// <summary>Whether Quick Scan reads the crash logs.</summary>
    public static bool NeedsCrashes(GameProblem p) => p is GameProblem.Crashes or GameProblem.Everything or GameProblem.Stutter;

    private static string F(double v, string fmt = "0") => v.ToString(fmt, CultureInfo.CurrentCulture);

    // ---- measurement → numbers ------------------------------------------------------------------------------------------
    public static FixMeasurement FromCapture(GameProblem p, Capture c, Diagnosis d, double targetFps)
    {
        var m = new Dictionary<string, double>(d.Summary);
        int n = Math.Max(1, c.Samples.Count);
        m["GPU thermal limit (% of time)"] = c.Samples.Count(s => s.GpuThermalLimited) * 100.0 / n;
        m["GPU power limit (% of time)"] = c.Samples.Count(s => s.GpuPowerCapped) * 100.0 / n;
        if (d.Frames is { } f) { m["Average frame time (ms)"] = 1000.0 / f.AvgFps; m["FPS variation (%)"] = f.FpsCv * 100; }
        if (c.Samples.Select(s => s.VramUsedMb).Where(v => v is not null).Max() is { } vram) m["VRAM used (max GB)"] = vram / 1024;
        var limits = new List<string>();
        if (c.FramesUnavailable is { } why) limits.Add("Frame timing wasn't available: " + why);
        if (!m.ContainsKey("CPU temp (max °C)")) limits.Add("CPU temperature isn't reported on this PC right now (Hardware Monitor can enable it).");
        if (!m.ContainsKey("GPU temp (max °C)")) limits.Add("GPU temperature isn't reported by the driver.");
        return new FixMeasurement { Problem = p, What = c.What, Metrics = m, Limits = limits, Diagnosis = d, TargetFps = targetFps };
    }

    public static FixMeasurement FromNetwork(DiagnosticReport r, GameProblem p = GameProblem.HighPing)
    {
        var m = NetworkMetrics(r);
        return new FixMeasurement { Problem = p, What = r.Connection, Metrics = m, Network = r,
            Limits = ["Generic ping to public servers and your router, not the game server. The game's own ping is shown in its settings or stats overlay."] };
    }

    private static Dictionary<string, double> NetworkMetrics(DiagnosticReport r)
    {
        var m = new Dictionary<string, double>();
        var internet = r.Endpoints.Where(e => e.Label != "Router" && e.Stats.Sent > e.Stats.Lost).ToList();
        if (internet.Count > 0)
        {
            m["Internet ping (ms)"] = internet.Min(e => e.Stats.AvgMs);
            m["Internet jitter (ms)"] = internet.Average(e => e.Stats.JitterMs);
            m["Internet packet loss (%)"] = internet.Average(e => e.Stats.LossPercent);
        }
        else if (r.Endpoints.Any(e => e.Label != "Router")) m["Internet packet loss (%)"] = 100;
        if (r.Endpoints.FirstOrDefault(e => e.Label == "Router") is { Stats.Sent: > 0 } router && router.Stats.Lost < router.Stats.Sent)
        {
            m["Router ping (ms)"] = router.Stats.AvgMs;
            m["Router jitter (ms)"] = router.Stats.JitterMs;
            m["Router packet loss (%)"] = router.Stats.LossPercent;
        }
        return m;
    }

    /// <summary>A Quick Scan: no live numbers, only what the context (settings, logs, display, history) shows.</summary>
    public static FixMeasurement QuickOnly(GameProblem p, string what) => new() { Problem = p, What = what };

    /// <summary>Game capture plus network test (Everything): one measurement with both sets of numbers.</summary>
    public static FixMeasurement Merge(FixMeasurement game, DiagnosticReport net)
    {
        var m = new Dictionary<string, double>(game.Metrics);
        foreach (var (k, v) in NetworkMetrics(net)) m[k] = v;
        return new FixMeasurement { Problem = game.Problem, At = game.At, What = game.What, Metrics = m, Spread = game.Spread, Runs = game.Runs,
            Limits = [.. game.Limits, "Ping is generic ping to public servers and your router, not the game server."], Diagnosis = game.Diagnosis, Network = net, TargetFps = game.TargetFps };
    }

    /// <summary>Repeated runs averaged, with the spread kept as that metric's measured noise.</summary>
    public static FixMeasurement Combine(IReadOnlyList<FixMeasurement> runs)
    {
        if (runs.Count == 1) return runs[0];
        var keys = runs.SelectMany(r => r.Metrics.Keys).Distinct();
        var avg = new Dictionary<string, double>();
        var spread = new Dictionary<string, double>();
        foreach (var k in keys)
        {
            var v = runs.Where(r => r.Metrics.ContainsKey(k)).Select(r => r.Metrics[k]).ToList();
            avg[k] = v.Average();
            if (v.Count > 1) spread[k] = v.Max() - v.Min();
        }
        var first = runs.FirstOrDefault(r => r.Diagnosis?.FpsMeasured == true) ?? runs[0];
        return new FixMeasurement { Problem = first.Problem, At = runs[^1].At, What = first.What, Metrics = avg, Spread = spread, Runs = runs.Count,
            Limits = runs.SelectMany(r => r.Limits).Distinct().ToList(), Diagnosis = first.Diagnosis, Network = runs.Select(r => r.Network).LastOrDefault(n => n is not null), TargetFps = first.TargetFps };
    }

    // ---- plan -----------------------------------------------------------------------------------------------------------
    private static readonly Dictionary<string, string[]> Alternatives = new()
    {
        ["gpu"] = ["An in-game frame limit set close to the FPS you got would look similar.", "A heavier scene than usual (big fights, new areas) pushes the GPU harder."],
        ["cpu"] = ["The game engine's own limit on one main thread (common in older or simulation-heavy games).", "A frame limit that varies with the scene.", "Background apps taking CPU time."],
        ["cap"] = ["The game's own engine cap (some games stop at 60, 120 or 240 FPS).", "A limiter in the NVIDIA/AMD control panel or an overlay (RTSS)."],
        ["storage"] = ["Shader compilation the first time an area loads also causes hitches; it settles after a while.", "Another app using the same disk."],
        ["memory"] = ["A game that slowly uses more memory over a long session (a leak).", "Browser tabs or launchers kept open in the background."],
        ["thermal"] = ["Dust in the heatsink or a fan curve set very quiet.", "A warm room or a laptop on a soft surface."],
        ["ping-home"] = ["Downloads or streaming on another device.", "An overloaded or old router."],
        ["ping-isp"] = ["The test servers being busy; the game's own servers may behave differently.", "Evening congestion at your provider."],
        ["crash-game"] = ["A mod, overlay or recording tool hooking into the game.", "An unstable CPU, GPU or memory overclock (XMP included)."],
        ["driver"] = ["An unstable GPU overclock or undervolt.", "The graphics card overheating.", "A power supply struggling at full load."],
        ["refresh"] = ["The cable or port can't carry the higher rate (older HDMI / DisplayPort cables).", "A different monitor was set as the main display."],
        ["vram"] = ["Texture-streaming settings in the game set higher than the card's memory.", "Another app (browser, video) using graphics memory."],
        ["frametime"] = ["Low FPS itself: a frame that takes longer also shows your input later."],
    };

    private sealed class Builder(GameProblem problem, FixContext ctx, FixMeasurement m)
    {
        public readonly List<FixCause> Causes = [];
        public readonly List<FixAction> Actions = [];
        public readonly List<string> Notes = [];
        public GameProblem Problem => problem;
        public FixContext Ctx => ctx;
        public FixMeasurement M => m;
        public Diagnosis? D => m.Diagnosis;
        public double? V(string k) => m.Metrics.TryGetValue(k, out var v) ? v : null;
        public Finding? Find(string id) => ctx.Findings.FirstOrDefault(f => f.Id == id);
        public LimiterFactor? Factor(Limiter k) => D?.Factors.FirstOrDefault(f => f.Kind == k);

        public void Note(string n) { if (!Notes.Contains(n)) Notes.Add(n); }

        public FixCause Cause(string key, string title, string plain, IEnumerable<string> evidence, string conf, double score,
            FixSeverity? severity = null, FixStatus? status = null, string? technical = null, string? altKey = null)
        {
            var c = new FixCause(key, title, plain, evidence.Where(e => e.Length > 0).ToList(), conf, score)
            {
                Severity = severity ?? (score >= 0.8 ? FixSeverity.High : score >= 0.45 ? FixSeverity.Medium : FixSeverity.Info),
                Status = status ?? (conf is "Low" or "Weak" ? FixStatus.Possible : FixStatus.Likely),
                Alternatives = Alternatives.TryGetValue(altKey ?? key, out var alt) ? alt : [],
                Technical = technical,
            };
            int i = Causes.FindIndex(x => x.Key == key);
            if (i < 0) Causes.Add(c);
            else if (Causes[i].Score < score) Causes[i] = c;
            return c;
        }

        public void Act(FixAction a)
        {
            int i = Actions.FindIndex(x => x.Id == a.Id);
            if (i < 0) Actions.Add(a);
            else if (Actions[i].Score < a.Score) Actions[i] = a;
        }

        public void Tweaks(Finding f, double score, string expect, string causeKey)
        {
            foreach (var id in f.FixTweakIds)
                if (TweakCatalog.Find(id) is { } t)
                    Act(new("tweak:" + id, FixKind.Tweak, t.Name, f.Why, expect, TweakId: id, Score: score) { ForCause = causeKey });
        }

        /// <summary>A Smart Scanner finding: a confirmed setting/state, with its tweak or manual step.</summary>
        public void FromFinding(string id, string title, double score, string expect, string? stepTitle = null, FixSeverity? severity = null)
        {
            if (Find(id) is not { } f) return;
            Cause("finding:" + id, title, f.Why, f.Evidence, f.Confidence.ToString(), score, severity, FixStatus.Confirmed);
            if (f.FixTweakIds.Count > 0) Tweaks(f, score, expect, "finding:" + id);
            else if (f.Manual is { } manual) Act(new("step:" + id, FixKind.Step, stepTitle ?? f.Title, manual, expect, Score: score) { ForCause = "finding:" + id });
        }

        /// <summary>A limiter from the bottleneck diagnosis: several agreeing signals, so Likely (or Possible when weak).</summary>
        public FixCause? FromFactor(Limiter k, string key, string title, string plain, double weight = 1, FixSeverity? severity = null)
        {
            if (Factor(k) is not { } fc) return null;
            return Cause(key, title, plain, fc.Evidence, fc.Confidence, fc.Score * weight, severity,
                technical: $"Bottleneck engine: {fc.Kind} score {fc.Score:0.00} ({fc.Confidence}). " + string.Join(" ", D?.Notes ?? []));
        }
    }

    /// <summary>Tests and callers without context: findings only.</summary>
    public static FixPlan Plan(FixMeasurement m, IReadOnlyList<Finding> findings) => Plan(m, new FixContext { Findings = [.. findings] });

    /// <summary>Findings and the 1–3 most relevant actions. Pure: tests build the inputs by hand.</summary>
    public static FixPlan Plan(FixMeasurement m, FixContext ctx)
    {
        var b = new Builder(m.Problem, ctx, m);
        foreach (var n in m.Limits) b.Note(n);
        var problems = m.Problem == GameProblem.Everything
            ? new[] { GameProblem.LowFps, GameProblem.Stutter, GameProblem.Overheating, GameProblem.InputDelay, GameProblem.HighPing, GameProblem.Crashes }
            : [m.Problem];
        foreach (var p in problems)
        {
            switch (p)
            {
                case GameProblem.LowFps: LowFps(b); break;
                case GameProblem.Stutter: Stutter(b); break;
                case GameProblem.InputDelay: InputDelay(b); break;
                case GameProblem.HighPing: Ping(b); break;
                case GameProblem.Overheating: Heat(b); break;
                case GameProblem.Crashes: Crashes(b); break;
            }
        }
        if (m.Problem is GameProblem.LowFps or GameProblem.Stutter or GameProblem.InputDelay or GameProblem.Everything) Display(b);
        Orchestrate(b);
        Personalize(b);

        if (b.Causes.Count == 0)
            b.Cause("none", "Not enough evidence for a cause", m.Live
                ? "Nothing measured points clearly at one cause, so Money Tweaks won't guess. Try measuring in a heavier or more typical scene, while the problem happens."
                : "The quick checks found nothing that explains this. A Deep Scan measures the game while you play and usually tells more.",
                [], "Low", 0, FixSeverity.Info, FixStatus.NotEnoughEvidence);
        foreach (var a in b.Actions.ToList()) b.Actions[b.Actions.IndexOf(a)] = Describe(a);
        var causes = b.Causes.OrderByDescending(c => c.Severity).ThenBy(c => c.Status).ThenByDescending(c => c.Score).ToList();
        var ranked = b.Actions.Where(a => a.Score >= 0.2).OrderByDescending(a => a.Score).ToList();
        return new(m.Problem, causes, ranked.Take(3).ToList(), b.Notes) { More = ranked.Skip(3).Take(5).ToList() };
    }

    private static void LowFps(Builder b)
    {
        var d = b.D;
        bool all = b.Problem == GameProblem.Everything;
        if (d is not null && d.FpsMeasured == false) b.Note("FPS itself couldn't be measured (needs administrator rights), so this is based on CPU and GPU load only.");
        if (d?.Frames is { } fr && b.M.TargetFps > 0 && fr.AvgFps >= b.M.TargetFps * 0.95 && fr.StutterPct < 1 && d.CapFps is null)
            b.Cause("fps-ok", "FPS already reaches your monitor's refresh rate", $"You got {F(fr.AvgFps)} FPS on a {F(b.M.TargetFps)} Hz display, which is as many frames as the screen can show. High GPU use here is normal, not a problem.",
                [$"Average {F(fr.AvgFps)} FPS, 1% low {F(fr.Low1Fps)} FPS."], "High", 0.3, FixSeverity.Info, FixStatus.Confirmed);
        if (b.FromFactor(Limiter.FrameCap, "cap", $"FPS is held at a limit ({F(d?.CapFps ?? 0)} FPS)", "The frame rate is flat at a fixed number while the hardware isn't maxed out: a frame limit or V-Sync is holding it there on purpose.", 1, FixSeverity.Medium) is { } cap)
        {
            bool belowRefresh = d!.CapFps is { } c && b.M.TargetFps > 0 && c < b.M.TargetFps - 5;
            b.Act(new("step:uncap", FixKind.Step, belowRefresh ? $"Raise the frame limit to at least {F(b.M.TargetFps)} FPS" : "Raise or remove the frame limit",
                "In the game's video settings, turn off V-Sync or set the frame-rate limit higher (at least your monitor's refresh rate). Also check the GPU driver's own frame limiter.",
                "Higher FPS if the hardware has room left, which the measurement suggests it does.", Score: cap.Score * (belowRefresh ? 1 : 0.6)) { ForCause = "cap" });
        }
        bool fpsOk = b.Causes.Any(c => c.Key == "fps-ok");
        if (!fpsOk && b.FromFactor(Limiter.Gpu, "gpu", "Your graphics card is the limit", "The GPU is busy almost all the time, so it decides the frame rate. Windows tweaks can't make it faster; lighter graphics settings can.") is { } gpu)
        {
            b.Act(new("step:gpu-settings", FixKind.Step, "Lower the heaviest graphics settings", "In the game: lower resolution scale, shadows, effects and view distance first, or turn on DLSS / FSR / XeSS if the game has it. These cost the most GPU time.", "More FPS in GPU-heavy scenes. How much depends on the game and settings.", Score: gpu.Score) { ForCause = "gpu" });
            if (b.Ctx.Game?.PotatoId is not null)
                b.Act(new("tool:potato", FixKind.Tool, $"Use Potato Graphics for {b.Ctx.Game.Name}", "Potato Graphics lowers this game's own graphics settings in one step. It backs up the settings file first and has one-click restore.", "Noticeably lighter on the GPU; the game looks simpler.", PageTag: "potato", Score: gpu.Score * 0.8) { ForCause = "gpu" });
        }
        b.FromFactor(Limiter.GpuPower, "gpu-power", "The graphics card hits its power limit", "The driver reports the power limit as what's capping its clocks. That's normal under full load; it only matters if the card allows a higher limit.", 0.7, FixSeverity.Info);
        if (b.FromFactor(Limiter.GpuThermal, "gpu-thermal", "The graphics card is slowing down from heat", "The driver reports thermal slowdown, so it lowers its clocks.", 0.9, FixSeverity.High) is { } gth)
            b.Act(new("step:cooling", FixKind.Step, "Improve cooling", "Clean dust from the GPU and case fans, make sure the case has airflow, and keep a laptop on a hard surface.", "Steadier clocks under load once temperatures drop.", Score: gth.Score * 0.9) { ForCause = "gpu-thermal" });
        if (!fpsOk) b.FromFactor(Limiter.Cpu, "cpu", "The processor can't prepare frames fast enough", "The GPU is waiting for the CPU (often one main game thread). Lower GPU use alone doesn't prove this; here the CPU side was saturated too.");
        b.FromFactor(Limiter.CpuClocks, "cpu-clocks", "The processor runs below its normal speed", "Under load the CPU stayed below its base clock: power saving, a power limit or heat.", 1, FixSeverity.High);
        b.FromFactor(Limiter.CpuThermal, "cpu-thermal", "The processor is at its temperature limit", "CPUs slow themselves down at about 95–100 °C.", 1, FixSeverity.Critical);
        if (b.FromFactor(Limiter.Background, "background", "Other apps are using the processor", "Apps other than the game used a noticeable share of the CPU while you played.") is { } bg)
            b.Act(new("tool:booster", FixKind.Tool, "Close background apps with Game Booster", "Game Booster lists running apps and closes only the ones you tick (it asks first).", "More CPU time for the game while those apps are closed.", PageTag: "booster", Score: bg.Score) { ForCause = "background" });
        b.FromFactor(Limiter.Memory, "memory", "Your PC is running out of memory", "When RAM is full Windows swaps to disk, which slows games down.", 1, FixSeverity.High);
        if (b.FromFactor(Limiter.Vram, "vram", "Graphics memory is full", "Textures no longer fit in the graphics card's memory.") is { } vr)
            b.Act(new("step:textures", FixKind.Step, "Lower texture quality one step", "In the game's graphics settings, lower Textures (or the texture streaming budget) one notch.", "Fewer drops when VRAM was full.", Score: vr.Score) { ForCause = "vram" });
        if (d?.Frames is { FpsCv: > 0.25 } unstable && !all)
            b.Cause("unstable", "FPS jumped around a lot", "The frame rate changed a lot from second to second. Part of that is the scene changing; together with the findings above it explains uneven performance.",
                [$"FPS varied {F(unstable.FpsCv * 100)}% second to second."], "Medium", 0.35, FixSeverity.Info, FixStatus.Confirmed);
        bool cpuSide = b.Factor(Limiter.Cpu) is not null || b.Factor(Limiter.CpuClocks) is not null || b.Factor(Limiter.Background) is not null;
        b.FromFinding("power-saver", "Windows is in Power saver mode", 0.85, "Higher CPU clocks while plugged in.", "Switch to Balanced or High performance", FixSeverity.High);
        b.FromFinding("on-battery", "The laptop is on battery", 0.8, "Full CPU/GPU power once plugged in.", "Plug in the charger", FixSeverity.High);
        if (cpuSide)
        {
            b.FromFinding("power-balanced", "The desktop is on the Balanced power plan", 0.45, "Faster clock ramp-up on some PCs; not guaranteed to change FPS.");
            b.FromFinding("ram-xmp", "Memory runs below its rated speed", 0.6, "Often better CPU-limited FPS and 1% lows.", "Turn on XMP/EXPO in the BIOS (you do this; Money Tweaks never changes BIOS settings)");
        }
        b.FromFinding("ram-pressure", "Memory is nearly full right now", 0.6, "Less swapping to disk while those apps are closed.", "Close the largest apps before playing");
        b.FromFinding("game-dvr", "Background recording is on", 0.35, "Frees the GPU's video encoder and some memory.", severity: FixSeverity.Info);
        b.FromFinding("reverted", "Tweaks you applied earlier were changed back", 0.4, "Your earlier choices are active again.");
        b.FromFinding("reboot-pending", "A restart is needed for applied tweaks", 0.3, "Makes changes you already applied take effect.", "Restart Windows", FixSeverity.Info);
    }

    private static void Stutter(Builder b)
    {
        var fr = b.D?.Frames;
        if (b.M.Live && b.D is not null)
        {
            if (fr is null) { if (b.Problem == GameProblem.Stutter) b.Note("Stutter is measured from frame times, which need administrator rights (Restart as admin) and the game in front."); }
            else if (fr.StutterPct < 0.5 && fr.P99Ms < fr.MedianMs * 2)
                b.Cause("smooth", "Frame pacing looked smooth in this measurement", $"{F(fr.StutterPct, "0.#")}% long frames, 99% of frames within {F(fr.P99Ms, "0.#")} ms (typical {F(fr.MedianMs, "0.#")} ms). Stutter may happen in other scenes, while loading, or in the first minutes (shader compilation).", [], "Medium", 0.2, FixSeverity.Info, FixStatus.Confirmed);
            else
                b.Cause("spikes", "Frames arrive unevenly (frame-time spikes)", "Some frames take much longer than the rest, which you feel as hitches.",
                    [$"{F(fr.StutterPct, "0.#")}% of frames were long spikes; 1% low {F(fr.Low1Fps)} FPS vs {F(fr.AvgFps)} FPS average; 99% of frames within {F(fr.P99Ms, "0.#")} ms (typical {F(fr.MedianMs, "0.#")} ms)."],
                    "High", 0.5, fr.StutterPct >= 2 ? FixSeverity.High : FixSeverity.Medium, FixStatus.Confirmed, altKey: "storage");
        }
        if (b.FromFactor(Limiter.Storage, "storage", "The disk is slow to respond during hitches", "Long frames happened while the disk was busy: the game is waiting for data.", 1, FixSeverity.High) is { } sto)
            b.Act(new("step:ssd", FixKind.Step, "Move the game to an SSD (or free up the disk)", "If the game is on a hard drive, move it to an SSD with the launcher's move option. Pause downloads and updates while playing.", "Fewer loading hitches.", Score: sto.Score) { ForCause = "storage" });
        b.FromFactor(Limiter.Memory, "memory", "Your PC is running out of memory", "Swapping memory to disk causes freezes.", 1, FixSeverity.High);
        if (b.FromFactor(Limiter.Vram, "vram", "Graphics memory is full", "Textures streaming from system RAM cause hitches.") is { } vr)
            b.Act(new("step:textures", FixKind.Step, "Lower texture quality one step", "In the game's graphics settings, lower Textures one notch.", "Fewer hitches when VRAM was full.", Score: vr.Score) { ForCause = "vram" });
        if (b.FromFactor(Limiter.Background, "background", "Other apps are busy in the background", "Bursts of background work can steal time from the game.") is { } bg)
            b.Act(new("tool:booster", FixKind.Tool, "Close background apps with Game Booster", "Game Booster lists running apps and closes only the ones you tick.", "Fewer interruptions while those apps are closed.", PageTag: "booster", Score: bg.Score) { ForCause = "background" });
        b.FromFactor(Limiter.CpuThermal, "cpu-thermal", "The processor is at its temperature limit", "Heat throttling makes frame times uneven.", 1, FixSeverity.Critical);
        b.FromFactor(Limiter.GpuThermal, "gpu-thermal", "The graphics card is slowing down from heat", "Clock drops from heat show up as uneven frames.", 1, FixSeverity.High);
        b.FromFinding("ram-pressure", "Memory is nearly full right now", 0.7, "Less swapping while those apps are closed.", "Close the largest apps before playing", FixSeverity.High);
        if (b.Find("disk-space") is not null)
        {
            b.FromFinding("disk-space", "The system drive is almost full", 0.6, "Room for the page file and shader cache.", "Free up space on the system drive");
            b.Act(new("tool:cleaner", FixKind.Tool, "Free space with PC Cleaner", "PC Cleaner shows disposable files (temp files, caches, update leftovers) for you to review before anything is deleted; personal files are never touched.", "More free space on the system drive.", PageTag: "cleaner", Score: 0.55) { ForCause = "finding:disk-space" });
        }
        b.FromFinding("busy-processes", "Background apps were busy during the check", 0.4, "Fewer interruptions if they stay busy.", "Pause or close the busy apps");
        b.FromFinding("game-dvr", "Background recording is on", 0.35, "Removes the constant background capture work.", severity: FixSeverity.Info);
        if (fr is { StutterPct: >= 0.5 } && b.Factor(Limiter.Cpu) is { } cpu && b.Causes.All(c => c.Score < 0.5))
            b.Cause("cpu", "The processor is the limit", "When the CPU is maxed out, frame times vary more.", cpu.Evidence, cpu.Confidence, cpu.Score * 0.6);
        if (fr is { StutterPct: >= 0.5 })
            b.FromFinding("power-balanced", "The desktop is on the Balanced power plan", 0.3, "Can reduce clock ramp-up hitches on some systems; not guaranteed.");
    }

    private static void InputDelay(Builder b)
    {
        var d = b.D;
        if (b.Problem == GameProblem.InputDelay)
            b.Note("Money Tweaks can't measure the delay between your click and the screen directly (that needs special hardware). It checks the parts it can see: frame time, frame limits and the settings that affect input.");
        if (d?.Frames is { } fr)
        {
            double ft = 1000.0 / fr.AvgFps;
            if (b.Factor(Limiter.FrameCap) is { } cap && d.CapFps is { } capFps && Math.Abs(capFps - d.RefreshHz) < 1.5)
            {
                b.Cause("vsync", "V-Sync (or a limit at your refresh rate) is on", "The frame rate sits exactly at your monitor's refresh rate. V-Sync waits for the screen before showing a frame, which adds delay.", cap.Evidence, cap.Confidence, 0.8, FixSeverity.High, FixStatus.Likely, altKey: "cap");
                b.Act(new("step:vsync", FixKind.Step, "Turn off V-Sync in the game", "Turn V-Sync off in the game's video settings. With G-Sync/FreeSync, keep that on and set a frame limit a few FPS below the refresh rate instead.", "Less delay; without G-Sync/FreeSync you may see tearing.", Score: 0.8) { ForCause = "vsync" });
            }
            if (ft > 12 && b.Problem == GameProblem.InputDelay)
                b.Cause("frametime", "Each frame takes a long time", $"At {F(fr.AvgFps)} FPS each frame takes about {F(ft, "0.0")} ms, and your input waits for the next frame.", [$"Average {F(fr.AvgFps)} FPS ({F(ft, "0.0")} ms per frame), 1% low {F(fr.Low1Fps)} FPS."], "Medium", Math.Min(0.75, ft / 30), FixSeverity.Medium, FixStatus.Confirmed);
            if (b.Factor(Limiter.Gpu) is { Score: >= 0.75 } gpu)
            {
                b.Cause("queue", "The graphics card is maxed out, so frames queue up", "When the GPU is at 99%, frames wait in a queue before it, which adds delay on top of the frame time (render queue).", gpu.Evidence, gpu.Confidence, 0.6, FixSeverity.Medium, FixStatus.Likely, altKey: "gpu");
                b.Act(new("step:reflex", FixKind.Step, "Turn on NVIDIA Reflex / AMD Anti-Lag, or cap FPS slightly lower", "If the game has NVIDIA Reflex or AMD Anti-Lag, turn it on. Otherwise set a frame limit a little below what you get now, so the GPU isn't at 100%.", "Lower delay at the same or slightly lower FPS.", Score: 0.6) { ForCause = "queue" });
            }
        }
        else if (b.M.Live && b.Problem == GameProblem.InputDelay) b.Note("Frame timing wasn't available, so frame time and V-Sync couldn't be checked (Restart as admin).");
        b.FromFinding("mouse-accel", "Mouse acceleration is on", 0.5, "Same hand movement = same distance. It doesn't change delay, but aim feels more direct.");
        b.FromFinding("usb-suspend", "USB ports may power down", 0.35, "Avoids rare mouse or keyboard dropouts.", severity: FixSeverity.Info);
        b.FromFinding("game-mode", "Game Mode is off", 0.3, "Fewer background interruptions while playing.", severity: FixSeverity.Info);
        b.FromFinding("power-saver", "Windows is in Power saver mode", 0.6, "Higher clocks, shorter frame times.", "Switch to Balanced or High performance", FixSeverity.High);
        if (b.Problem == GameProblem.InputDelay)
            b.Act(new("tool:input", FixKind.Tool, "Open the Input Delay Reducer", "It holds Windows' fine 0.5 ms timer while Money Tweaks is open and groups the input-related tweaks (mouse, keyboard, USB, timers) in one place, each undoable.", "More consistent input timing; the effect depends on the game.", PageTag: "input", Score: 0.4));
    }

    private static void Ping(Builder b)
    {
        if (b.M.Network is not { } r) { if (b.Problem == GameProblem.HighPing) b.Note("Run a Deep Scan to test the connection (router and internet, 20 s)."); }
        double? rj = b.V("Router jitter (ms)"), rl = b.V("Router packet loss (%)"), ij = b.V("Internet jitter (ms)"), il = b.V("Internet packet loss (%)"), ip = b.V("Internet ping (ms)"), rp = b.V("Router ping (ms)");
        bool wifi = b.M.Network?.Connection.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) == true;
        if (b.M.Network is not null)
        {
            if (rl > 1 || rj > 10 || rp > 15)
            {
                b.Cause("ping-home", "The problem is between your PC and your router", "Even the router, right next to you, answers slowly or loses packets.",
                    [$"Router: {F(rp ?? 0)} ms, jitter {F(rj ?? 0, "0.#")} ms, loss {F(rl ?? 0, "0.#")}%.", wifi ? "Connected over Wi-Fi." : ""], "High", 0.9,
                    rl > 5 ? FixSeverity.Critical : FixSeverity.High, FixStatus.Confirmed);
                b.Act(new("step:cable", FixKind.Step, wifi ? "Use an Ethernet cable (or move closer to the router)" : "Check the cable and router", wifi ? "Wi-Fi interference causes this. A cable fixes it in most homes; otherwise get closer to the router or use the 5 GHz band." : "Try another cable or router port, and restart the router.", "Steadier ping and less packet loss inside your home.", Score: 0.9) { ForCause = "ping-home" });
            }
            else if (rp is null && il is not null)
                b.Note("Your router doesn't answer ping, so home and internet problems can't be told apart.");
            if ((il > 1 || ij > 10) && !(rl > 1 || rj > 10))
            {
                b.Cause("ping-isp", "Your home network is fine; the delay is beyond your router", "The router answers quickly, but servers on the internet show jitter or loss: your provider or the route to the servers.",
                    [$"Internet: {F(ip ?? 0)} ms, jitter {F(ij ?? 0, "0.#")} ms, loss {F(il ?? 0, "0.#")}%; router stable."], "Medium", 0.75, il > 5 ? FixSeverity.Critical : FixSeverity.High, FixStatus.Confirmed);
                b.Act(new("step:isp", FixKind.Step, "Check other devices and contact your provider", "Pause downloads and streams on other devices. If it continues at the same times of day, report the packet loss to your internet provider.", "Nothing on this PC can fix provider issues; this tells you where to look.", Score: 0.6) { ForCause = "ping-isp" });
            }
            if (ip > 80 && !(il > 1 || ij > 10))
            {
                b.Cause("ping-far", "The servers are far away", "The connection is stable but every packet takes a while: usually distance to the server.", [$"Lowest internet ping {F(ip ?? 0)} ms with low jitter."], "Medium", 0.55, FixSeverity.Medium, FixStatus.Likely);
                b.Act(new("step:region", FixKind.Step, "Pick the closest server region in the game", "In the game's settings, choose the matchmaking region nearest to you.", "Lower ping if a closer region exists.", Score: 0.55) { ForCause = "ping-far" });
            }
            bool unstable = b.Causes.Any(c => c.Key is "ping-home" or "ping-isp");
            if (!b.Causes.Any(c => c.Key.StartsWith("ping-", StringComparison.Ordinal)) && il is not null)
            {
                // Measured fine: say so, and don't offer setting changes the measurement doesn't support.
                b.Cause("ping-ok", "The connection measured stable", "Low jitter and no meaningful packet loss to your router or the internet in this test. If the game still lags, it's likely the game server or it happens at other times: test again when it happens.",
                    [$"Internet {F(ip ?? 0)} ms, jitter {F(ij ?? 0, "0.#")} ms, loss {F(il ?? 0, "0.#")}%."], "Medium", 0.3, FixSeverity.Info, FixStatus.Confirmed);
                if (b.Find("wifi-only") is { } w) b.Cause("finding:wifi-only", "You're on Wi-Fi", w.Why + " It measured fine this time, but Wi-Fi can vary.", w.Evidence, w.Confidence.ToString(), 0.2, FixSeverity.Info, FixStatus.Confirmed);
                return;
            }
            if (unstable)
                b.Act(new("tool:ping", FixKind.Tool, "Watch it live with the Ping Stabilizer", "The Ping Stabilizer graphs latency, jitter and loss continuously, so you can see whether spikes match the lag in your game.", "Shows when and where spikes happen.", PageTag: "ping", Score: 0.3));
        }
        if (b.Problem == GameProblem.HighPing || b.M.Network is not null)
        {
            b.FromFinding("wifi-only", "You're on Wi-Fi", 0.5, "Steadier ping on a cable.", "Use an Ethernet cable");
            if (b.M.Network is not null) b.FromFinding("adapter-power", "Network adapter power saving is on", 0.35, "Fewer drop-outs on adapters that misbehave with power saving.", severity: FixSeverity.Info);
            foreach (var f in b.Ctx.Findings.Where(f => f.Id.StartsWith("eth-speed-", StringComparison.Ordinal)))
                b.Cause("finding:" + f.Id, f.Title, f.Why, f.Evidence, f.Confidence.ToString(), 0.25, FixSeverity.Info, FixStatus.Confirmed);
        }
        if (b.Problem == GameProblem.HighPing) b.Note("DNS changes don't lower in-game ping, so they aren't recommended here.");
    }

    private static void Heat(Builder b)
    {
        double? ct = b.V("CPU temp (max °C)"), gt = b.V("GPU temp (max °C)"), gth = b.V("GPU thermal limit (% of time)");
        if (b.M.Live && b.D is not null && ct is null && gt is null) b.Note("No temperature readings are available. Open Hardware Monitor to enable CPU temperature (it installs a sensor driver only after you confirm).");
        if (b.Problem == GameProblem.Overheating && b.D is null) b.Note("Temperatures are measured during a Deep Scan while you play.");
        if (ct >= 90)
            b.Cause("cpu-hot", "The processor runs very hot", ct >= 95 ? "It's at the point where it slows itself down to protect itself." : "It's close to the temperature where it starts slowing down.",
                [$"Peak CPU temperature {F(ct!.Value)} °C."], ct >= 95 ? "High" : "Medium", ct >= 95 ? 0.9 : 0.6, ct >= 95 ? FixSeverity.Critical : FixSeverity.High, FixStatus.Confirmed, altKey: "thermal");
        if (gt >= 83 || gth >= 20)
            b.Cause("gpu-hot", "The graphics card runs very hot", gth >= 20 ? "The driver reports it's slowing down because of heat." : "It's running near its usual maximum temperature.",
                [gt is { } g ? $"Peak GPU temperature {F(g)} °C." : "", gth is { } t && t > 0 ? $"Thermal slowdown in {F(t)}% of the seconds measured." : ""],
                gth >= 20 ? "High" : "Medium", gth >= 20 ? 0.9 : 0.55, gth >= 20 ? FixSeverity.Critical : FixSeverity.High, FixStatus.Confirmed, altKey: "thermal");
        b.FromFactor(Limiter.CpuClocks, "cpu-clocks", "The processor drops below its normal speed", "Often a sign of heat or a power limit.", 0.7, FixSeverity.High);
        bool hot = b.Causes.Any(c => c.Key is "cpu-hot" or "gpu-hot" or "gpu-thermal" or "cpu-thermal");
        if (hot)
        {
            b.Act(new("step:cooling", FixKind.Step, "Clean dust and improve airflow", "Blow dust out of the fans and filters, keep the side vents clear, and lift a laptop off soft surfaces. Make sure every case fan spins.", "Lower temperatures and steadier clocks. Usually the biggest win.", Score: 0.9) { ForCause = "cpu-hot" });
            if (b.D?.Frames is { } fr && b.D.CapFps is null && b.M.TargetFps > 0 && fr.AvgFps > b.M.TargetFps * 1.05)
                b.Act(new("step:fpscap", FixKind.Step, "Set a frame limit at your refresh rate", $"You get about {F(fr.AvgFps)} FPS on a {F(b.M.TargetFps)} Hz monitor. A limit at {F(b.M.TargetFps)} FPS stops the PC from making frames the screen can't show.", "Less heat and fan noise; nothing visible is lost.", Score: 0.75) { ForCause = "gpu-hot" });
            b.Act(new("tool:tuning", FixKind.Tool, "Lower the GPU power limit or undervolt", "CPU & GPU Tuning can lower the graphics card's power limit and has an undervolt assistant. Every change there is confirmed and can be reverted.", "Less heat for a small, measurable performance cost.", PageTag: "tuning", Score: 0.5) { ForCause = "gpu-hot" });
        }
        else if (b.Problem == GameProblem.Overheating && (ct is not null || gt is not null))
            b.Cause("temps-ok", "Temperatures looked normal in this measurement", "Neither the processor nor the graphics card was near its limit while you played.",
                [ct is { } c2 ? $"CPU peak {F(c2)} °C." : "", gt is { } g2 ? $"GPU peak {F(g2)} °C." : ""], "Medium", 0.1, FixSeverity.Info, FixStatus.Confirmed);
        if (b.Problem == GameProblem.Overheating) b.FromFinding("on-battery", "The laptop is on battery", 0.3, "Laptops behave differently on battery.", "Plug in the charger", FixSeverity.Info);
        if (b.D is not null) b.Note("Temperatures depend on the scene and the room: re-test in the same place for a fair comparison.");
    }

    /// <summary>Crash Detective's incidents from the last 7 days: the game's own crashes, driver resets, blue screens.</summary>
    private static void Crashes(Builder b)
    {
        if (b.Ctx.Crashes is not { } report)
        {
            if (b.Problem == GameProblem.Crashes) b.Note("The crash logs couldn't be read, so crashes can't be checked.");
            return;
        }
        var game = b.Ctx.Game;
        bool Ours(CrashIncident i) => game?.Exe is { } exe && i.App is { } app && (app.Equals(exe, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(app).Equals(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase));
        var mine = report.Incidents.Where(i => i.Kind is CrashKind.AppCrash or CrashKind.AppHang && Ours(i)).ToList();
        var driver = report.Incidents.Where(i => i.Kind is CrashKind.GpuDriverReset || (i.Kind == CrashKind.LiveKernelEvent && i.Facts.Any(f => f.Text.Contains("graphics", StringComparison.OrdinalIgnoreCase)))).ToList();
        var system = report.Incidents.Where(i => i.Kind is CrashKind.BlueScreen or CrashKind.HardwareError or CrashKind.UnexpectedRestart).ToList();
        string When(IEnumerable<CrashIncident> xs) => string.Join(", ", xs.Take(4).Select(x => x.Time.ToString("g", CultureInfo.CurrentCulture)));
        string? Lead(IEnumerable<CrashIncident> xs) => xs.SelectMany(x => x.Hypotheses).OrderByDescending(h => h.Confidence).FirstOrDefault() is { } h ? $"{h.ConfidenceLabel}: {h.Cause}. {h.Caveat}" : null;

        if (mine.Count > 0)
        {
            var c = b.Cause("crash-game", $"{game!.Name} crashed {mine.Count} time(s) in the last 7 days", "Windows logged the game itself crashing or stopping responding.",
                [.. mine.Take(3).SelectMany(i => i.Facts.Take(1)).Select(f => f.Text), $"When: {When(mine)}."], "High", 0.85,
                mine.Count >= 3 ? FixSeverity.Critical : FixSeverity.High, FixStatus.Confirmed, Lead(mine), "crash-game");
            b.Act(new("tool:crash", FixKind.Tool, "See the crash details in Crash Detective", "Crash Detective shows each crash with what Windows logged, the suspected causes with their evidence, and step-by-step fixes.", "You see exactly which module failed and what usually fixes it.", PageTag: "monitor#crash", Score: 0.8) { ForCause = "crash-game" });
            if (mine.Count >= 2)
            {
                b.Cause("files", "The game's files may be damaged or out of date", "Repeated crashes of the same game are often fixed by the launcher's file check. This is a possibility, not something the logs can prove.",
                    [$"{mine.Count} crashes of {game.Exe} in 7 days."], "Low", 0.5, FixSeverity.Medium, FixStatus.Possible, altKey: "crash-game");
                b.Act(new("step:verify", FixKind.Step, "Verify (repair) the game files in its launcher", "Steam: right-click the game › Properties › Installed Files › Verify integrity. Epic: Library › the game's ⋯ › Manage › Verify. Riot / Battle.net / EA: the launcher's Repair or Scan option. Roblox: reinstall from roblox.com.", "Replaces damaged or missing files; doesn't touch your settings or progress.", Score: 0.6) { ForCause = "files" });
            }
        }
        if (driver.Count > 0)
        {
            b.Cause("driver", $"The graphics driver crashed {driver.Count} time(s)", "Windows logged the GPU driver stopping and being restarted (black screen or a game closing with a driver error).",
                [.. driver.Take(3).SelectMany(i => i.Facts.Take(1)).Select(f => f.Text), $"When: {When(driver)}."], "High", 0.8, FixSeverity.High, FixStatus.Confirmed, Lead(driver), "driver");
            b.Act(new("tool:health", FixKind.Tool, "Check the graphics driver in the Health Center", "The Driver & Windows Health Center reads the installed driver's version and age and points to the official download. Nothing is installed automatically.", "Tells you whether a driver update is the next step.", PageTag: "monitor#health", Score: 0.7) { ForCause = "driver" });
        }
        if (system.Count > 0)
        {
            b.Cause("system-crash", $"{system.Count} blue screen(s), hardware error(s) or sudden restart(s)", "Windows itself went down, not just the game. These need attention before game settings.",
                [.. system.Take(3).Select(i => $"{i.KindLabel} at {i.Time.ToString("g", CultureInfo.CurrentCulture)}: {i.Title}")], "High", 0.9, FixSeverity.Critical, FixStatus.Confirmed, Lead(system), "driver");
            b.Act(new("tool:crash-system", FixKind.Tool, "Investigate in Crash Detective", "Shows stop codes, the drivers involved, hardware error details and fixes ranked by evidence.", "A specific next step instead of guessing.", PageTag: "monitor#crash", Score: 0.85) { ForCause = "system-crash" });
        }
        if (b.Problem == GameProblem.Crashes && mine.Count == 0 && driver.Count == 0 && system.Count == 0)
        {
            b.Cause("no-crashes", game is null ? "No crashes logged in the last 7 days" : $"No crashes of {game.Name} logged in the last 7 days",
                "Windows has no record of the game, the graphics driver or Windows crashing recently. A game that closes itself or fails to launch without an error may not be logged: run this again right after it happens.",
                [$"Read: {string.Join(", ", report.SourcesRead)}."], "Medium", 0.2, FixSeverity.Info, FixStatus.Confirmed);
            if (game is null) b.Note("Pick your game above so its crashes can be matched.");
        }
    }

    /// <summary>The display's refresh rate against what it supports, and frame limits against the refresh rate.</summary>
    private static void Display(Builder b)
    {
        var primary = b.Ctx.Displays.FirstOrDefault(d => d.Primary) ?? b.Ctx.Displays.FirstOrDefault();
        if (primary is null) return;
        var cur = primary.Current;
        int max = primary.RatesFor(cur.Width, cur.Height).DefaultIfEmpty(cur.Hz).Max();
        if (max >= cur.Hz + 10)
        {
            b.Cause("refresh", $"Your monitor runs at {cur.Hz} Hz but supports {max} Hz", $"At {cur.Hz} Hz the screen shows at most {cur.Hz} frames per second, however high your FPS is. This is a common one: Windows often keeps 60 Hz after a new monitor or cable.",
                [$"{primary.MonitorName}: {cur} now; the driver reports {cur.Width}×{cur.Height} @ {max} Hz as available."], "High", 0.85, FixSeverity.High, FixStatus.Confirmed, altKey: "refresh");
            b.Act(new("step:refresh", FixKind.Step, $"Set the monitor to {max} Hz", $"Windows Settings › System › Display › Advanced display › Choose a refresh rate › {max} Hz. (Stretched Resolution in Money Tweaks can also switch it, with an automatic revert unless you confirm.)", "Smoother motion and less delay on screen; FPS above the old refresh rate becomes visible.", Score: 0.85) { ForCause = "refresh" });
        }
    }

    /// <summary>Existing features for this game: its Gaming Profile when it isn't on.</summary>
    private static void Orchestrate(Builder b)
    {
        if (b.Ctx.Game is { ProfileId: { } pid, ProfileName: { } pname, ProfileActive: false } g
            && b.Problem is GameProblem.LowFps or GameProblem.Stutter or GameProblem.InputDelay or GameProblem.Everything
            && b.Causes.Any(c => c.Status != FixStatus.NotEnoughEvidence && c.Severity >= FixSeverity.Medium && c.Key is not ("refresh" or "crash-game" or "system-crash" or "driver")))
            b.Act(new("profile:" + pid, FixKind.Profile, $"Turn on the “{pname}” gaming profile", $"{g.Name} has its own Gaming Profile. It applies that profile's tweaks, power plan and timer while you play and restores and verifies everything when you turn it off.", "The settings chosen for this game are active while you play.", Score: 0.35) { ProfileId = pid });
    }

    /// <summary>
    /// This game's history: an action that didn't help last time is ranked lower (and says so), one that helped but is
    /// no longer active is ranked higher, and outcomes measured with another driver or game version are marked outdated.
    /// </summary>
    private static void Personalize(Builder b)
    {
        if (b.Ctx.Game is not { } game) return;
        var h = b.Ctx.History;
        int recurring = h.Recurring(game.Key, b.Problem);
        if (recurring >= 2)
            b.Cause("recurring", $"This keeps coming back ({recurring} checks in 30 days)", $"You've checked {Label(b.Problem).ToLowerInvariant()} for {game.Name} {recurring} times this month. Look at the history below for what was tried; a recurring problem after a fix often points at hardware (heat, driver) rather than settings.",
                [], "High", 0.3, recurring >= 4 ? FixSeverity.Medium : FixSeverity.Info, FixStatus.Confirmed);
        for (int i = 0; i < b.Actions.Count; i++)
        {
            var a = b.Actions[i];
            if (h.Outcome(game.Key, b.Problem, a.Id) is not { } o) continue;
            var when = o.At.ToString("d", CultureInfo.CurrentCulture);
            bool outdated = (o.Driver is not null && b.Ctx.GpuDriver is not null && o.Driver != b.Ctx.GpuDriver) || (o.GameVersion is not null && game.Version is not null && o.GameVersion != game.Version);
            string stale = outdated ? " That was measured with a different graphics driver or game version, so it may be outdated: re-test." : "";
            bool active = a.TweakId is null || Engine.BackupStore.Exists(a.TweakId);
            b.Actions[i] = o.Verdict switch
            {
                FixVerdict.Same or FixVerdict.Worse when !outdated => a with { Score = a.Score * 0.5, History = $"Tried on {when}: {(o.Verdict == FixVerdict.Worse ? "it got worse" : "no measurable improvement")}{(o.Undone ? " (undone)" : "")}." },
                FixVerdict.Improved when !active => a with { Score = a.Score + 0.2, History = $"Helped on {when}, but it's no longer active (undone or changed back).{stale}" },
                FixVerdict.Improved => a with { History = $"Helped on {when} and is still active.{stale}" },
                _ => a with { History = $"Tried on {when}{(o.Verdict is null ? " (not re-tested)" : $": {o.Verdict}")}.{stale}" },
            };
        }
    }

    /// <summary>Risk, restart and reversibility in words, from the tweak itself for tweaks.</summary>
    private static FixAction Describe(FixAction a) => a.Kind switch
    {
        FixKind.Tweak when a.TweakId is { } id && TweakCatalog.Find(id) is { } t => a with
        {
            Risk = t.Risk == Risk.Caution ? "Caution: " + (t.Warning ?? "a real trade-off; read the preview.") : "Low: a documented Windows setting.",
            Restart = t.RequiresReboot,
            Reversible = "Yes. The current values are backed up first; Undo puts them back and reads them back.",
        },
        FixKind.Profile => a with { Risk = "Low: the profile's own settings.", Reversible = "Yes. Turning the profile off restores and verifies everything it changed." },
        FixKind.Tool => a with { Risk = "None until you confirm something in the tool.", Reversible = "The tool shows how to undo each change." },
        _ => a with { Risk = "Your choice: Money Tweaks changes nothing here.", Reversible = "You can change it back the same way." },
    };

    // ---- re-test --------------------------------------------------------------------------------------------------------
    private sealed record Key(string Metric, string Unit, bool HigherIsBetter, double MinChange);

    private static readonly Key[] Context =
    [
        new("GPU busy (median %)", "%", true, double.NaN), new("Busiest CPU core (median %)", "%", true, double.NaN),
        new("CPU temp (max °C)", " °C", false, double.NaN), new("GPU temp (max °C)", " °C", false, double.NaN),
        new("Lowest available RAM (GB)", " GB", true, double.NaN), new("VRAM used (max GB)", " GB", false, double.NaN),
    ];

    private static Key[] Keys(GameProblem p) => p switch
    {
        GameProblem.LowFps => [new("Average FPS", " FPS", true, 0.05), new("1% low FPS", " FPS", true, 0.08), new("FPS variation (%)", "%", false, double.NaN), .. Context],
        GameProblem.Stutter => [new("Stutter frames (%)", "%", false, 0.25), new("1% low FPS", " FPS", true, 0.08), new("99th pct frame time (ms)", " ms", false, 0.1), new("Average FPS", " FPS", true, double.NaN), .. Context],
        GameProblem.InputDelay => [new("Average frame time (ms)", " ms", false, 0.05), new("99th pct frame time (ms)", " ms", false, 0.1), new("Average FPS", " FPS", true, double.NaN), .. Context],
        GameProblem.HighPing => [new("Internet jitter (ms)", " ms", false, 0.25), new("Internet packet loss (%)", "%", false, double.NaN), new("Internet ping (ms)", " ms", false, 0.1), new("Router jitter (ms)", " ms", false, double.NaN), new("Router packet loss (%)", "%", false, double.NaN)],
        GameProblem.Overheating => [new("CPU temp (max °C)", " °C", false, 0.04), new("GPU temp (max °C)", " °C", false, 0.04), new("GPU thermal limit (% of time)", "%", false, double.NaN), new("Average FPS", " FPS", true, double.NaN), new("GPU busy (median %)", "%", true, double.NaN)],
        GameProblem.Everything => [new("Average FPS", " FPS", true, 0.05), new("1% low FPS", " FPS", true, 0.08), new("Stutter frames (%)", "%", false, double.NaN), new("99th pct frame time (ms)", " ms", false, double.NaN),
            .. Context, new("Internet ping (ms)", " ms", false, double.NaN), new("Internet jitter (ms)", " ms", false, double.NaN), new("Internet packet loss (%)", "%", false, double.NaN)],
        _ => [],
    };

    /// <summary>Whether the symptom is still present in <paramref name="m"/>, by the same thresholds the plan uses.</summary>
    public static bool StillAProblem(FixMeasurement m)
    {
        double? V(string k) => m.Metrics.TryGetValue(k, out var v) ? v : null;
        bool fps = V("Average FPS") is { } f ? f < m.TargetFps * 0.9 && m.Diagnosis?.CapFps is null : true;
        bool stutter = V("Stutter frames (%)") is { } st ? st >= 0.5 || V("99th pct frame time (ms)") > 2 * (V("Average frame time (ms)") ?? double.MaxValue) : true;
        bool heat = V("CPU temp (max °C)") >= 95 || V("GPU temp (max °C)") >= 87 || V("GPU thermal limit (% of time)") >= 20;
        bool ping = V("Internet packet loss (%)") > 1 || V("Internet jitter (ms)") > 10 || V("Router packet loss (%)") > 1 || V("Router jitter (ms)") > 10 || V("Internet ping (ms)") > 100;
        return m.Problem switch
        {
            GameProblem.LowFps => fps,
            GameProblem.Stutter => stutter,
            GameProblem.InputDelay => V("Average frame time (ms)") is { } ft ? ft > 12 || (m.Diagnosis?.CapFps is { } c && Math.Abs(c - m.Diagnosis.RefreshHz) < 1.5) : true,
            GameProblem.HighPing => ping,
            GameProblem.Overheating => heat,
            GameProblem.Everything => (V("Average FPS") is not null && (fps || stutter)) || heat || (m.Network is not null && ping),
            _ => true,
        };
    }

    /// <summary>
    /// Before/after on the problem's own numbers. Changes inside the metric's noise margin (a minimum, plus the spread of
    /// repeated runs) count as no measurable improvement. Different games, missing frame timing or a scene that changed
    /// too much make it inconclusive instead of a verdict.
    /// </summary>
    public static FixComparison Compare(FixMeasurement before, FixMeasurement after)
    {
        var rows = new List<(string, double?, double?, string, bool)>();
        var notes = new List<string>();
        FixVerdict? verdict = null;
        string summary = "";
        foreach (var k in Keys(before.Problem))
        {
            double? b = before.Metrics.TryGetValue(k.Metric, out var bv) ? bv : null, a = after.Metrics.TryGetValue(k.Metric, out var av) ? av : null;
            if (b is null && a is null) continue;
            rows.Add((k.Metric, b, a, k.Unit, k.HigherIsBetter));
            if (verdict is not null || double.IsNaN(k.MinChange) || b is null || a is null) continue;
            // Absolute floor so tiny numbers (0.1% → 0.2%) don't flip the verdict; repeated runs add their own spread.
            double floor = k.Unit is "%" ? 0.3 : k.Unit is " ms" ? 1 : k.Unit is " °C" ? 3 : 2;
            double spread = (before.Spread.GetValueOrDefault(k.Metric) + after.Spread.GetValueOrDefault(k.Metric)) / 2;
            double change = a.Value - b.Value, margin = Math.Max(floor, Math.Abs(b.Value) * k.MinChange) + spread;
            double better = k.HigherIsBetter ? change : -change;
            verdict = better > margin ? FixVerdict.Improved : better < -margin ? FixVerdict.Worse : FixVerdict.Same;
            summary = $"{k.Metric}: {F(b.Value, "0.#")}{k.Unit} → {F(a.Value, "0.#")}{k.Unit}" + (verdict == FixVerdict.Same ? $" (within the ±{F(margin, "0.#")}{k.Unit} a re-test normally varies)" : $" (beyond the ±{F(margin, "0.#")}{k.Unit} noise margin)");
        }
        string? why = null;
        if (verdict is null) why = before.Problem == GameProblem.Crashes
            ? "Crashes can't be re-tested in a minute: play normally and run Fix My Game again later; new crashes since the fix will show up."
            : "The two measurements don't share the numbers needed to compare (for example, frame timing was missing in one of them).";
        else if (before.Problem != GameProblem.HighPing && before.What != after.What) why = $"Different programs were measured ({before.What} before, {after.What} after).";
        else if (before.Metrics.GetValueOrDefault("FPS variation (%)") > 25 || after.Metrics.GetValueOrDefault("FPS variation (%)") > 25)
            why = "The scene changed too much during one of the runs (FPS varied over 25% second to second), so the difference can't be credited to the fix.";
        if (why is not null) { notes.Add(why); verdict = FixVerdict.Inconclusive; if (summary.Length == 0) summary = why; }
        if (before.Runs > 1 || after.Runs > 1) notes.Add($"Averaged over {before.Runs} run(s) before and {after.Runs} after; their spread is added to the noise margin.");
        notes.Add(before.Problem == GameProblem.HighPing
            ? "Internet conditions change through the day; repeat the test if the result surprises you."
            : "A re-test is only fair in the same place in the game with the same settings. Differences under the margin are normal run-to-run noise.");
        bool had = StillAProblem(before), still = StillAProblem(after);
        if (!had && !still && before.Problem != GameProblem.Crashes) notes.Insert(0, "Neither measurement showed the problem, so there was nothing to improve in these tests.");
        return new(verdict!.Value, still, summary, rows, notes) { HadProblem = had };
    }

    /// <summary>A plain-text repair report: what was found, what was changed, and what the re-test showed.</summary>
    public static string Report(GameContext? game, FixPlan plan, FixMeasurement before, FixComparison? cmp, IEnumerable<string> applied, IEnumerable<string> undone)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"MONEY TWEAKS · FIX MY GAME REPORT · {DateTime.Now.ToString("g", CultureInfo.CurrentCulture)}");
        sb.AppendLine($"Problem: {Label(plan.Problem)}");
        if (game is not null) sb.AppendLine($"Game: {game.Name}{(game.Version is null ? "" : " " + game.Version)}{(game.Exe is null ? "" : $" ({game.Exe})")}");
        sb.AppendLine($"Scan: {(before.Live ? $"Deep ({before.Runs} run(s), {before.What})" : "Quick")} at {before.At.ToString("t", CultureInfo.CurrentCulture)}");
        sb.AppendLine().AppendLine("FINDINGS");
        foreach (var c in plan.Causes)
        {
            sb.AppendLine($"- [{c.Severity}] {c.Title} ({c.Status}, {c.Confidence} confidence)");
            foreach (var e in c.Evidence) sb.AppendLine("    evidence: " + e);
        }
        sb.AppendLine().AppendLine("RECOMMENDED");
        foreach (var a in plan.Actions) sb.AppendLine($"- {a.Title} [{a.Kind}]");
        var ap = applied.ToList();
        if (ap.Count > 0) sb.AppendLine().AppendLine("CHANGED BY YOU HERE").AppendLine(string.Join(Environment.NewLine, ap.Select(x => "- " + x)));
        var un = undone.ToList();
        if (un.Count > 0) sb.AppendLine("UNDONE").AppendLine(string.Join(Environment.NewLine, un.Select(x => "- " + x)));
        if (cmp is not null)
        {
            sb.AppendLine().AppendLine($"RE-TEST: {cmp.VerdictLabel}{(cmp.StillAProblem ? ", still a problem" : cmp.HadProblem ? ", looks resolved" : "")}");
            sb.AppendLine(cmp.Summary);
            foreach (var (metric, b, a, unit, _) in cmp.Rows) sb.AppendLine($"  {metric}: {(b is { } x ? F(x, "0.#") + unit : "n/a")} → {(a is { } y ? F(y, "0.#") + unit : "n/a")}");
        }
        if (plan.Notes.Count > 0) sb.AppendLine().AppendLine("NOTES").AppendLine(string.Join(Environment.NewLine, plan.Notes.Select(n => "- " + n)));
        return sb.ToString();
    }
}
