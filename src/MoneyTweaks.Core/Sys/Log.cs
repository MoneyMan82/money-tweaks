namespace MoneyTweaks.Core.Sys;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} [{Level.ToString().ToUpperInvariant()}] {Message}";
}

/// <summary>Thread-safe file + in-memory log. Files live in %LOCALAPPDATA%\MoneyTweaks\logs.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly List<LogEntry> Recent = [];
    public static event Action<LogEntry>? Written;

    public static IReadOnlyList<LogEntry> Snapshot() { lock (Gate) return Recent.ToList(); }
    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"moneytweaks-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m, Exception? ex = null) => Write(LogLevel.Error, ex is null ? m : $"{m}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        var e = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            Recent.Add(e);
            if (Recent.Count > 2000) Recent.RemoveRange(0, 500);
            try { File.AppendAllText(CurrentFile, e + Environment.NewLine); } catch { /* logging must never crash the app */ }
        }
        if (level != LogLevel.Debug) Written?.Invoke(e);
    }
}
