using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Ops;

/// <summary>Changes a Windows service start type (2 Automatic, 3 Manual, 4 Disabled) through its registry Start value.</summary>
public sealed class ServiceStartOp(string service, int start) : TweakOp
{
    public string Service { get; } = service;
    public int Start { get; } = start;
    private string KeyPath => $@"SYSTEM\CurrentControlSet\Services\{Service}";

    public override bool RequiresAdmin => true;
    public override string Describe() => $"Service {Service} start type -> {StartName(Start)}";

    public static string StartName(int s) => s switch { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => s.ToString(CultureInfo.InvariantCulture) };

    public int? CurrentStart()
    {
        using var k = Registry.LocalMachine.OpenSubKey(KeyPath);
        return k?.GetValue("Start") is int i ? i : null;
    }

    public override OpState Check() => CurrentStart() switch
    {
        null => OpState.NotApplicable,
        var s when s == Start => OpState.Applied,
        // Already "stricter" than we want (e.g. Disabled when we set Manual) counts as applied.
        var s when s > Start => OpState.Applied,
        _ => OpState.NotApplied
    };

    public override OpBackup Capture()
    {
        var b = new OpBackup { Kind = "service" };
        b.Data["start"] = CurrentStart()?.ToString(CultureInfo.InvariantCulture);
        using var k = Registry.LocalMachine.OpenSubKey(KeyPath);
        b.Data["delayed"] = (k?.GetValue("DelayedAutostart") as int?)?.ToString(CultureInfo.InvariantCulture);
        return b;
    }

    public override void Apply()
    {
        if (CurrentStart() is null) return; // not installed on this PC
        SetStart(Start);
        if (Start == 4) TryStop();
    }

    public override void Undo(OpBackup? backup)
    {
        if (CurrentStart() is null) return;
        var original = backup?.Data.GetValueOrDefault("start") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 3;
        SetStart(original);
        if (backup?.Data.GetValueOrDefault("delayed") is { } d)
        {
            using var k = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            k?.SetValue("DelayedAutostart", int.Parse(d, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        }
        if (original == 2) TryStartService();
    }

    private void SetStart(int value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true)
                ?? throw new TweakOpException($"Service {Service} not found");
            k.SetValue("Start", value, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Some services have protected keys; sc.exe goes through the SCM and may still succeed.
            var mode = value switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => "demand" };
            var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "sc.exe"), $"config \"{Service}\" start= {mode}");
            if (!r.Ok) throw new TweakOpException($"Windows protects the {Service} service and refused the change. {r.Combined}", ex);
        }
    }

    private void TryStop()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(Service);
            if (sc.Status is System.ServiceProcess.ServiceControllerStatus.Running && sc.CanStop)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex) { Log.Warn($"Could not stop {Service} now (takes effect after reboot): {ex.Message}"); }
    }

    private void TryStartService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(Service);
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) sc.Start();
        }
        catch (Exception ex) { Log.Warn($"Could not start {Service}: {ex.Message}"); }
    }
}

/// <summary>Changes one power-plan setting on the active scheme (AC and DC), backing up the previous indices.</summary>
public sealed class PowerCfgOp(string subgroup, string setting, int value, string label) : TweakOp
{
    public override bool RequiresAdmin => false;
    public override string Describe() => $"Power plan: {label} = {value}";
    private static string PowerCfg => Path.Combine(ProcessRunner.System32, "powercfg.exe");

