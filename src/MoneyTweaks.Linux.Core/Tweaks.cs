using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoneyTweaks.Linux;

public enum LinuxTweakState { NotApplied, Applied, Partial, NotAvailable }

/// <summary>
/// A reversible kernel setting: one value written to one or more /proc/sys or /sys files. Changes last until the next restart
/// (Money Tweaks for Linux doesn't edit boot or sysctl configuration files).
/// </summary>
public sealed record LinuxTweak(string Id, string Name, string Purpose, string Risk, string Value,
    Func<LinuxFs, IReadOnlyList<string>> Targets, Func<LinuxFs, string, bool>? Supported = null, bool AtLeast = false)
{
    public const string Persistence = "until the next restart (the kernel starts with its defaults again)";

    /// <summary>The value at <paramref name="current"/> counts as this tweak's (numeric "at least" for limits).</summary>
    public bool Holds(string? current) => current is not null && (AtLeast && long.TryParse(current, out var c) && long.TryParse(Value, out var v) ? c >= v : current == Value);
}

public static class LinuxCatalog
{
    private static IReadOnlyList<string> Cpus(LinuxFs fs, string file) =>
        fs.List("/sys/devices/system/cpu", @"cpu\d+").Select(c => c + "/cpufreq/" + file).Where(fs.Exists).ToList();

    /// <summary>The "available_*" list next to a cpufreq file offers <paramref name="value"/>.</summary>
    private static Func<LinuxFs, string, bool> Offers(string listFile, string value) =>
        (fs, target) => (fs.ReadLine(target[..(target.LastIndexOf('/') + 1)] + listFile) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value);

    public static IReadOnlyList<LinuxTweak> All { get; } =
    [
        new("cpu.governor", "CPU governor: performance",
            "Sets every core's cpufreq governor to \"performance\" instead of a power-saving one, so clocks don't have to ramp up first. Uses more power and heat; on modern CPUs the FPS difference is often not measurable.",
            "Low (more power use; laptops run hotter and drain faster)", "performance",
            fs => Cpus(fs, "scaling_governor"), Offers("scaling_available_governors", "performance")),
        new("cpu.epp", "CPU energy preference: performance",
            "Sets the energy/performance preference that the CPU's own power management (Intel HWP, AMD P-State EPP) follows to \"performance\". Only present with the intel_pstate or amd-pstate-epp drivers.",
            "Low (more power use)", "performance",
            fs => Cpus(fs, "energy_performance_preference"), Offers("energy_performance_available_preferences", "performance")),
        new("vm.swappiness", "Swappiness: 10",
            "Makes the kernel prefer dropping file cache over moving running programs to swap (the usual default is 60). Matters mostly when memory is nearly full.",
            "Low", "10", fs => fs.Exists("/proc/sys/vm/swappiness") ? ["/proc/sys/vm/swappiness"] : []),
        new("vm.max_map_count", "Memory map limit: 1048576",
            "Raises how many memory mappings one program may use. Some games and Proton titles crash with the old default of 65530; SteamOS and several distributions already ship a higher value.",
            "Low", "1048576", fs => fs.Exists("/proc/sys/vm/max_map_count") ? ["/proc/sys/vm/max_map_count"] : [], AtLeast: true),
    ];
}

/// <summary>Writes values to whitelisted kernel files with root rights.</summary>
public interface IPrivilegedWriter
{
    /// <summary>Writes each value to its file. Throws with a readable reason when it can't.</summary>
    void Write(IReadOnlyDictionary<string, string> values);
}

