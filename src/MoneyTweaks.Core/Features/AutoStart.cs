using Microsoft.Win32;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>
/// "Start with Windows" through the per-user Run key (no admin, no service, no scheduled task). Windows starts it
/// after sign-in with --background, so it opens minimized and skips the heavy dashboard work.
/// </summary>
public static class AutoStart
{
    public const string ValueName = "MoneyTweaks";
    public const string BackgroundArg = "--background";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Tests can point this at a scratch key so the real Run key is never touched.</summary>
    internal static string KeyPath { get; set; } = RunKey;

    public static string CommandFor(string exe) => $"\"{exe}\" {BackgroundArg}";

    public static string? Current()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled => Current() is not null;

    public static void Enable(string exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        k.SetValue(ValueName, CommandFor(exe), RegistryValueKind.String);
        Log.Info($"Start with Windows enabled: {CommandFor(exe)}");
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("Start with Windows disabled");
    }

    /// <summary>If enabled but pointing at another location (moved portable copy, reinstall), point it at this exe.</summary>
    public static void EnsurePath(string? exe)
    {
        if (exe is null || Current() is not { } cur) return;
        if (!string.Equals(cur, CommandFor(exe), StringComparison.OrdinalIgnoreCase)) Enable(exe);
    }
}
