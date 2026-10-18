using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Genome;

/// <summary>Changes and reads the settings behind the factors. The real one uses only documented interfaces.</summary>
public interface IGenomeBackend
{
    /// <summary>Current level of each (non-manual) factor. A value matching no level is added as a "Current" level.</summary>
    Dictionary<string, string> ReadCurrent(IReadOnlyList<GenomeFactor> factors);
    /// <summary>Applies one level and reads it back where the interface allows.</summary>
    (bool ok, string message) Apply(GenomeFactor f, FactorLevel level);
    Fingerprint Fingerprint(GenomeWorkload w);
    (double idleCpu, int processes, bool? onAc) Conditions();
    Task SettleAsync(Action<string> status, CancellationToken ct);
    /// <summary>Crash safety: the settings to put back if Money Tweaks closes mid-session.</summary>
    void SavePending(Dictionary<string, string> original);
    void ClearPending();
}

public sealed record WorkloadResult(Measurement? Measured, bool Stable, string? Problem, bool Fatal = false);

/// <summary>Runs one measured trial (or an unmeasured warm-up).</summary>
public interface IGenomeWorkload
{
    Task<WorkloadResult> RunAsync(GenomeWorkload w, bool warmup, Action<string> status, CancellationToken ct);
}

public sealed record GenomeProgress(int Run, int Total, string Text, Trial? Last);

/// <summary>
/// One experiment session: snapshot your settings → warm-up → trials chosen by <see cref="GenomePlanner"/> with baseline
/// runs at the start, after every 3 trials and at the end (they anchor the normalization and catch drift) → your settings
/// restored and read back. Every trial is saved as it finishes. Failures (refused changes, instability, crashes of the
/// load, safety temperatures) are recorded so that level is never tried again for this workload.
/// </summary>
public static class GenomeRunner
{
    public const int BaselineEvery = 3;

    /// <summary>Number of measured runs for <paramref name="trials"/> experiment trials (baselines included).</summary>
    public static int TotalRuns(int trials) => trials + 2 + (trials > 0 ? (trials - 1) / BaselineEvery : 0) + 1;