/// <summary>
/// Root writes through polkit: one "pkexec tee FILE…" per distinct value, the value on standard input (no shell, nothing
/// interpolated into a command line). Only the kernel files Money Tweaks' tweaks use and simple values are accepted.
/// Already root: written directly.
/// </summary>
public sealed class PkexecWriter : IPrivilegedWriter
{
    private static readonly Regex AllowedPath = new(@"^(/sys/devices/system/cpu/cpu\d+/cpufreq/(scaling_governor|energy_performance_preference)|/proc/sys/vm/(swappiness|max_map_count))$");
    private static readonly Regex AllowedValue = new(@"^[a-z0-9_]{1,32}$");

    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (path, value) in values)
        {
            if (!AllowedPath.IsMatch(path)) throw new InvalidOperationException($"Refused: {path} isn't a file Money Tweaks changes.");
            if (!AllowedValue.IsMatch(value)) throw new InvalidOperationException($"Refused: \"{value}\" isn't a plain setting value.");
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
    private static bool IsRoot() { try { return GetEuid() == 0; } catch { return false; } }

    public void Write(IReadOnlyDictionary<string, string> values)
    {
        Validate(values);
        if (IsRoot())
        {
            foreach (var (path, value) in values) File.WriteAllText(path, value + "\n");
            return;
        }
        string pkexec = Sampler.Which("pkexec") ?? throw new InvalidOperationException("pkexec (polkit) isn't installed, so Money Tweaks can't ask for root rights. Install polkit, or start Money Tweaks with sudo.");
        string tee = new[] { "/usr/bin/tee", "/bin/tee" }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("tee wasn't found in /usr/bin or /bin.");
        foreach (var group in values.GroupBy(kv => kv.Value))
        {
            var psi = new ProcessStartInfo(pkexec) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(tee);
            foreach (var kv in group) psi.ArgumentList.Add(kv.Key);
            using var p = Process.Start(psi)!;
            p.StandardInput.Write(group.Key + "\n");
            p.StandardInput.Close();
            _ = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 126) throw new InvalidOperationException("The password prompt was cancelled; nothing was changed.");
            if (p.ExitCode == 127) throw new InvalidOperationException("polkit didn't authorise the change" + (err.Length > 0 ? ": " + err : "."));
            if (p.ExitCode != 0) throw new InvalidOperationException($"Writing failed (exit {p.ExitCode}){(err.Length > 0 ? ": " + err : "")}");
        }
    }
}

/// <summary>One file a tweak changes: the value now, after applying, and what Undo restores (Tweak X-Ray).</summary>
public sealed record LinuxChange(string Path, string Current, string Proposed, string Undo, bool AlreadySet);

public sealed record LinuxResult(bool Success, string Message, LinuxTweakState State);

public sealed class LinuxBackup
{
    public string TweakId { get; set; } = "";
    public DateTime AppliedAt { get; set; }
    /// <summary>Kernel boot id when applied: after a restart the kernel has its defaults again and the backup is stale.</summary>
    public string BootId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
}