    private (int? ac, int? dc) Query()
    {
        var r = ProcessRunner.Run(PowerCfg, $"/q scheme_current {subgroup} {setting}");
        if (!r.Ok) return (null, null);
        // Labels are localized, but the two index lines are always the last two "0x" values in the output.
        var hex = Regex.Matches(r.StdOut, @":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline)
            .Select(m => int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToList();
        return hex.Count >= 2 ? (hex[^2], hex[^1]) : (null, null);
    }

    public override OpState Check()
    {
        var (ac, dc) = Query();
        if (ac is null) return OpState.NotApplicable;
        return ac == value && dc == value ? OpState.Applied : OpState.NotApplied;
    }

    public override OpBackup Capture()
    {
        var (ac, dc) = Query();
        return new OpBackup { Kind = "powercfg", Data = { ["ac"] = ac?.ToString(CultureInfo.InvariantCulture), ["dc"] = dc?.ToString(CultureInfo.InvariantCulture) } };
    }

    public override void Apply() => Set(value, value);

    public override void Undo(OpBackup? backup)
    {
        if (backup?.Data.GetValueOrDefault("ac") is not { } ac) return;
        var dc = backup.Data.GetValueOrDefault("dc") ?? ac;
        Set(int.Parse(ac, CultureInfo.InvariantCulture), int.Parse(dc, CultureInfo.InvariantCulture));
    }

    private void Set(int ac, int dc)
    {
        var r1 = ProcessRunner.Run(PowerCfg, $"/setacvalueindex scheme_current {subgroup} {setting} {ac}");
        if (!r1.Ok) throw new TweakOpException($"powercfg failed: {r1.Combined}");
        ProcessRunner.Run(PowerCfg, $"/setdcvalueindex scheme_current {subgroup} {setting} {dc}");
        ProcessRunner.Run(PowerCfg, "/setactive scheme_current");
    }
}

public enum Shell { Cmd, PowerShell }

/// <summary>
/// Runs an explicit apply command and an explicit undo command. An optional check command plus regex decides
/// whether the change is active; without one, state comes from Money Tweaks' backup record.
/// </summary>
public sealed class CommandOp : TweakOp
{
    public required string ApplyCommand { get; init; }
    public required string UndoCommand { get; init; }
    public Shell Shell { get; init; } = Shell.Cmd;
    /// <summary>Shell for <see cref="CheckCommand"/>; defaults to <see cref="Shell"/>. PowerShell output is not localized.</summary>
    public Shell? CheckShell { get; init; }
    public string? CheckCommand { get; init; }
    public string? AppliedPattern { get; init; }
    public bool Admin { get; init; } = true;
    public string? Label { get; init; }
    /// <summary>Exit codes other than 0 treated as success (e.g. robocopy-like tools).</summary>
    public int[] OkExitCodes { get; init; } = [0];

    public override bool RequiresAdmin => Admin;
    public override string Describe() => Label ?? ApplyCommand;

    private static ProcessResult Exec(string c, Shell shell) => shell == Shell.Cmd ? ProcessRunner.Cmd(c) : ProcessRunner.PowerShell(c);

    public override OpState Check()
    {
        if (CheckCommand is null || AppliedPattern is null) return OpState.Unknown;
        try
        {
            var r = Exec(CheckCommand, CheckShell ?? Shell);
            return Regex.IsMatch(r.Combined, AppliedPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline) ? OpState.Applied : OpState.NotApplied;
        }
        catch { return OpState.Unknown; }
    }

    public override OpBackup Capture() => new() { Kind = "command" };

    public override void Apply() => RunChecked(ApplyCommand);
    public override void Undo(OpBackup? backup) => RunChecked(UndoCommand);

    private void RunChecked(string c)
    {
        var r = Exec(c, Shell);
        if (!OkExitCodes.Contains(r.ExitCode)) throw new TweakOpException($"Command failed ({r.ExitCode}): {Trim(r.Combined)}");
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}

/// <summary>Disables a built-in scheduled task through the Task Scheduler COM API.</summary>
public sealed class ScheduledTaskOp(string taskPath) : TweakOp
{
    public string TaskPath { get; } = taskPath;
    public override bool RequiresAdmin => true;
    public override string Describe() => $"Disable scheduled task {TaskPath}";

    private static dynamic? GetTask(string path)
    {
        try
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) return null;
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();
            var idx = path.LastIndexOf('\\');
            dynamic folder = svc.GetFolder(idx <= 0 ? "\\" : path[..idx]);
            return folder.GetTask(path[(idx + 1)..]);
        }
        catch { return null; }
    }

    public override OpState Check()
    {
        var task = GetTask(TaskPath);
        if (task is null) return OpState.NotApplicable;
        return (bool)task.Enabled ? OpState.NotApplied : OpState.Applied;
    }

    public override OpBackup Capture()
    {
        var task = GetTask(TaskPath);
        return new OpBackup { Kind = "task", Data = { ["enabled"] = task is null ? null : ((bool)task.Enabled).ToString() } };
    }

    public override void Apply() => SetEnabled(false);

    public override void Undo(OpBackup? backup)
    {
        if (backup?.Data.GetValueOrDefault("enabled") == "False") return; // it was already disabled before us
        SetEnabled(true);
    }

    private void SetEnabled(bool enabled)
    {
        if (GetTask(TaskPath) is null) return;
        var r = ProcessRunner.Run(Path.Combine(ProcessRunner.System32, "schtasks.exe"),
            $"/Change /TN \"{TaskPath}\" {(enabled ? "/Enable" : "/Disable")}");
        if (!r.Ok) throw new TweakOpException($"schtasks failed for {TaskPath}: {r.Combined}");
    }
}

/// <summary>A registry op whose concrete targets are discovered at runtime (network interfaces, GPUs, startup entries…).</summary>
public sealed class DynamicRegOp(string label, bool admin, Func<IEnumerable<RegValueOp>> discover) : TweakOp
{
    public override bool RequiresAdmin => admin;
    public override string Describe() => label;

    private List<RegValueOp> Targets()
    {
        try { return discover().ToList(); }
        catch (Exception ex) { Log.Warn($"{label}: discovery failed: {ex.Message}"); return []; }
    }

    private static string Key(RegValueOp o) => $@"{o.Hive}\{o.Path}\{o.Name}";

    public override OpState Check()
    {
        var t = Targets();
        if (t.Count == 0) return OpState.NotApplicable;
        return t.All(o => o.Check() == OpState.Applied) ? OpState.Applied : OpState.NotApplied;
    }

    public override OpBackup Capture()
    {
        var b = new OpBackup { Kind = "dynreg" };
        foreach (var o in Targets())
        {
            var ob = o.Capture();
            b.Data[Key(o) + "|kind"] = ob.Data.GetValueOrDefault("kind");
            b.Data[Key(o) + "|value"] = ob.Data.GetValueOrDefault("value");
            b.Data[Key(o) + "|keyExisted"] = ob.Data.GetValueOrDefault("keyExisted");
        }
        return b;
    }

    public override void Apply()
    {
        var t = Targets();
        if (t.Count == 0) throw new TweakOpException($"{label}: nothing found to change on this PC.");
        foreach (var o in t) o.Apply();
    }

    public override void Undo(OpBackup? backup)
    {
        foreach (var o in Targets())
        {
            OpBackup? ob = null;
            if (backup is not null && backup.Data.ContainsKey(Key(o) + "|keyExisted"))
            {
                ob = new OpBackup { Kind = "reg" };
                ob.Data["keyExisted"] = backup.Data[Key(o) + "|keyExisted"];
                if (backup.Data.GetValueOrDefault(Key(o) + "|kind") is { } k)
                {
                    ob.Data["kind"] = k;
                    ob.Data["value"] = backup.Data.GetValueOrDefault(Key(o) + "|value");
                }
            }
            o.Undo(ob);
        }
    }
}
