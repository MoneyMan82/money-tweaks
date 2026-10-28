using System.Globalization;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Scanner;

namespace MoneyTweaks.Core.Advisor;

public enum GameProblem { LowFps, Stutter, InputDelay, HighPing, Overheating }

public enum FixKind
{
    /// <summary>A catalog tweak: previewed, applied only after approval, backed up and undoable.</summary>
    Tweak,
    /// <summary>Something the player does (a game setting, a cable, cleaning a fan). Money Tweaks changes nothing.</summary>
    Step,
    /// <summary>Opens an existing Money Tweaks tool, which has its own confirmations.</summary>
    Tool,
}

/// <summary>A likely cause, in plain words, with the measurements that point at it.</summary>
public sealed record FixCause(string Title, string Plain, IReadOnlyList<string> Evidence, string Confidence, double Score);

/// <summary>One recommended action. <see cref="Expect"/> never promises a number.</summary>
public sealed record FixAction(string Id, FixKind Kind, string Title, string Plain, string Expect, string? TweakId = null, string? PageTag = null, double Score = 0);

/// <summary>The measurement a plan and a re-test are based on (key numbers only; null = not available).</summary>
public sealed class FixMeasurement
{
    public GameProblem Problem { get; init; }
    public DateTime At { get; init; } = DateTime.Now;
    public string What { get; init; } = "";
    public Dictionary<string, double> Metrics { get; init; } = [];
    public List<string> Limits { get; init; } = [];
    public Diagnosis? Diagnosis { get; init; }
    public DiagnosticReport? Network { get; init; }
    public double TargetFps { get; init; }
}

public sealed record FixPlan(GameProblem Problem, IReadOnlyList<FixCause> Causes, IReadOnlyList<FixAction> Actions, IReadOnlyList<string> Notes);

public enum FixVerdict { Improved, Same, Worse, NotComparable }

/// <summary>Re-test result: the verdict on the main number, every number side by side, and whether the problem is still there.</summary>
public sealed record FixComparison(FixVerdict Verdict, bool StillAProblem, string Summary, IReadOnlyList<(string metric, double? before, double? after, string unit, bool higherIsBetter)> Rows, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the first measurement showed the problem at all.</summary>
    public bool HadProblem { get; init; } = true;
}

/// <summary>
/// Fix My Game: from one problem the player picks to the 1–3 most relevant actions, using only what was measured on this
/// PC: the Upgrade Advisor's game capture and bottleneck diagnosis (frame times, CPU/GPU load, clocks, temperatures,
/// throttling, memory, VRAM, disk, background CPU), the network diagnostic (router vs internet ping, jitter, loss) and
/// Smart Scanner's findings (settings, power plan, RAM, startup, Wi-Fi…). A cause is only named when that evidence
/// supports it; otherwise the plan says no clear cause was found. Recommendations are existing undoable tweaks,
/// plain steps, or existing tools. Nothing here changes the system.
/// </summary>
public static class FixMyGame
{
    public static string Label(GameProblem p) => p switch
    {
        GameProblem.LowFps => "Low FPS",
        GameProblem.Stutter => "Stuttering",
        GameProblem.InputDelay => "Input delay",
        GameProblem.HighPing => "High ping",
        _ => "Overheating",
    };

    public static string Blurb(GameProblem p) => p switch
    {
        GameProblem.LowFps => "The game runs at fewer frames per second than you want.",
        GameProblem.Stutter => "Smooth most of the time, with hitches, freezes or uneven frames.",
        GameProblem.InputDelay => "Aim or controls feel sluggish or floaty.",
        GameProblem.HighPing => "Lag, rubber-banding or a high ping number online.",
        _ => "The PC gets hot or loud, or slows down after a while.",
    };

    /// <summary>Ping needs only the network; everything else is measured while the game runs.</summary>
    public static bool NeedsGame(GameProblem p) => p != GameProblem.HighPing;

    private static string F(double v, string fmt = "0") => v.ToString(fmt, CultureInfo.CurrentCulture);

