using System.Text.Json;
using MoneyTweaks.Core.Benchmarks;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Lab;

/// <summary>What a setting is expected to do to one measured metric. An estimate from how the setting works, never a measurement.</summary>
public enum Expect { NoChange, MayRise, MayFall, Unknown }

public sealed record MetricExpectation(string MetricKey, Expect Expect, string Why);

/// <summary>How a prediction compares with what was measured.</summary>
public enum PredictionCheck { Matched, NotSeen, Opposite, Unexpected, NoData, NoPrediction }

/// <summary>A safe, curated set of tweaks to try in the lab: every one switchable without a restart and fully reversible.</summary>
public sealed record LabExperiment(string Id, string Name, string Goal, IReadOnlyList<string> TweakIds);

/// <summary>An experiment the user chose to keep: exactly the tweaks it switched on, so rolling back touches nothing else.</summary>
public sealed class LabRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExperimentName { get; set; } = "";
    public DateTime KeptAt { get; set; } = DateTime.Now;
    public string? StudyId { get; set; }
    public List<string> AppliedIds { get; set; } = [];
}

public sealed class LabFile
{
    public int Version { get; set; } = 1;
    public List<LabRecord> Kept { get; set; } = [];
}

public static class RealityLab
{
    /// <summary>Metrics the lab measures and predicts, in display order (keys from <see cref="BenchmarkStats.Metrics"/>).</summary>
    public static readonly string[] MetricKeys = ["fps", "low1", "cpu", "gpu", "ram", "cputemp", "gputemp"];

    /// <summary>Metrics that only exist when the 3D scene runs.</summary>
    public static readonly HashSet<string> SceneOnly = ["fps", "low1"];

    public static IReadOnlyList<LabExperiment> Experiments { get; } =
    [
        new("lab.lighter-desktop", "Lighter desktop", "Fewer desktop effects: does Windows' own drawing cost anything while a 3D app runs?",
            ["gen.visualfx-performance", "gen.transparency", "gen.menu-delay"]),
        new("lab.power-saving-off", "Power saving off", "No parked CPU cores, no power throttling, no USB port sleep: steadier frame times, or no difference?",
            ["gen.core-parking", "adv.power-throttling", "gen.usb-suspend"]),
        new("lab.quiet-background", "Quiet background", "No Game Bar background recording or tips, Game Mode on: does any of it show outside a real game?",
            ["gen.background-recording", "gen.gamebar-tips", "gen.game-mode"]),
        new("lab.foreground-priority", "Foreground priority", "Windows' scheduler favours the active window and games: visible in this test, or only in real games?",
            ["adv.priority-separation", "adv.system-responsiveness", "adv.games-task"]),
    ];

    // Hand-written from how each setting works. Missing metric = no change expected; a tweak not listed = Unknown.
    private static readonly Dictionary<string, MetricExpectation[]> Known = new()
    {
        ["gen.transparency"] = [new("gpu", Expect.MayFall, "Less blending work for the desktop compositor.")],
        ["gen.min-processor-state"] =
        [
            new("cputemp", Expect.MayRise, "The CPU never clocks down, so it runs warmer when lightly loaded."),
            new("low1", Expect.MayRise, "No clock ramp-up delay; often too small to measure on modern CPUs."),
        ],
        ["gen.core-parking"] = [new("low1", Expect.MayRise, "No waiting for parked cores to wake; often too small to measure.")],
        ["gen.ultimate-plan"] =
        [
            new("cputemp", Expect.MayRise, "High clocks are held, so more heat and power."),
            new("low1", Expect.MayRise, "Less clock ramp-up stutter; often too small to measure."),
        ],
        ["adv.power-throttling"] = [new("cpu", Expect.Unknown, "Only background apps that Windows would throttle are affected; depends on what's running.")],
        ["adv.priority-separation"] =
        [
            new("fps", Expect.Unknown, "Longer time slices for the foreground window; small and workload-dependent."),
            new("low1", Expect.Unknown, "Longer time slices for the foreground window; small and workload-dependent."),
        ],
    };

    // Settings whose mechanism doesn't touch a running 3D window or idle resource use: no change expected in the lab.
    private static readonly Dictionary<string, string> NoEffectHere = new()
    {
        ["gen.visualfx-performance"] = "Animations and shadows of desktop windows aren't drawn inside a 3D window.",
        ["gen.menu-delay"] = "Only changes how fast menus open.",
        ["gen.background-recording"] = "Game DVR only records games; the lab's scene isn't one.",
        ["gen.gamebar-tips"] = "Only the pop-up when a game starts.",
        ["gen.toasts-off"] = "Notifications are occasional, not continuous load.",
        ["gen.game-mode"] = "Windows applies Game Mode only to apps it recognises as games.",
        ["gen.usb-suspend"] = "Only keeps idle USB ports powered; mice and headsets stop dropping out, load and frame rate aren't affected.",
        ["adv.system-responsiveness"] = "Only affects threads registered with the multimedia scheduler (games, audio).",
        ["adv.games-task"] = "Only affects threads registered as games with the multimedia scheduler.",
    };

