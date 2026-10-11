using System.Diagnostics;
using System.Text;

namespace MoneyTweaks.Core.Sys;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Combined => (StdOut + Environment.NewLine + StdErr).Trim();
}

public static class ProcessRunner
{
    public static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static ProcessResult Run(string file, string args, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, outTask.Result, $"Timed out after {timeoutMs / 1000}s");
        }
        p.WaitForExit();
        var r = new ProcessResult(p.ExitCode, outTask.Result, errTask.Result);
        Log.Debug($"exec {Path.GetFileName(file)} {args} -> {r.ExitCode}");
        return r;
    }

    public static ProcessResult Cmd(string command, int timeoutMs = 60_000) =>
        Run(Path.Combine(System32, "cmd.exe"), "/d /c " + command, timeoutMs);

    public static ProcessResult PowerShell(string script, int timeoutMs = 120_000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';" + script));
        return Run(Path.Combine(System32, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMs);
    }
}
