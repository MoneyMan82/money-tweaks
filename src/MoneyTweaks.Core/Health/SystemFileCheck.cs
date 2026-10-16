using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Scanner;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Health;

public enum FileCheckKind { DismCheckHealth, DismScanHealth, SfcVerifyOnly, SfcScanNow, DismRestoreHealth }

public sealed record FileCheckInfo(FileCheckKind Kind, string Title, string What, string Duration, bool Repairs, bool Cancellable);

/// <summary>
/// System File Checker and DISM component-store checks. All need administrator rights. Results are read in a
/// language-independent way: DISM runs with /English, and SFC's verdict comes from the [SR] lines it writes to CBS.log
/// (always English), falling back to the console text only when the log can't be read.
/// </summary>
public static class SystemFileCheck
{
    public static readonly IReadOnlyList<FileCheckInfo> All =
    [
        new(FileCheckKind.DismCheckHealth, "DISM CheckHealth", "Reads whether Windows has already recorded component store damage. Read-only.", "a few seconds", false, true),
        new(FileCheckKind.DismScanHealth, "DISM ScanHealth", "Scans the component store (the copies Windows repairs itself from) for damage. Read-only.", "5–15 minutes", false, true),
        new(FileCheckKind.SfcVerifyOnly, "SFC verify only", "Checks every protected Windows system file against its known-good copy. Read-only (sfc /verifyonly).", "5–15 minutes", false, true),
        new(FileCheckKind.SfcScanNow, "SFC repair (sfc /scannow)", "Checks protected system files and replaces damaged ones with Windows' own good copies.", "5–20 minutes", true, false),
        new(FileCheckKind.DismRestoreHealth, "DISM RestoreHealth", "Repairs the component store, downloading good copies from Windows Update. Run this first if SFC can't repair files.", "10–30 minutes, needs internet", true, false),
    ];

    public static FileCheckInfo Info(FileCheckKind k) => All.First(a => a.Kind == k);

    private static string CbsLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Logs\CBS\CBS.log");