/// <summary>Apply, verify, roll back and undo, with one JSON backup of the original values per applied tweak.</summary>
public sealed class LinuxTweakEngine(LinuxFs fs, IPrivilegedWriter writer, string backupDir)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Action<string> Log { get; set; } = _ => { };

    private string BackupPath(LinuxTweak t) => Path.Combine(backupDir, t.Id + ".json");
    private string BootId => fs.ReadLine("/proc/sys/kernel/random/boot_id") ?? "";

    /// <summary>The backup of the original values, if the tweak was applied during this boot.</summary>
    public LinuxBackup? Backup(LinuxTweak t)
    {
        try
        {
            var b = JsonSerializer.Deserialize<LinuxBackup>(File.ReadAllText(BackupPath(t)));
            if (b is null) return null;
            if (b.BootId != BootId) { File.Delete(BackupPath(t)); return null; }   // restarted since: the kernel reset the values
            return b;
        }
        catch { return null; }
    }

    public (LinuxTweakState state, string? reason) State(LinuxTweak t)
    {
        var targets = t.Targets(fs);
        if (targets.Count == 0) return (LinuxTweakState.NotAvailable, "this kernel or CPU driver doesn't provide the setting");
        if (t.Supported is { } ok && !targets.All(p => ok(fs, p))) return (LinuxTweakState.NotAvailable, $"the driver doesn't offer \"{t.Value}\"");
        int set = targets.Count(p => t.Holds(fs.ReadLine(p)));
        return (set == targets.Count ? LinuxTweakState.Applied : set == 0 ? LinuxTweakState.NotApplied : LinuxTweakState.Partial, null);
    }

    public IReadOnlyList<LinuxChange> Inspect(LinuxTweak t)
    {
        var backup = Backup(t);
        return t.Targets(fs).Select(p =>
        {
            string? cur = fs.ReadLine(p);
            string undo = backup?.Values.GetValueOrDefault(p) is { } orig ? $"back to {orig} (backed up {backup.AppliedAt:g})" : "back to the current value, captured when you apply";
            return new LinuxChange(p, cur ?? "unreadable", t.Value + (t.AtLeast ? " (or higher)" : ""), undo, t.Holds(cur));
        }).ToList();
    }

    public LinuxResult Apply(LinuxTweak t)
    {
        var (state, reason) = State(t);
        if (state == LinuxTweakState.NotAvailable) return new(false, "Not available on this PC: " + reason + ".", state);
        if (state == LinuxTweakState.Applied) return new(true, "Already set; nothing was changed.", state);
        var targets = t.Targets(fs);
        var backup = Backup(t);
        bool fresh = backup is null;
        if (fresh)
        {
            backup = new LinuxBackup { TweakId = t.Id, AppliedAt = DateTime.Now, BootId = BootId };
            foreach (var p in targets) backup.Values[p] = fs.ReadLine(p) ?? throw new InvalidOperationException($"Couldn't read {p}; nothing was changed.");
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(BackupPath(t), JsonSerializer.Serialize(backup, Json));
            foreach (var (p, v) in backup.Values) Log($"{t.Id} before: {p} = {v}");
        }
        var todo = targets.Where(p => !t.Holds(fs.ReadLine(p))).ToDictionary(p => p, _ => t.Value);
        try { writer.Write(todo); }
        catch (Exception ex)
        {
            if (fresh) File.Delete(BackupPath(t));
            Log($"{t.Id} apply failed: {ex.Message}");
            return new(false, ex.Message, State(t).state);
        }
        // Read every file back: drivers can refuse a value (e.g. EPP while the governor is "performance") without an error.
        var wrong = todo.Keys.Where(p => !t.Holds(fs.ReadLine(p))).ToList();
        if (wrong.Count > 0)
        {
            try { writer.Write(todo.Keys.ToDictionary(p => p, p => backup!.Values[p])); } catch (Exception ex) { Log($"{t.Id} rollback failed: {ex.Message}"); }
            if (fresh) File.Delete(BackupPath(t));
            Log($"{t.Id}: read-back differs at {string.Join(", ", wrong)}; rolled back");
            return new(false, $"The kernel didn't keep the value ({wrong[0]} reads \"{fs.ReadLine(wrong[0])}\"), so the change was rolled back.", State(t).state);
        }
        Log($"{t.Id} applied and verified ({todo.Count} file(s))");
        return new(true, "Applied and verified. Lasts " + LinuxTweak.Persistence + ".", LinuxTweakState.Applied);
    }

    public LinuxResult Undo(LinuxTweak t)
    {
        var backup = Backup(t);
        if (backup is null) return new(false, "There's no backup from this boot to restore (after a restart the kernel already uses its defaults).", State(t).state);
        var restore = backup.Values.Where(kv => fs.ReadLine(kv.Key) != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        try { writer.Write(restore); }
        catch (Exception ex) { Log($"{t.Id} undo failed: {ex.Message}"); return new(false, ex.Message, State(t).state); }
        var wrong = restore.Keys.Where(p => fs.ReadLine(p) != backup.Values[p]).ToList();
        if (wrong.Count > 0) return new(false, $"Restored, but {wrong[0]} reads \"{fs.ReadLine(wrong[0])}\" instead of \"{backup.Values[wrong[0]]}\". The backup is kept so you can retry.", State(t).state);
        File.Delete(BackupPath(t));
        Log($"{t.Id} undone and verified");
        return new(true, "Restored the original values and verified them.", State(t).state);
    }
}
