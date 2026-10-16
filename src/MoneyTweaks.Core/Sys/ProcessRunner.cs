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

    /// <summary>
    /// Runs a long tool (SFC, DISM) and reports its output as it arrives. Output encoding is detected (SFC writes UTF-16
    /// when redirected, DISM writes 8-bit text). Cancelling kills the process only when <paramref name="killOnCancel"/> is set.
    /// </summary>
    public static async Task<ProcessResult> RunStreamingAsync(string file, string args, Action<string>? onText, CancellationToken ct = default, bool killOnCancel = true)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var all = new StringBuilder();
        async Task Pump(Stream s)
        {
            var buf = new byte[4096];
            Decoder? dec = null;
            int n;
            while ((n = await s.ReadAsync(buf, CancellationToken.None)) > 0)
            {
                if (dec is null)
                {
                    int zeros = 0;
                    for (int i = 1; i < n; i += 2) if (buf[i] == 0) zeros++;
                    dec = (n >= 4 && zeros >= n / 4 ? Encoding.Unicode : Encoding.UTF8).GetDecoder();
                }
                var chars = new char[dec.GetCharCount(buf, 0, n)];
                dec.GetChars(buf, 0, n, chars, 0);
                var text = new string(chars).Replace("\0", "");
                lock (all) all.Append(text);
                onText?.Invoke(text);
            }
        }
        var pumps = Task.WhenAll(Pump(p.StandardOutput.BaseStream), Pump(p.StandardError.BaseStream));
        try { await p.WaitForExitAsync(killOnCancel ? ct : CancellationToken.None); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        await pumps;
        Log.Debug($"exec {Path.GetFileName(file)} {args} -> {p.ExitCode}");
        lock (all) return new ProcessResult(p.ExitCode, all.ToString(), "");
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
