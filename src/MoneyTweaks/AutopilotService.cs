using System.Diagnostics;
using System.Windows;
using MoneyTweaks.Core.Games;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>
/// Game Autopilot at runtime: watches the games that have a rule (verified process identity, every 3 s, only while at
/// least one rule is on or a session runs), starts one session at a time and ends it when the game exits or crashes.
/// A session changes settings only through the Gaming Profiles engine (values recorded before any change, verified
/// restore, crash recovery), raises only the game's own process priority, shows and hides the overlay, and holds the
/// sensor hub open so its existing warnings are recorded. The session is saved before anything changes; after an
/// unexpected shutdown it is resumed if the game still runs, otherwise ended and restored.
/// </summary>
public static class AutopilotService
{
    public static AutopilotFile File { get; private set; } = AutopilotStore.Load();
    public static event Action? Changed;
    /// <summary>Games with a rule that are running while another session is active (they start when it ends).</summary>
    public static IReadOnlyList<string> Waiting { get; private set; } = [];

    private static CancellationTokenSource? _watch;
    private static readonly HashSet<int> Suppressed = [];
    private static readonly Dictionary<int, string?> Paths = [];
    private static readonly object HubToken = new();
    private static readonly Lock Gate = new();
    private static Action<SensorFrame>? _sampled;
    private static Action<MonitorAlert>? _alert;
    private static bool _busy;

    public static void Save()
    {
        try { AutopilotStore.Save(File); }
        catch (Exception ex) { Log.Error("Saving autopilot.json failed", ex); }
        Changed?.Invoke();
    }

    /// <summary>Whether a still-active Gaming Profiles session belongs to an Autopilot session (Autopilot restores it).</summary>
    public static bool OwnsProfileSession(ProfileSession s) => File.Active?.OwnedProfileId is { } id && id == s.ProfileId;

