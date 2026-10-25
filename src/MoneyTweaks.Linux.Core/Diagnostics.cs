namespace MoneyTweaks.Linux;

/// <summary>Where Money Tweaks for Linux keeps its files (XDG state directory) and its log.</summary>
public static class LinuxPaths
{
    public static string State
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "state") : xdg, "money-tweaks");
        }
    }

    public static string Backups => Path.Combine(State, "backups");
    public static string LogFile => Path.Combine(State, "money-tweaks.log");

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(State);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch { /* logging must never break the app */ }
    }
}

/// <summary>
/// System facts for bug reports: distribution, kernel, CPU, GPU driver, desktop session and which sensors and settings this
/// PC exposes. No user names, host names, paths in the home folder or network addresses.
/// </summary>
public static class LinuxDiagnostics
{
    public static string OsName(LinuxFs fs)
    {
        foreach (var line in (fs.Read("/etc/os-release") ?? "").Split('\n'))
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) return line[12..].Trim().Trim('"');
        return "unknown distribution";
    }

    public static string CpuModel(LinuxFs fs)
    {
        foreach (var line in (fs.Read("/proc/cpuinfo") ?? "").Split('\n'))
            if (line.StartsWith("model name", StringComparison.Ordinal) && line.Contains(':')) return line[(line.IndexOf(':') + 1)..].Trim();
        return "unknown CPU";
    }

    public static string Report(LinuxFs fs, string version, LinuxTweakEngine engine)
    {
        var temps = Proc.HwmonTemps(fs);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Money Tweaks for Linux {version} (preview)");
        sb.AppendLine($"Distribution: {OsName(fs)}");
        sb.AppendLine($"Kernel: {fs.ReadLine("/proc/sys/kernel/osrelease") ?? "unknown"}");
        sb.AppendLine($"CPU: {CpuModel(fs)}");
        sb.AppendLine($"cpufreq driver: {fs.ReadLine("/sys/devices/system/cpu/cpu0/cpufreq/scaling_driver") ?? "none"}");
        sb.AppendLine($"Session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"} / {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown"}");
        sb.AppendLine($"Temperature sensors: {(temps.Count == 0 ? "none" : string.Join(", ", temps.Select(t => $"{t.Chip}:{t.Label}").Distinct()))}");
        sb.AppendLine($"CPU temperature source: {(Proc.CpuTemp(temps, fs) is { } c ? $"{c.Chip} {c.Label}" : "none found")}");
        sb.AppendLine($"nvidia-smi: {(Sampler.Which("nvidia-smi") is null ? "not found" : "found")}, pkexec: {(Sampler.Which("pkexec") is null ? "not found" : "found")}");
        foreach (var t in LinuxCatalog.All)
        {
            var (state, reason) = engine.State(t);
            sb.AppendLine($"Tweak {t.Id}: {state}{(reason is null ? "" : " (" + reason + ")")}");
        }
        return sb.ToString();
    }

    /// <summary>A GitHub "new issue" link with the report prefilled (opened in the browser; nothing is sent automatically).</summary>
    public static string IssueUrl(string title, string report) =>
        "https://github.com/MoneyMan82/money-tweaks/issues/new?title=" + Uri.EscapeDataString("[Linux] " + title)
        + "&body=" + Uri.EscapeDataString("What happened:\n\n\nWhat I expected:\n\n\n---\n" + report);
}
