using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

public sealed class CrosshairSettings
{
    public string Style { get; set; } = "Cross";       // Cross, Dot, Circle, T, CrossDot
    public string Color { get; set; } = "#FF2BE38B";
    public double Size { get; set; } = 10;
    public double Thickness { get; set; } = 2;
    public double Gap { get; set; } = 4;
    public double Opacity { get; set; } = 1;
    public bool Outline { get; set; } = true;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public int MonitorIndex { get; set; }
}

public sealed class AppSettings
{
    public bool CreateRestorePointBeforeBatch { get; set; } = true;
    public bool ConfirmCautionTweaks { get; set; } = true;
    public CrosshairSettings Crosshair { get; set; } = new();
    public List<string> BoosterCloseList { get; set; } = [];
    public string BoosterGameProcess { get; set; } = "";
    public bool BoosterPurgeStandby { get; set; } = true;
    public string PingHost { get; set; } = "1.1.1.1";
    public double TimerResolutionMs { get; set; } = 0.5;
    public bool ShowWelcome { get; set; } = true;
    /// <summary>Check GitHub Releases for a newer version at startup.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>null = follow Windows (animations off or weak GPU → reduce); true/false = user override.</summary>
    public bool? ReduceMotion { get; set; }
    /// <summary>Set just before an in-app update installs; checked on the next start to confirm the update applied.</summary>
    public string? PendingUpdateTag { get; set; }
    public string? PendingUpdateVersion { get; set; }
    /// <summary>A release that failed to install or was mislabeled; not offered automatically again.</summary>
    public string? SkippedUpdateTag { get; set; }
    public Dictionary<string, PotatoOptions> Potato { get; set; } = [];
    public FortnitePotatoSettings FortnitePotato { get; set; } = FortnitePotatoSettings.FromPreset(FnPreset.Potato);
    /// <summary>User's own Fortnite captures for the Potato before/after comparison.</summary>
    public string? PotatoBeforeShot { get; set; }
    public string? PotatoAfterShot { get; set; }
    /// <summary>Live performance overlay: layout, hotkey and per-game profiles.</summary>
    public OverlaySettings Overlay { get; set; } = OverlaySettings.WithDefaults();
    public Pocket.PocketSettings Pocket { get; set; } = new();
    /// <summary>Sidebar shows icons only.</summary>
    public bool SidebarCompact { get; set; }
    /// <summary>Sidebar groups the user collapsed.</summary>
    public List<string> CollapsedNavGroups { get; set; } = [];
    /// <summary>Tweak-list groups the user collapsed, as "page|group" (e.g. "General|Power", "all|Networking").</summary>
    public List<string> CollapsedTweakGroups { get; set; } = [];
    /// <summary>Start MSI Afterburner without asking when its controls are used ("Don't ask again"). Never at app startup.</summary>
    public bool AutoStartAfterburner { get; set; }
    /// <summary>Pages pinned to the top of the sidebar and the dashboard shortcuts.</summary>
    public List<string> FavouritePages { get; set; } = [];
    /// <summary>Most recently opened pages, newest first.</summary>
    public List<string> RecentPages { get; set; } = [];
    /// <summary>Community Hub server address (https). Empty until the official server is online; can point at a self-hosted one.</summary>
    public string CommunityServer { get; set; } = "";
    /// <summary>Community guidelines version the user accepted before publishing (0 = not yet).</summary>
    public int CommunityGuidelinesAccepted { get; set; }
    /// <summary>Dashboard cards the user hid.</summary>
    public List<string> DashboardHidden { get; set; } = [];
    /// <summary>Legacy UI: the original (1.0–1.3) green look with flat cards and no ambient glow. Applied at startup.</summary>
    public bool LegacyUi { get; set; }
    /// <summary>Tweak Truth Index: the user allowed sharing sanitized measured results with the Community Hub. Off by default.</summary>
    public bool ShareEvidence { get; set; }
    /// <summary>Hardware Monitor: refresh rate, history range, pinned metrics, logging, compact window and warnings.</summary>
    public Monitoring.MonitorSettings Monitor { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch (Exception ex) { Log.Warn("Settings file was unreadable, using defaults: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { AtomicFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }
}