    // ---- measurement → numbers ------------------------------------------------------------------------------------------
    public static FixMeasurement FromCapture(GameProblem p, Capture c, Diagnosis d, double targetFps)
    {
        var m = new Dictionary<string, double>(d.Summary);
        int n = Math.Max(1, c.Samples.Count);
        m["GPU thermal limit (% of time)"] = c.Samples.Count(s => s.GpuThermalLimited) * 100.0 / n;
        m["GPU power limit (% of time)"] = c.Samples.Count(s => s.GpuPowerCapped) * 100.0 / n;
        if (d.Frames is { } f) m["Average frame time (ms)"] = 1000.0 / f.AvgFps;
        var limits = new List<string>();
        if (c.FramesUnavailable is { } why) limits.Add("Frame timing wasn't available: " + why);
        if (!m.ContainsKey("CPU temp (max °C)")) limits.Add("CPU temperature isn't reported on this PC right now (Hardware Monitor can enable it).");
        if (!m.ContainsKey("GPU temp (max °C)")) limits.Add("GPU temperature isn't reported by the driver.");
        return new FixMeasurement { Problem = p, What = c.What, Metrics = m, Limits = limits, Diagnosis = d, TargetFps = targetFps };
    }

    public static FixMeasurement FromNetwork(DiagnosticReport r)
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
        return new FixMeasurement { Problem = GameProblem.HighPing, What = r.Connection, Metrics = m, Network = r,
            Limits = ["Generic ping to public servers and your router, not the game server. The game's own ping is shown in its settings or stats overlay."] };
    }

    // ---- plan -----------------------------------------------------------------------------------------------------------
    /// <summary>Causes and the 1–3 most relevant actions for <paramref name="m"/>. Pure: tests build the inputs by hand.</summary>
    public static FixPlan Plan(FixMeasurement m, IReadOnlyList<Finding> findings)
    {
        var causes = new List<FixCause>();
        var actions = new List<FixAction>();
        var notes = new List<string>(m.Limits);
        var d = m.Diagnosis;
        var x = m.Metrics;
        double? V(string k) => x.TryGetValue(k, out var v) ? v : null;
        Finding? Find(string id) => findings.FirstOrDefault(f => f.Id == id);
        LimiterFactor? Factor(Limiter k) => d?.Factors.FirstOrDefault(f => f.Kind == k);
        void Cause(string title, string plain, IEnumerable<string> ev, string conf, double score) => causes.Add(new(title, plain, ev.Where(e => e.Length > 0).ToList(), conf, score));
        void Act(FixAction a) { if (!actions.Any(o => o.Id == a.Id)) actions.Add(a); }
        void TweakFrom(Finding f, double score, string expect)
        {
            foreach (var id in f.FixTweakIds)
                if (TweakCatalog.Find(id) is { } t)
                    Act(new("tweak:" + id, FixKind.Tweak, t.Name, f.Why, expect, TweakId: id, Score: score));
        }
        void FromFinding(string id, string causeTitle, double score, string expect, string? stepTitle = null)
        {
            if (Find(id) is not { } f) return;
            Cause(causeTitle, f.Why, f.Evidence, f.Confidence.ToString(), score);
            if (f.FixTweakIds.Count > 0) TweakFrom(f, score, expect);
            else if (f.Manual is { } manual) Act(new("step:" + id, FixKind.Step, stepTitle ?? f.Title, manual, expect, Score: score));
        }
        void FromFactor(Limiter k, string title, string plain, double weight = 1)
        {
            if (Factor(k) is { } fc) Cause(title, plain, fc.Evidence, fc.Confidence, fc.Score * weight);
        }

        switch (m.Problem)
        {
            case GameProblem.LowFps:
            {
                if (d?.FpsMeasured != true) notes.Add("FPS itself couldn't be measured, so this is based on CPU and GPU load only.");
                FromFactor(Limiter.FrameCap, $"FPS is held at a limit ({F(d?.CapFps ?? 0)} FPS)", "The frame rate is flat at a fixed number while the hardware isn't maxed out: a frame limit or V-Sync is holding it there on purpose.");
                if (Factor(Limiter.FrameCap) is { } cap)
                    Act(new("step:uncap", FixKind.Step, "Raise or remove the frame limit", "In the game's video settings, turn off V-Sync or set the frame-rate limit higher (or to your monitor's refresh rate). Also check the GPU driver's own frame limiter.", "Only higher FPS if the hardware has room left, which the measurement suggests it does.", Score: cap.Score));
                FromFactor(Limiter.Gpu, "Your graphics card is working at full power", "The GPU is busy almost all the time, so it decides the frame rate. Windows tweaks can't make it faster; lighter graphics settings can.");
                if (Factor(Limiter.Gpu) is { } gpu)
                    Act(new("step:gpu-settings", FixKind.Step, "Lower the heaviest graphics settings", "In the game: lower resolution scale, shadows, effects and view distance first, or turn on DLSS / FSR / XeSS if the game has it. These are the settings that cost the most GPU time.", "More FPS in GPU-heavy scenes. How much depends on the game and settings.", Score: gpu.Score));
                FromFactor(Limiter.GpuPower, "The graphics card hits its power limit", "The driver reports the power limit as what's capping its clocks.", 0.8);
                FromFactor(Limiter.GpuThermal, "The graphics card is slowing down from heat", "The driver reports thermal slowdown, so it lowers its clocks.", 0.9);
                if (Factor(Limiter.GpuThermal) is { } gth) Act(new("step:cooling", FixKind.Step, "Improve cooling", "Clean dust from the GPU and case fans, make sure the case has airflow, and keep a laptop on a hard surface.", "Steadier clocks under load once temperatures drop.", Score: gth.Score * 0.9));
                FromFactor(Limiter.Cpu, "The processor can't prepare frames fast enough", "The GPU is waiting: the CPU (often one main game thread) is the limit.");
                FromFactor(Limiter.CpuClocks, "The processor runs below its normal speed", "Under load the CPU stayed below its base clock: power saving, a power limit or heat.");
                FromFactor(Limiter.CpuThermal, "The processor is at its temperature limit", "CPUs slow themselves down at about 95–100 °C.");
                FromFactor(Limiter.Background, "Other apps are using the processor", "Apps other than the game used a noticeable share of the CPU while you played.");
                if (Factor(Limiter.Background) is { } bg) Act(new("tool:booster", FixKind.Tool, "Close background apps with Game Booster", "Game Booster lists running apps and closes only the ones you tick (it asks first).", "More CPU time for the game while those apps are closed.", PageTag: "booster", Score: bg.Score));
                FromFactor(Limiter.Memory, "Your PC is running out of memory", "When RAM is full Windows swaps to disk, which slows games down.");
                FromFactor(Limiter.Vram, "Graphics memory is full", "Textures no longer fit in the graphics card's memory.");
                if (Factor(Limiter.Vram) is { } vr) Act(new("step:textures", FixKind.Step, "Lower texture quality one step", "In the game's graphics settings, lower Textures (or texture streaming budget) one notch.", "Fewer drops when VRAM was full.", Score: vr.Score));
                bool cpuSide = Factor(Limiter.Cpu) is not null || Factor(Limiter.CpuClocks) is not null || Factor(Limiter.Background) is not null;
                FromFinding("power-saver", "Windows is in Power saver mode", 0.85, "Higher CPU clocks while plugged in.", "Switch to Balanced or High performance");
                FromFinding("on-battery", "The laptop is on battery", 0.8, "Full CPU/GPU power once plugged in.", "Plug in the charger");
                if (cpuSide)
                {
                    FromFinding("power-balanced", "The desktop is on the Balanced power plan", 0.45, "Faster clock ramp-up on some PCs; not guaranteed to change FPS.");
                    FromFinding("ram-xmp", "Memory runs below its rated speed", 0.6, "Often better CPU-limited FPS and 1% lows.", "Turn on XMP/EXPO in the BIOS");
                }
                FromFinding("ram-pressure", "Memory is nearly full right now", 0.6, "Less swapping to disk while those apps are closed.", "Close the largest apps before playing");
                FromFinding("game-dvr", "Background recording is on", 0.35, "Frees the GPU's video encoder and some memory.");
                FromFinding("reverted", "Tweaks you applied earlier were changed back", 0.4, "Your earlier choices are active again.");
                break;
            }
            case GameProblem.Stutter:
            {
                var fr = d?.Frames;
                if (fr is null) notes.Add("Stutter is measured from frame times, which need administrator rights (Restart as admin) and the game in front.");
                else if (fr.StutterPct < 0.5 && fr.P99Ms < fr.MedianMs * 2)
                    Cause("Frame pacing looked smooth in this measurement", $"{F(fr.StutterPct, "0.#")}% long frames, 99% of frames within {F(fr.P99Ms, "0.#")} ms (typical {F(fr.MedianMs, "0.#")} ms). Stutter may happen in other scenes, during loading or the first minutes (shader compilation).", [], "Medium", 0.2);
                else if (fr is { } s)
                    Cause("Frames arrive unevenly", "Some frames take much longer than the rest, which you feel as hitches.", [$"{F(s.StutterPct, "0.#")}% of frames were long spikes; 1% low {F(s.Low1Fps)} FPS vs {F(s.AvgFps)} FPS average; 99% of frames within {F(s.P99Ms, "0.#")} ms."], "High", 0.3);
                FromFactor(Limiter.Storage, "The disk is slow to respond during hitches", "Long frames happened while the disk was busy: the game is waiting for data.");
                if (Factor(Limiter.Storage) is { } sto) Act(new("step:ssd", FixKind.Step, "Move the game to an SSD (or free up the disk)", "If the game is on a hard drive, move it to an SSD with the launcher's move/verify option. Pause downloads and updates while playing.", "Fewer loading hitches.", Score: sto.Score));
                FromFactor(Limiter.Memory, "Your PC is running out of memory", "Swapping memory to disk causes freezes.");
                FromFactor(Limiter.Vram, "Graphics memory is full", "Textures streaming from system RAM cause hitches.");
                if (Factor(Limiter.Vram) is { } vr) Act(new("step:textures", FixKind.Step, "Lower texture quality one step", "In the game's graphics settings, lower Textures one notch.", "Fewer hitches when VRAM was full.", Score: vr.Score));
                FromFactor(Limiter.Background, "Other apps are busy in the background", "Bursts of background work can steal time from the game.");
                if (Factor(Limiter.Background) is { } bg) Act(new("tool:booster", FixKind.Tool, "Close background apps with Game Booster", "Game Booster lists running apps and closes only the ones you tick.", "Fewer interruptions while those apps are closed.", PageTag: "booster", Score: bg.Score));
                FromFactor(Limiter.CpuThermal, "The processor is at its temperature limit", "Heat throttling makes frame times uneven.");
                FromFactor(Limiter.GpuThermal, "The graphics card is slowing down from heat", "Clock drops from heat show up as uneven frames.");
                FromFinding("ram-pressure", "Memory is nearly full right now", 0.7, "Less swapping while those apps are closed.", "Close the largest apps before playing");
                FromFinding("disk-space", "The system drive is almost full", 0.6, "Room for the page file and shader cache.", "Free up space on the system drive");
                FromFinding("busy-processes", "Background apps were busy during the check", 0.4, "Fewer interruptions if they stay busy.", "Pause or close the busy apps");
                FromFinding("game-dvr", "Background recording is on", 0.35, "Removes the constant background capture work.");
                if (d?.Frames is { StutterPct: >= 0.5 } && Factor(Limiter.Cpu) is { } cpu && causes.All(c => c.Score < 0.5))
                    Cause("The processor is the limit", "When the CPU is maxed, frame times vary more.", cpu.Evidence, cpu.Confidence, cpu.Score * 0.6);
                if (d?.Frames is { StutterPct: >= 0.5 })
                    FromFinding("power-balanced", "The desktop is on the Balanced power plan", 0.3, "Can reduce clock ramp-up hitches on some systems; not guaranteed.");
                break;
            }
            case GameProblem.InputDelay:
            {
                notes.Add("Money Tweaks can't measure the delay between your click and the screen directly (that needs special hardware). It checks the parts it can see: frame time, frame limits and the settings that affect input.");
                if (d?.Frames is { } fr)
                {
                    double ft = 1000.0 / fr.AvgFps;
                    if (Factor(Limiter.FrameCap) is { } cap && d.CapFps is { } capFps && Math.Abs(capFps - d.RefreshHz) < 1.5)
                    {
                        Cause("V-Sync (or a limit at your refresh rate) is on", "The frame rate sits exactly at your monitor's refresh rate. V-Sync waits for the screen before showing a frame, which adds delay.", cap.Evidence, cap.Confidence, 0.8);
                        Act(new("step:vsync", FixKind.Step, "Turn off V-Sync in the game", "Turn V-Sync off in the game's video settings. If you have G-Sync/FreeSync, keep it on and set a frame limit a few FPS below the refresh rate instead.", "Less delay; you may see tearing without G-Sync/FreeSync.", Score: 0.8));
                    }
                    if (ft > 12)
                        Cause("Each frame takes a long time", $"At {F(fr.AvgFps)} FPS each frame takes about {F(ft, "0.0")} ms, and your input waits for the next frame.", [$"Average {F(fr.AvgFps)} FPS ({F(ft, "0.0")} ms per frame), 1% low {F(fr.Low1Fps)} FPS."], "Medium", Math.Min(0.75, ft / 30));
                    if (Factor(Limiter.Gpu) is { Score: >= 0.75 } gpu)
                    {
                        Cause("The graphics card is maxed out", "When the GPU is at 99%, frames queue up before it, which adds delay on top of the frame time.", gpu.Evidence, gpu.Confidence, 0.6);
                        Act(new("step:reflex", FixKind.Step, "Turn on NVIDIA Reflex / AMD Anti-Lag, or cap FPS slightly lower", "If the game has NVIDIA Reflex or AMD Anti-Lag, turn it on. Otherwise set a frame limit a little below what you get now, so the GPU isn't at 100%.", "Lower delay at the same or slightly lower FPS.", Score: 0.6));
                    }
                }
                else notes.Add("Frame timing wasn't available, so frame time and V-Sync couldn't be checked (Restart as admin).");
                FromFinding("mouse-accel", "Mouse acceleration is on", 0.5, "Same hand movement = same distance. It doesn't change delay, but aim feels more direct.");
                FromFinding("usb-suspend", "USB ports may power down", 0.35, "Avoids rare mouse or keyboard dropouts.");
                FromFinding("game-mode", "Game Mode is off", 0.3, "Fewer background interruptions while playing.");
                FromFinding("power-saver", "Windows is in Power saver mode", 0.6, "Higher clocks, shorter frame times.", "Switch to Balanced or High performance");
                break;
            }
            case GameProblem.HighPing:
            {
                var r = m.Network;
                double? rj = V("Router jitter (ms)"), rl = V("Router packet loss (%)"), ij = V("Internet jitter (ms)"), il = V("Internet packet loss (%)"), ip = V("Internet ping (ms)"), rp = V("Router ping (ms)");
                bool wifi = r?.Connection.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) == true;
                if (rl > 1 || rj > 10 || rp > 15)
                {
                    Cause("The problem is between your PC and your router", "Even the router, right next to you, answers slowly or loses packets.", [$"Router: {F(rp ?? 0)} ms, jitter {F(rj ?? 0, "0.#")} ms, loss {F(rl ?? 0, "0.#")}%.", wifi ? "Connected over Wi-Fi." : ""], "High", 0.9);
                    Act(new("step:cable", FixKind.Step, wifi ? "Use an Ethernet cable (or move closer to the router)" : "Check the cable and router", wifi ? "Wi-Fi interference causes this. A cable fixes it in most homes; otherwise get closer to the router or use the 5 GHz band." : "Try another cable or router port, and restart the router.", "Steadier ping and less packet loss inside your home.", Score: 0.9));
                }
                else if (rp is null && il is not null)
                    notes.Add("Your router doesn't answer ping, so home and internet problems can't be told apart.");
                if ((il > 1 || ij > 10) && !(rl > 1 || rj > 10))
                {
                    Cause("Your home network is fine; the delay is beyond your router", "The router answers quickly, but servers on the internet show jitter or loss: your provider or the route to the servers.", [$"Internet: {F(ip ?? 0)} ms, jitter {F(ij ?? 0, "0.#")} ms, loss {F(il ?? 0, "0.#")}%; router stable."], "Medium", 0.75);
                    Act(new("step:isp", FixKind.Step, "Check other devices and contact your provider", "Pause downloads and streams on other devices. If it continues at the same times of day, report the packet loss to your internet provider.", "Nothing on this PC can fix provider issues; this tells you where to look.", Score: 0.6));
                }
                if (ip > 80 && !(il > 1 || ij > 10))
                {
                    Cause("The servers are far away", "The connection is stable but every packet takes a while: usually distance to the server.", [$"Lowest internet ping {F(ip ?? 0)} ms with low jitter."], "Medium", 0.55);
                    Act(new("step:region", FixKind.Step, "Pick the closest server region in the game", "In the game's settings, choose the matchmaking region nearest to you.", "Lower ping if a closer region exists.", Score: 0.55));
                }
                if (causes.Count == 0 && il is not null)
                {
                    // Measured fine: say so first, and don't offer setting changes the measurement doesn't support.
                    Cause("The connection measured stable", "Low jitter and no meaningful packet loss to your router or the internet in this test. If the game still lags, it's likely the game server or happens at other times: test again when it happens.", [$"Internet {F(ip ?? 0)} ms, jitter {F(ij ?? 0, "0.#")} ms, loss {F(il ?? 0, "0.#")}%."], "Medium", 0.6);
                    if (Find("wifi-only") is { } w) Cause("You're on Wi-Fi", w.Why + " It measured fine this time, but Wi-Fi can vary.", w.Evidence, w.Confidence.ToString(), 0.2);
                    break;
                }
                FromFinding("wifi-only", "You're on Wi-Fi", 0.5, "Steadier ping on a cable.", "Use an Ethernet cable");
                FromFinding("adapter-power", "Network adapter power saving is on", 0.35, "Fewer drop-outs on adapters that misbehave with power saving.");
                foreach (var f in findings.Where(f => f.Id.StartsWith("eth-speed-", StringComparison.Ordinal)))
                    Cause(f.Title, f.Why, f.Evidence, f.Confidence.ToString(), 0.25);
                notes.Add("DNS changes don't lower in-game ping, so they aren't recommended here.");
                break;
            }
            case GameProblem.Overheating:
            {
                double? ct = V("CPU temp (max °C)"), gt = V("GPU temp (max °C)"), gth = V("GPU thermal limit (% of time)");
                if (ct is null && gt is null) notes.Add("No temperature readings are available. Open Hardware Monitor to enable CPU temperature (it installs a sensor driver only after you confirm).");
                if (ct >= 90)
                    Cause("The processor runs very hot", ct >= 95 ? "It's at the point where it slows itself down to protect itself." : "It's close to the temperature where it starts slowing down.", [$"Peak CPU temperature {F(ct!.Value)} °C."], ct >= 95 ? "High" : "Medium", ct >= 95 ? 0.9 : 0.6);
                if (gt >= 83 || gth >= 20)
                    Cause("The graphics card runs very hot", gth >= 20 ? "The driver reports it's slowing down because of heat." : "It's running near its usual maximum temperature.", [gt is { } g ? $"Peak GPU temperature {F(g)} °C." : "", gth is { } t && t > 0 ? $"Thermal slowdown in {F(t)}% of the seconds measured." : ""], gth >= 20 ? "High" : "Medium", gth >= 20 ? 0.9 : 0.55);
                FromFactor(Limiter.CpuClocks, "The processor drops below its normal speed", "Often a sign of heat or a power limit.", 0.7);
                bool hot = causes.Any(c => c.Score >= 0.5);
                if (hot)
                {
                    Act(new("step:cooling", FixKind.Step, "Clean dust and improve airflow", "Blow dust out of the fans and filters, keep the side vents clear, and lift a laptop off soft surfaces. Make sure every case fan spins.", "Lower temperatures and steadier clocks. Usually the biggest win.", Score: 0.9));
                    if (d?.Frames is { } fr && d.CapFps is null)
                        Act(new("step:fpscap", FixKind.Step, "Set a frame limit", $"You get about {F(fr.AvgFps)} FPS. A limit near your monitor's refresh rate ({F(m.TargetFps)} Hz) or a bit below what you get stops the PC from working harder than you can see.", "Less heat and fan noise; FPS above the limit is lost.", Score: 0.7));
                    Act(new("tool:tuning", FixKind.Tool, "Lower the GPU power limit or undervolt", "CPU & GPU Tuning can lower the graphics card's power limit and has an undervolt assistant. Every change there is confirmed and can be reverted.", "Less heat for a small, measurable performance cost.", PageTag: "tuning", Score: 0.5));
                }
                else if (ct is not null || gt is not null)
                    Cause("Temperatures looked normal in this measurement", "Neither the processor nor the graphics card was near its limit while you played.", [ct is { } c2 ? $"CPU peak {F(c2)} °C." : "", gt is { } g2 ? $"GPU peak {F(g2)} °C." : ""], "Medium", 0.1);
                FromFinding("on-battery", "The laptop is on battery", 0.3, "Laptops behave differently on battery.", "Plug in the charger");
                notes.Add("Temperatures depend on the scene and the room: re-test in the same place for a fair comparison.");
                break;
            }
        }

        if (causes.Count == 0)
            Cause("No clear cause found", "Nothing measured points clearly at one cause, so Money Tweaks won't guess. Try measuring in a heavier or more typical scene, for longer.", [], "Low", 0);
        // Actions follow the strongest causes; at most three, strongest first.
        return new(m.Problem, causes.OrderByDescending(c => c.Score).ToList(), actions.OrderByDescending(a => a.Score).Where(a => a.Score >= 0.25).Take(3).ToList(), notes);
    }

    // ---- re-test --------------------------------------------------------------------------------------------------------
    private sealed record Key(string Metric, string Unit, bool HigherIsBetter, double MinChange);

    private static Key[] Keys(GameProblem p) => p switch
    {
        GameProblem.LowFps => [new("Average FPS", " FPS", true, 0.05), new("1% low FPS", " FPS", true, 0.08), new("GPU busy (median %)", "%", true, double.NaN), new("Busiest CPU core (median %)", "%", true, double.NaN)],
        GameProblem.Stutter => [new("Stutter frames (%)", "%", false, 0.25), new("1% low FPS", " FPS", true, 0.08), new("99th pct frame time (ms)", " ms", false, 0.1), new("Average FPS", " FPS", true, double.NaN)],
        GameProblem.InputDelay => [new("Average frame time (ms)", " ms", false, 0.05), new("99th pct frame time (ms)", " ms", false, 0.1), new("Average FPS", " FPS", true, double.NaN)],
        GameProblem.HighPing => [new("Internet jitter (ms)", " ms", false, 0.25), new("Internet packet loss (%)", "%", false, double.NaN), new("Internet ping (ms)", " ms", false, 0.1), new("Router jitter (ms)", " ms", false, double.NaN), new("Router packet loss (%)", "%", false, double.NaN)],
        _ => [new("CPU temp (max °C)", " °C", false, 0.04), new("GPU temp (max °C)", " °C", false, 0.04), new("GPU thermal limit (% of time)", "%", false, double.NaN), new("Average FPS", " FPS", true, double.NaN)],
    };

    /// <summary>Whether the symptom is still present in <paramref name="m"/>, by the same thresholds the plan uses.</summary>
    public static bool StillAProblem(FixMeasurement m)
    {
        double? V(string k) => m.Metrics.TryGetValue(k, out var v) ? v : null;
        return m.Problem switch
        {
            GameProblem.LowFps => V("Average FPS") is { } fps ? fps < m.TargetFps * 0.9 && m.Diagnosis?.CapFps is null : true,
            GameProblem.Stutter => V("Stutter frames (%)") is { } st ? st >= 0.5 || V("99th pct frame time (ms)") > 2 * (V("Average frame time (ms)") ?? double.MaxValue) : true,
            GameProblem.InputDelay => V("Average frame time (ms)") is { } ft ? ft > 12 || (m.Diagnosis?.CapFps is { } c && Math.Abs(c - m.Diagnosis.RefreshHz) < 1.5) : true,
            GameProblem.HighPing => V("Internet packet loss (%)") > 1 || V("Internet jitter (ms)") > 10 || V("Router packet loss (%)") > 1 || V("Router jitter (ms)") > 10 || V("Internet ping (ms)") > 100,
            _ => V("CPU temp (max °C)") >= 95 || V("GPU temp (max °C)") >= 87 || V("GPU thermal limit (% of time)") >= 20,
        };
    }

    /// <summary>
    /// Before/after on the problem's own numbers. Changes smaller than the metric's noise margin count as "the same"; the
    /// verdict comes from the first metric both runs have.
    /// </summary>
    public static FixComparison Compare(FixMeasurement before, FixMeasurement after)
    {
        var rows = new List<(string, double?, double?, string, bool)>();
        var notes = new List<string>();
        FixVerdict verdict = FixVerdict.NotComparable;
        string summary = "";
        foreach (var k in Keys(before.Problem))
        {
            double? b = before.Metrics.TryGetValue(k.Metric, out var bv) ? bv : null, a = after.Metrics.TryGetValue(k.Metric, out var av) ? av : null;
            if (b is null && a is null) continue;
            rows.Add((k.Metric, b, a, k.Unit, k.HigherIsBetter));
            if (verdict != FixVerdict.NotComparable || double.IsNaN(k.MinChange) || b is null || a is null) continue;
            // Absolute floor so tiny numbers (0.1% → 0.2%) don't flip the verdict.
            double floor = k.Unit is "%" ? 0.3 : k.Unit is " ms" ? 1 : k.Unit is " °C" ? 3 : 2;
            double change = a.Value - b.Value, margin = Math.Max(floor, Math.Abs(b.Value) * k.MinChange);
            double better = k.HigherIsBetter ? change : -change;
            verdict = better > margin ? FixVerdict.Improved : better < -margin ? FixVerdict.Worse : FixVerdict.Same;
            summary = $"{k.Metric}: {F(b.Value, "0.#")}{k.Unit} → {F(a.Value, "0.#")}{k.Unit}" + (verdict == FixVerdict.Same ? $" (within the ±{F(margin, "0.#")}{k.Unit} a re-test normally varies)" : "");
        }
        if (verdict == FixVerdict.NotComparable)
            summary = "The two measurements don't share the numbers needed to compare (for example, frame timing was missing in one of them).";
        if (before.What != after.What && before.Problem != GameProblem.HighPing) notes.Add($"Measured {before.What} before and {after.What} after.");
        notes.Add(before.Problem == GameProblem.HighPing
            ? "Internet conditions change through the day; repeat the test if the result surprises you."
            : "A re-test is only fair in the same place in the game with the same settings. Differences under the margin are normal run-to-run noise.");
        if (!StillAProblem(before) && !StillAProblem(after)) notes.Insert(0, "Neither measurement showed the problem, so there was nothing to improve in these tests.");
        return new(verdict, StillAProblem(after), summary, rows, notes) { HadProblem = StillAProblem(before) };
    }
}