    /// <summary>The estimate for one tweak and metric.</summary>
    public static MetricExpectation For(Tweak t, string metric)
    {
        if (Known.TryGetValue(t.Id, out var list))
            return list.FirstOrDefault(e => e.MetricKey == metric) ?? new(metric, Expect.NoChange, "Not something this setting works on.");
        if (NoEffectHere.TryGetValue(t.Id, out var why)) return new(metric, Expect.NoChange, why);
        if (SyntheticPlan.NotMeasurable(t) is { } nm && SceneOnly.Contains(metric)) return new(metric, Expect.NoChange, char.ToUpperInvariant(nm[0]) + nm[1..] + ".");
        return new(metric, Expect.Unknown, "No estimate for this setting; the measurement will tell.");
    }

    /// <summary>Combined estimate for a set of tweaks: opposite expectations or any unknown make the result Unknown.</summary>
    public static (Expect expect, List<string> reasons) Combine(IReadOnlyCollection<Tweak> tweaks, string metric)
    {
        var each = tweaks.Select(t => (t, e: For(t, metric))).ToList();
        var reasons = each.Where(x => x.e.Expect != Expect.NoChange).Select(x => $"{x.t.Name}: {x.e.Why}").ToList();
        bool rise = each.Any(x => x.e.Expect == Expect.MayRise), fall = each.Any(x => x.e.Expect == Expect.MayFall);
        var unknown = each.Any(x => x.e.Expect == Expect.Unknown);
        var expect = rise && fall ? Expect.Unknown : rise ? Expect.MayRise : fall ? Expect.MayFall : unknown ? Expect.Unknown : Expect.NoChange;
        if (rise && fall) reasons.Insert(0, "Settings in this set pull in opposite directions.");
        return (expect, reasons);
    }

    public static string Describe(Expect e, Metric m) => e switch
    {
        Expect.NoChange => "No change expected",
        Expect.MayRise => m.HigherIsBetter ? "May rise slightly (better)" : "May rise",
        Expect.MayFall => m.HigherIsBetter ? "May fall" : "May fall slightly (better)",
        _ => "Can't be estimated",
    };

    /// <summary>
    /// Compares an estimate with a measured comparison. "May" predictions are only possibilities, so a change that stays
    /// within run-to-run variation is reported as "not seen", never as a hit.
    /// </summary>
    public static PredictionCheck Check(Expect e, MetricComparison c)
    {
        if (c.Verdict == Verdict.NotEnoughData) return PredictionCheck.NoData;
        if (e == Expect.Unknown) return PredictionCheck.NoPrediction;
        bool changed = c.Verdict is Verdict.Better or Verdict.Worse;
        bool rose = changed && c.Diff > 0;
        return e switch
        {
            Expect.NoChange => changed ? PredictionCheck.Unexpected : PredictionCheck.Matched,
            Expect.MayRise => !changed ? PredictionCheck.NotSeen : rose ? PredictionCheck.Matched : PredictionCheck.Opposite,
            _ => !changed ? PredictionCheck.NotSeen : rose ? PredictionCheck.Opposite : PredictionCheck.Matched,
        };
    }

    public static string Describe(PredictionCheck p) => p switch
    {
        PredictionCheck.Matched => "Estimate matched",
        PredictionCheck.NotSeen => "Possible change not seen",
        PredictionCheck.Opposite => "Opposite of the estimate",
        PredictionCheck.Unexpected => "Changed unexpectedly",
        PredictionCheck.NoPrediction => "No estimate to check",
        _ => "Not enough data",
    };

    /// <summary>Tweaks of a set the lab can switch for a test, and why the others are left out.</summary>
    public static (List<Tweak> usable, List<string> left) Usable(IEnumerable<string> ids, Func<string, Tweak?>? find = null)
    {
        find ??= TweakCatalog.Find;
        var usable = new List<Tweak>();
        var left = new List<string>();
        foreach (var id in ids.Distinct())
        {
            if (find(id) is not { } t) { left.Add($"{id} (not in this version)"); continue; }
            if (t.Risk != Risk.Safe) left.Add($"{t.Name} (Caution tweak: apply it from its own page after reading the warning)");
            else if (!TweakStateGuard.Eligible(t, out var why)) left.Add($"{t.Name} ({why})");
            else usable.Add(t);
        }
        return (usable, left);
    }

    // ---- storage ----------------------------------------------------------------------------------------------

    public static string FilePath => Path.Combine(AppPaths.Root, "lab.json");
    /// <summary>Original tweak states while an experiment runs; restored on the next start if the app closes mid-test.</summary>
    public static string PendingPath => Path.Combine(AppPaths.Root, "lab-restore.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LabFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LabFile>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { Log.Error("lab.json is unreadable; starting a new list (old file kept as .bad)", ex); try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new();
    }

    public static void Save(LabFile f)
    {
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(f, Json));
    }
}