    public static async Task<GenomeSession> RunAsync(GenomeStore s, GenomeWorkload w, int trials, IGenomeBackend b, IGenomeWorkload load,
        Func<string, string, Task<bool>> ask, IProgress<GenomeProgress>? progress, CancellationToken ct, Random? rng = null, Action? save = null)
    {
        rng ??= new Random();
        save ??= () => s.Save();
        var auto = s.Factors.Where(f => !f.IsManual).ToList();
        var manual = s.Factors.Where(f => f.IsManual && f.Enabled && w.IsGame).ToList();
        var original = b.ReadCurrent(auto);
        foreach (var m in manual) original[m.Id] = m.Levels[0].Id; // manual factors: first level = what you use now
        var session = new GenomeSession { WorkloadId = w.Id, Original = new(original) };
        s.Sessions.Add(session);
        s.ActiveWorkloadId = w.Id;
        b.SavePending(original);
        save();
        var applied = new Dictionary<string, string>(original);
        int total = TotalRuns(trials), run = 0, sinceBaseline = 0;
        string outcome = "completed";

        void Report(string text, Trial? t = null) => progress?.Report(new(run, total, text, t));

        // Puts the given factors back to their original levels (manual ones: asks the user).
        async Task<List<string>> RestoreAsync(IEnumerable<string> factorIds)
        {
            var problems = new List<string>();
            foreach (var id in factorIds.ToList())
            {
                var f = s.Factor(id);
                if (f is null || applied.GetValueOrDefault(id) == original[id]) continue;
                if (f.IsManual)
                {
                    await ask("Put the game setting back", $"Set {f.Name} back to {f.LabelOf(original[id])} in the game.");
                    applied[id] = original[id];
                    continue;
                }
                var (ok, msg) = await Task.Run(() => b.Apply(f, f.Level(original[id])!));
                if (ok) applied[id] = original[id]; else problems.Add($"{f.Name}: {msg}");
            }
            return problems;
        }

        try
        {
            Report("Warm-up run (not measured)…");
            var warm = await load.RunAsync(w, true, t => Report("Warm-up: " + t), ct);
            if (!warm.Stable) { outcome = "stopped: the warm-up failed (" + warm.Problem + ")"; return session; }

            while (run < total)
            {
                ct.ThrowIfCancellationRequested();
                bool baseline = run < 2 || run == total - 1 || sinceBaseline >= BaselineEvery;
                PlannedTrial plan;
                if (baseline) plan = new(new(original), "baseline (your settings)", true);
                else
                {
                    var a = GenomeAnalysis.Build(s, w, b.Fingerprint(w), original);
                    plan = GenomePlanner.Next(a, s.Goal, rng);
                    plan = plan with { Config = GenomeAnalysis.Complete(plan.Config, s.Factors) };
                    foreach (var f in s.Factors.Where(f => !f.Enabled || (f.IsManual && !w.IsGame))) plan.Config[f.Id] = original[f.Id];
                    if (plan.IsBaseline) plan = plan with { Reason = "baseline (nothing new to test)" };
                }
                run++;
                sinceBaseline = plan.IsBaseline ? 0 : sinceBaseline + 1;
                var label = GenomeAnalysis.Describe(s, plan.Config, original);
                Report($"Run {run}/{total}: {plan.Reason}. Applying: {label}");

                // Apply only what differs from what is set now.
                string? applyProblem = null; var failedLevels = new Dictionary<string, string>();
                foreach (var f in s.Factors.Where(f => plan.Config.GetValueOrDefault(f.Id) != applied.GetValueOrDefault(f.Id)))
                {
                    var level = f.Level(plan.Config[f.Id])!;
                    if (f.IsManual)
                    {
                        if (!await ask("Change a game setting", $"Set {f.Name} to {level.Label} in the game, keep everything else the same, then continue.")) throw new OperationCanceledException();
                        applied[f.Id] = level.Id;
                        continue;
                    }
                    var (ok, msg) = await Task.Run(() => b.Apply(f, level), ct);
                    if (ok) { applied[f.Id] = level.Id; continue; }
                    applyProblem = $"{f.Name} {level.Label} couldn't be applied: {msg}";
                    failedLevels[f.Id] = level.Id;
                    break;
                }
                var conditions = b.Conditions();
                var trial = new Trial
                {
                    SessionId = session.Id, WorkloadId = w.Id, Config = new(plan.Config), IsBaseline = plan.IsBaseline, Reason = plan.Reason,
                    Fingerprint = b.Fingerprint(w), IdleCpuBefore = conditions.idleCpu, ProcessCount = conditions.processes, OnAc = conditions.onAc,
                };
                if (applyProblem is not null)
                {
                    trial.Status = TrialStatus.Failed; trial.Problem = applyProblem;
                    s.Failures.Add(new FailedSetting { WorkloadId = w.Id, Levels = failedLevels, Reason = applyProblem });
                    s.Note("Learned: " + applyProblem);
                    s.Trials.Add(trial); save();
                    await RestoreAsync(failedLevels.Keys.Concat(plan.Config.Keys));
                    Report($"Run {run}/{total}: not measured. {applyProblem}", trial);
                    if (plan.IsBaseline) { outcome = "stopped: your own settings couldn't be re-applied"; break; }
                    continue;
                }

                await b.SettleAsync(t => Report($"Run {run}/{total} ({label}): {t}"), ct);
                conditions = b.Conditions();
                trial.IdleCpuBefore = conditions.idleCpu; trial.ProcessCount = conditions.processes;
                trial.At = DateTime.Now;
                var r = await load.RunAsync(w, false, t => Report($"Run {run}/{total} ({label}): {t}"), ct);
                trial.Measured = r.Measured;
                trial.Status = r.Stable && r.Measured is not null ? TrialStatus.Ok : TrialStatus.Failed;
                trial.Problem = r.Problem;
                s.Trials.Add(trial);
                if (trial.Status == TrialStatus.Failed)
                {
                    if (plan.IsBaseline)
                    {
                        save();
                        outcome = $"stopped: the run failed with your own settings ({r.Problem}). Fix that before tuning";
                        Report(outcome, trial);
                        break;
                    }
                    var changed = plan.Config.Where(kv => original.GetValueOrDefault(kv.Key) != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
                    s.Failures.Add(new FailedSetting { WorkloadId = w.Id, Levels = changed, Reason = r.Problem ?? "failed" });
                    s.Note($"Learned: {GenomeAnalysis.Describe(s, plan.Config, original)} failed ({r.Problem}); it won't be tried again on this workload.");
                    save();
                    // Back to your settings straight away, then let the driver/system recover.
                    await RestoreAsync(changed.Keys);
                    Report($"Run {run}/{total}: failed ({r.Problem}). Your settings were put back; this combination is blocked.", trial);
                    if (r.Fatal) { outcome = "stopped: " + r.Problem; break; }
                    await b.SettleAsync(t => Report($"Recovering: {t}"), ct);
                    continue;
                }
                save();
                Report($"Run {run}/{total} done: {Summary(trial)}", trial);
            }
        }
        catch (OperationCanceledException) { outcome = "stopped by you"; }
        catch (Exception ex) { outcome = "stopped: " + ex.Message; Log.Error("genome session", ex); }
        finally
        {
            Report("Restoring your settings…");
            var problems = await RestoreAsync(s.Factors.Select(f => f.Id));
            // Read back what the interfaces can confirm.
            var now = b.ReadCurrent(auto);
            foreach (var f in auto.Where(f => now.GetValueOrDefault(f.Id) != original.GetValueOrDefault(f.Id)))
                if (f.Kind != FactorKind.GpuClockCap) problems.Add($"{f.Name} reads {f.LabelOf(now.GetValueOrDefault(f.Id, "?"))}, expected {f.LabelOf(original[f.Id])}");
            if (problems.Count == 0) b.ClearPending();
            session.Ended = DateTime.Now;
            session.Outcome = outcome + (problems.Count == 0 ? ". Your settings were restored and read back." : ". Restore problems: " + string.Join("; ", problems));
            s.Note($"Session on {w.Name}: {session.Outcome}");
            save();
            Report(session.Outcome);
        }
        return session;
    }

    /// <summary>After a crash: puts the settings from the interrupted session back. Returns problems (empty = done).</summary>
    public static List<string> Recover(GenomeStore s, IGenomeBackend b, Dictionary<string, string> original)
    {
        var problems = new List<string>();
        var auto = s.Factors.Where(f => !f.IsManual && original.ContainsKey(f.Id)).ToList();
        var now = b.ReadCurrent(auto);
        foreach (var f in auto)
        {
            if (now.GetValueOrDefault(f.Id) == original[f.Id] && f.Kind != FactorKind.GpuClockCap) continue;
            if (f.Level(original[f.Id]) is not { } l) { problems.Add($"{f.Name}: original level unknown"); continue; }
            var (ok, msg) = b.Apply(f, l);
            if (!ok) problems.Add($"{f.Name}: {msg}");
        }
        if (problems.Count == 0) { b.ClearPending(); s.Note("Recovered settings from an interrupted session."); }
        return problems;
    }

    public static string Summary(Trial t)
    {
        if (t.Measured is not { } m) return t.Problem ?? "no data";
        var parts = new List<string>();
        if (m.Get("fps") is { } fps) parts.Add($"{fps:0.0} fps" + (m.FpsCapped ? " (at refresh cap)" : ""));
        if (m.Get("low1") is { } l) parts.Add($"1% low {l:0.0}");
        if (m.Get("gputemp") is { } gt) parts.Add($"GPU {gt:0} °C");
        if (m.Get("gpupower") is { } gp) parts.Add($"{gp:0} W");
        if (m.Get("cputemp") is { } ctemp) parts.Add($"CPU {ctemp:0} °C");
        return string.Join(" · ", parts);
    }
}