    public static AutopilotRule RuleFor(string key, string name)
    {
        if (!File.Rules.TryGetValue(key, out var r)) File.Rules[key] = r = new AutopilotRule { GameKey = key, GameName = name };
        r.GameName = name;
        return r;
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------------------------
    /// <summary>At startup (after the gaming-profile check): resume or recover a session left by an unexpected shutdown.</summary>
    public static async Task InitAsync()
    {
        var d = GameAutopilot.Recover(File, GameAutopilot.Alive);
        if (d.Kind == "resume" && File.Active is { } s && File.Rules.TryGetValue(s.GameKey, out var rule))
        {
            Log.Info($"Game Autopilot: resuming the {s.GameName} session (pid {s.Pid} still running)");
            StartLive(s, rule);
            MainWindow.Current?.ShowToast($"Game Autopilot picked up the {s.GameName} session again.", "Accent");
        }
        else if (d.Kind == "resume" || d.Kind == "end")
        {
            Log.Info($"Game Autopilot: recovering the {File.Active?.GameName} session: {d.Reason ?? "rule removed"}");
            await EndAsync(d.Reason ?? "its rule was removed", recovered: true);
        }
        Refresh();
    }

    /// <summary>Starts or stops the watcher to match the rules (no polling at all while Autopilot has nothing to do).</summary>
    public static void Refresh()
    {
        bool needed = File.Active is not null || (File.Enabled && File.Rules.Values.Any(r => r.Enabled));
        if (needed && _watch is null)
        {
            _watch = new CancellationTokenSource();
            var ct = _watch.Token;
            var ui = Application.Current.Dispatcher;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(3000, ct); } catch { break; }
                    try { await Poll(ui); } catch (Exception ex) { Log.Debug("autopilot watcher: " + ex.Message); }
                }
            });
            Log.Info("Game Autopilot watcher started");
        }
        else if (!needed && _watch is not null) { _watch.Cancel(); _watch = null; Log.Info("Game Autopilot watcher stopped"); }
        Changed?.Invoke();
    }

    private static List<(string key, int pid)> RunningRuleGames()
    {
        var keys = File.Rules.Values.Where(r => r.Enabled).Select(r => r.GameKey).ToHashSet();
        if (keys.Count == 0) return [];
        var games = GameLibrary.Items(GameLibrary.Current).Where(g => keys.Contains(g.Key)).ToList();
        var list = new List<(string, int)>();
        foreach (var (g, _, match, status) in GameRuntime.Statuses(games))
            if (status == GameStatus.Running && match.Process is { } p) { list.Add((g.Key, p.Pid)); lock (Gate) Paths[p.Pid] = p.Path; }
        return list;
    }

    private static async Task Poll(System.Windows.Threading.Dispatcher ui)
    {
        if (_busy) return;
        var running = RunningRuleGames();
        lock (Gate) Suppressed.RemoveWhere(pid => !GameAutopilot.Alive(pid));
        var d = GameAutopilot.Decide(File, running, GameAutopilot.Alive, Suppressed);
        var waiting = d.Waiting ?? [];
        if (!waiting.SequenceEqual(Waiting)) { Waiting = waiting; await ui.InvokeAsync(() => Changed?.Invoke()); }
        if (d.Kind == "start") await ui.InvokeAsync(() => StartAsync(d.GameKey!, d.Pid)).Task.Unwrap();
        else if (d.Kind == "end") await ui.InvokeAsync(() => EndAsync(d.Reason!)).Task.Unwrap();
    }

    // ---- start --------------------------------------------------------------------------------------------------------------
    public static async Task StartAsync(string key, int pid)
    {
        if (_busy || File.Active is not null || !File.Rules.TryGetValue(key, out var rule)) return;
        _busy = true;
        try
        {
            string? path; lock (Gate) Paths.TryGetValue(pid, out path);
            var s = new AutopilotSession { GameKey = key, GameName = rule.GameName, Pid = pid, Exe = path is null ? null : System.IO.Path.GetFileName(path) };
            File.Active = s;
            Save(); // recorded before anything changes
            Log.Info($"Game Autopilot: {rule.GameName} started (pid {pid}, {path ?? "path unknown"}); session {s.Id}");

            if (rule.Readiness) _ = ReadinessAsync(s, path);

            var game = GameLibrary.Find(GameLibrary.Current, key);
            var gameProfile = game?.Entry?.ProfileId is { } gp ? ProfileStore.All(ProfileService.File).FirstOrDefault(p => p.Id == gp) : null;
            if (GameAutopilot.ProfileFor(rule, gameProfile) is { } profile)
            {
                if (ProfileService.Active is { } act)
                    s.Skipped.Add($"Settings: kept the gaming profile “{act.ProfileName}” that was already on (Autopilot never replaces it or restores it).");
                else if (!Admin.IsElevated && await Task.Run(() => ProfileEngine.NeedsAdmin(profile)))
                    s.Skipped.Add("Settings: some of them are system-wide and Money Tweaks isn't running as administrator, so none were changed (no prompt during play).");
                else
                {
                    s.OwnedProfileId = profile.Id;
                    Save();
                    if (await ProfileService.ActivateAsync(profile, null, confirm: false, key))
                        s.Actions.Add(rule.Optimizations == AutopilotOptimizations.GameProfile ? $"Turned on the “{profile.Name}” gaming profile." : $"Applied {profile.TweakIds.Count} setting(s){(profile.Power != PowerChoice.Keep ? ", the power plan" : "")}{(profile.TimerResolutionMs is not null ? " and the timer" : "")} for the session.");
                    else { s.OwnedProfileId = null; s.Skipped.Add("Settings: not applied (see the message shown); nothing was changed."); }
                }
            }
            if (rule.RaisePriority)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (proc.PriorityClass == ProcessPriorityClass.Normal)
                    {
                        s.OriginalPriority = nameof(ProcessPriorityClass.Normal);
                        Save();
                        proc.PriorityClass = ProcessPriorityClass.High;
                        s.Actions.Add($"{s.Exe ?? rule.GameName} → High CPU priority.");
                    }
                    else s.Skipped.Add($"Priority: already {proc.PriorityClass}, left as is.");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                { s.Skipped.Add("Priority: Windows doesn't allow changing this game's priority (often protected by its anti-cheat); left as is."); }
            }
            if (rule.ShowOverlay)
            {
                if (!OverlayService.Shown) { s.OverlayShownByAutopilot = true; Save(); OverlayService.Show(); s.Actions.Add("Showed the performance overlay."); }
                else s.Skipped.Add("Overlay: already on, left as is.");
            }
            StartLive(s, rule);
            Save();
            MainWindow.Current?.ShowToast($"Game Autopilot: {rule.GameName} session started" + (s.Actions.Count > 0 ? $" ({s.Actions.Count} action(s))." : "."), "Accent");
        }
        catch (Exception ex) { Log.Error("Game Autopilot: starting the session failed", ex); }
        finally { _busy = false; }
    }

    private static async Task ReadinessAsync(AutopilotSession s, string? path)
    {
        try
        {
            SensorHub.Shared.Acquire(HubToken, record: false);
            var items = await Task.Run(() => { Thread.Sleep(1500); return GameAutopilot.Readiness(GameAutopilot.ReadInputs(path, SensorHub.Shared.Last)); });
            if (File.Active?.Id != s.Id) return;
            s.Readiness = [.. items.Select(i => $"{(i.Ok ? "OK" : "Check")} · {i.Area}: {i.Text}")];
            Save();
            int notes = items.Count(i => !i.Ok);
            Log.Info($"Game Autopilot readiness for {s.GameName}: {notes} to check of {items.Count}");
            if (notes > 0) MainWindow.Current?.ShowToast($"Game Autopilot: {notes} thing(s) to check for {s.GameName}: " + string.Join("; ", items.Where(i => !i.Ok).Select(i => i.Area)), "Gold");
        }
        catch (Exception ex) { Log.Warn("Game Autopilot readiness check failed: " + ex.Message); }
    }

    /// <summary>Live protection: the sensor hub's own warnings (heat, throttling, memory) recorded for the session.</summary>
    private static void StartLive(AutopilotSession s, AutopilotRule rule)
    {
        StopLive();
        SensorHub.Shared.Acquire(HubToken, record: false);
        _sampled = f =>
        {
            var cpu = f.Values.GetValueOrDefault("cpu.temp");
            var (_, gpuT, _, _) = OverlayService.BusiestGpu(f);
            lock (Gate)
            {
                if (cpu is { } c && c > (s.PeakCpuTempC ?? double.MinValue)) s.PeakCpuTempC = c;
                if (gpuT is { } g && g > (s.PeakGpuTempC ?? double.MinValue)) s.PeakGpuTempC = g;
            }
        };
        _alert = a =>
        {
            if (!rule.LiveWarnings) return;
            lock (Gate) s.Warnings.Add($"{DateTime.Now:t} {a.Title}: {a.Detail}");
            Log.Info($"Game Autopilot warning during {s.GameName}: {a.Title}");
            // The Hardware Monitor already shows a toast when its own warning toasts are on; otherwise one here, quietly.
            if (!App.Settings.Monitor.AlertToasts)
                Application.Current?.Dispatcher.BeginInvoke(() => MainWindow.Current?.ShowToast($"{s.GameName}: {a.Title}", a.Level == AlertLevel.Critical ? "Danger" : "Gold"));
        };
        SensorHub.Shared.Sampled += _sampled;
        SensorHub.Shared.AlertStarted += _alert;
    }

    private static void StopLive()
    {
        if (_sampled is not null) SensorHub.Shared.Sampled -= _sampled;
        if (_alert is not null) SensorHub.Shared.AlertStarted -= _alert;
        _sampled = null; _alert = null;
        SensorHub.Shared.Release(HubToken);
    }

    // ---- end ----------------------------------------------------------------------------------------------------------------
    /// <summary>Ends the session: puts back exactly what it changed (overlay, priority, the profile it started), then records it.</summary>
    public static async Task EndAsync(string reason, bool recovered = false)
    {
        if (File.Active is not { } s) return;
        _busy = true;
        try
        {
            StopLive();
            if (reason == "exited")
                reason = GameAutopilot.CrashedRecently(s.Exe, TimeSpan.FromMinutes(3)) ? "the game crashed (Windows logged an application error)" : "the game closed";
            if (s.OverlayShownByAutopilot)
            {
                if (OverlayService.Shown) { OverlayService.Hide(); s.Restored.Add("Overlay hidden again."); }
                else s.Restored.Add("Overlay was already closed.");
            }
            if (s.OriginalPriority is { } prio && GameAutopilot.Alive(s.Pid))
            {
                try { using var p = Process.GetProcessById(s.Pid); p.PriorityClass = Enum.Parse<ProcessPriorityClass>(prio); s.Restored.Add($"Priority back to {prio}."); }
                catch (Exception ex) { s.RestoreProblems.Add("Priority couldn't be put back: " + ex.Message); }
            }
            if (s.OwnedProfileId is { } id)
            {
                if (ProfileService.Active?.ProfileId == id)
                {
                    if (await ProfileService.DeactivateAsync(userInitiated: false)) s.Restored.Add("Every changed setting restored from its recorded value and read back.");
                    else s.RestoreProblems.Add("Some settings couldn't be restored yet (shown in the message). They stay recorded; Gaming Profiles can retry.");
                }
                else s.Restored.Add("Settings were already restored.");
            }
            s.EndedAt = DateTime.Now;
            s.EndedBy = reason;
            File.History.Add(s);
            File.Active = null;
            Save();
            Log.Info($"Game Autopilot: {s.GameName} session ended ({reason}) after {s.Duration:hh\\:mm\\:ss}; restored {s.Restored.Count}, problems {s.RestoreProblems.Count}, warnings {s.Warnings.Count}");
            if (File.Rules.GetValueOrDefault(s.GameKey)?.Summary != false)
                MainWindow.Current?.ShowToast($"Game Autopilot: {s.GameName} {(recovered ? "session recovered" : "session ended")} · {Math.Round(s.Duration?.TotalMinutes ?? 0)} min" +
                    (s.Warnings.Count > 0 ? $" · {s.Warnings.Count} warning(s)" : "") + (s.RestoreProblems.Count > 0 ? " · restore needs attention" : s.Restored.Count > 0 ? " · everything restored" : ""),
                    s.RestoreProblems.Count > 0 ? "Gold" : "Success");
        }
        catch (Exception ex) { Log.Error("Game Autopilot: ending the session failed", ex); }
        finally { _busy = false; Refresh(); }
    }

    /// <summary>"End session now": restore immediately and don't start again for this run of the game.</summary>
    public static async Task EndNowAsync()
    {
        if (File.Active is not { } s) return;
        lock (Gate) Suppressed.Add(s.Pid);
        await EndAsync("ended by you");
    }

    /// <summary>App exit: put back the overlay and priority and close the record; the gaming-profile shutdown restores settings after.</summary>
    public static void Shutdown()
    {
        _watch?.Cancel(); _watch = null;
        if (File.Active is not { } s) return;
        try
        {
            StopLive();
            if (s.OverlayShownByAutopilot && OverlayService.Shown) { OverlayService.Hide(); s.Restored.Add("Overlay hidden."); }
            if (s.OriginalPriority is { } prio && GameAutopilot.Alive(s.Pid))
                try { using var p = Process.GetProcessById(s.Pid); p.PriorityClass = Enum.Parse<ProcessPriorityClass>(prio); s.Restored.Add($"Priority back to {prio}."); } catch { }
            if (s.OwnedProfileId is not null) s.Restored.Add("Settings restored by the gaming-profile shutdown.");
            s.EndedAt = DateTime.Now;
            s.EndedBy = "Money Tweaks was closed";
            File.History.Add(s);
            File.Active = null;
            AutopilotStore.Save(File);
        }
        catch (Exception ex) { Log.Error("Game Autopilot shutdown", ex); }
    }
}