    public static async Task<FileCheckOutcome> RunAsync(FileCheckKind kind, IProgress<(double? percent, string text)>? progress, CancellationToken ct = default)
    {
        var info = Info(kind);
        var started = DateTime.Now;
        if (!Admin.IsElevated)
            return new(info.Title, HealthStatus.Unknown, "Needs administrator rights. Restart Money Tweaks as administrator to run it.", [], DateTime.Now, 0, 740);
        var sw = Stopwatch.StartNew();
        bool sfc = kind is FileCheckKind.SfcVerifyOnly or FileCheckKind.SfcScanNow;
        string exe = Path.Combine(ProcessRunner.System32, sfc ? "sfc.exe" : "dism.exe");
        string args = kind switch
        {
            FileCheckKind.SfcVerifyOnly => "/verifyonly",
            FileCheckKind.SfcScanNow => "/scannow",
            FileCheckKind.DismCheckHealth => "/English /Online /Cleanup-Image /CheckHealth",
            FileCheckKind.DismScanHealth => "/English /Online /Cleanup-Image /ScanHealth",
            _ => "/English /Online /Cleanup-Image /RestoreHealth",
        };
        long cbsStart = 0;
        if (sfc) try { cbsStart = new FileInfo(CbsLog).Length; } catch { cbsStart = -1; }
        Log.Info($"{info.Title} started ({exe} {args})");
        var pct = new Regex(@"(\d{1,3}(?:[.,]\d)?)\s?%");
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunStreamingAsync(exe, args, text =>
            {
                var ms = pct.Matches(text);
                double? p = ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Min(100, v) : null;
                var line = text.Split('\r', '\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
                progress?.Report((p, line));
            }, ct, killOnCancel: info.Cancellable);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"{info.Title} cancelled");
            return new(info.Title, HealthStatus.Unknown, "Cancelled before it finished; nothing was changed.", [], DateTime.Now, sw.Elapsed.TotalMinutes, -1);
        }
        string? cbs = null;
        if (sfc) cbs = ReadCbsSince(cbsStart);
        var outcome = sfc ? ParseSfc(kind, r.ExitCode, r.StdOut, cbs) : ParseDism(kind, r.ExitCode, r.StdOut);
        outcome = outcome with { Finished = DateTime.Now, Minutes = sw.Elapsed.TotalMinutes };
        Log.Info($"{info.Title} finished: {outcome.Status} · {outcome.Summary} (exit {r.ExitCode}, {sw.Elapsed.TotalMinutes:0.0} min)");
        return outcome;
    }

    private static string? ReadCbsSince(long start)
    {
        try
        {
            using var fs = new FileStream(CbsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The log is rolled over when it gets large; then the whole new file belongs to this run.
            fs.Seek(start >= 0 && start <= fs.Length ? start : 0, SeekOrigin.Begin);
            using var rd = new StreamReader(fs);
            return rd.ReadToEnd();
        }
        catch (Exception ex) { Log.Warn("CBS.log unreadable: " + ex.Message); return null; }
    }

    private static List<string> Tail(string output, int lines = 12) =>
        output.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('[')).TakeLast(lines).ToList();

    public static FileCheckOutcome ParseSfc(FileCheckKind kind, int exitCode, string output, string? cbs)
    {
        var title = Info(kind).Title;
        bool repair = kind == FileCheckKind.SfcScanNow;
        var details = new List<string> { $"Exit code: {exitCode}" };
        FileCheckOutcome O(HealthStatus s, string sum) => new(title, s, sum, details, DateTime.Now, 0, exitCode);
        if (cbs is not null)
        {
            var sr = cbs.Split('\n').Where(l => l.Contains("[SR]", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
            int unrepaired = sr.Count(l => l.Contains("Cannot repair member file", StringComparison.OrdinalIgnoreCase));
            int repaired = sr.Count(l => l.Contains("Repairing corrupted file", StringComparison.OrdinalIgnoreCase) || l.Contains("Repaired file", StringComparison.OrdinalIgnoreCase));
            int corrupt = cbs.Split('\n').Count(l => l.Contains("do not match actual file", StringComparison.OrdinalIgnoreCase)) +
                          sr.Count(l => l.Contains("Could not reproject corrupted file", StringComparison.OrdinalIgnoreCase));
            bool verified = sr.Any(l => l.Contains("Verify complete", StringComparison.OrdinalIgnoreCase));
            details.Add($"CBS.log [SR] lines from this run: {sr.Count} (verify complete: {(verified ? "yes" : "no")}, corrupt: {corrupt}, repaired: {repaired}, not repairable: {unrepaired})");
            details.AddRange(sr.Where(l => l.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || l.Contains("repair", StringComparison.OrdinalIgnoreCase)).Take(25));
            if (sr.Count > 0)
            {
                if (unrepaired > 0) return O(HealthStatus.Problem, $"Found damaged system files and couldn't repair {unrepaired} of them. Run DISM RestoreHealth, restart, then run the SFC repair again.");
                if (repair && repaired > 0) return O(HealthStatus.Passed, $"Found and repaired {repaired} damaged system file(s). Restart Windows to finish.");
                if (!repair && corrupt > 0) return O(HealthStatus.Problem, $"Found {corrupt} damaged system file(s). Run the SFC repair (sfc /scannow) to fix them.");
                if (verified) return O(HealthStatus.Passed, "No damaged system files found (no integrity violations).");
            }
        }
        else details.Add("CBS.log couldn't be read; result taken from SFC's own output.");
        details.AddRange(Tail(output));
        var t = output.ToLowerInvariant();
        if (t.Contains("did not find any integrity violations")) return O(HealthStatus.Passed, "No damaged system files found (no integrity violations).");
        if (t.Contains("successfully repaired")) return O(HealthStatus.Passed, "Found and repaired damaged system files. Restart Windows to finish.");
        if (t.Contains("unable to fix")) return O(HealthStatus.Problem, "Found damaged system files and couldn't repair some. Run DISM RestoreHealth, restart, then the SFC repair again.");
        if (t.Contains("found integrity violations")) return O(HealthStatus.Problem, "Found damaged system files. Run the SFC repair (sfc /scannow) to fix them.");
        if (t.Contains("repair pending")) return O(HealthStatus.Unknown, "A system repair is waiting for a restart. Restart Windows, then run this again.");
        if (t.Contains("must be an administrator")) return O(HealthStatus.Unknown, "Needs administrator rights.");
        return O(HealthStatus.Unknown, $"SFC finished (exit code {exitCode}) but its result couldn't be read. See the details, or %WINDIR%\\Logs\\CBS\\CBS.log.");
    }

    public static string? DismErrorText(int code) => unchecked((uint)code) switch
    {
        0x800F081F => "repair source files couldn't be found",
        0x800F0906 => "repair files couldn't be downloaded (check the internet connection, or Windows Update may be blocked)",
        0x800F0907 => "a Group Policy setting blocks downloading repair files",
        0x800F0950 => "the repair source isn't available",
        0x800F082F => "a pending update needs a restart first",
        0x800706BE or 1726 => "the servicing service stopped unexpectedly",
        740 => "administrator rights are needed",
        87 => "invalid option for this Windows version",
        112 => "not enough disk space",
        0x80070005 => "access denied",
        _ => null,
    };

    public static FileCheckOutcome ParseDism(FileCheckKind kind, int exitCode, string output)
    {
        var title = Info(kind).Title;
        var details = new List<string> { $"Exit code: {exitCode} (0x{exitCode:X8})" };
        details.AddRange(Tail(output));
        FileCheckOutcome O(HealthStatus s, string sum) => new(title, s, sum, details, DateTime.Now, 0, exitCode);
        var t = output.ToLowerInvariant();
        if (exitCode != 0)
        {
            var why = DismErrorText(exitCode) ?? "see the details";
            return unchecked((uint)exitCode) is 0x800F081F
                ? O(HealthStatus.Problem, $"DISM couldn't repair the component store: {why}. Make sure Windows Update works, then try again.")
                : O(HealthStatus.Unknown, $"DISM stopped with error 0x{exitCode:X8}: {why}.");
        }
        if (t.Contains("cannot be repaired")) return O(HealthStatus.Problem, "The component store is damaged beyond what DISM can repair. An in-place upgrade (reinstalling Windows over itself, keeping files and apps) fixes this.");
        if (t.Contains("component store is repairable")) return O(HealthStatus.Problem, "The component store is damaged but repairable. Run DISM RestoreHealth.");
        if (t.Contains("no component store corruption detected")) return O(HealthStatus.Passed, "No component store damage detected.");
        if (t.Contains("restore operation completed successfully") || t.Contains("corruption was repaired"))
            return O(HealthStatus.Passed, "Component store repaired or verified healthy. If SFC found unrepairable files, run the SFC repair again now.");
        if (t.Contains("operation completed successfully")) return O(HealthStatus.Passed, "DISM finished without errors.");
        return O(HealthStatus.Unknown, "DISM exited normally but its result couldn't be read. See the details.");
    }

    /// <summary>Turns a stored outcome into a health result so it appears with the scan (Windows area).</summary>
    public static HealthResult AsResult(FileCheckOutcome o) =>
        new("filecheck-" + o.Tool.Replace(' ', '-').ToLowerInvariant(), HealthArea.Windows, o.Tool, o.Status,
            o.Status switch { HealthStatus.Problem => Severity.High, HealthStatus.Warning => Severity.Medium, _ => Severity.Info },
            o.Summary, o.Details, o.Finished)
        {
            RepairId = o.Status == HealthStatus.Problem ? (o.Tool.StartsWith("SFC", StringComparison.Ordinal) && !o.Summary.Contains("DISM") ? SafeRepairs.SfcScanNow : SafeRepairs.DismRestore) : null,
        };
}
