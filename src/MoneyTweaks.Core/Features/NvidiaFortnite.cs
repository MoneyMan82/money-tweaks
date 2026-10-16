using System.Text.Json;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>A selectable value. Null Value = "driver default" (Money Tweaks removes its override).</summary>
public sealed record NvChoice(string Label, uint? Value);

/// <summary>
/// A texture-filtering setting in the NVIDIA driver profile. DriverName must match what the driver itself reports for
/// the ID, or the setting is treated as unsupported. Only the listed choices can ever be written.
/// </summary>
public sealed record NvTextureSetting(string Key, uint Id, string DriverName, string Label, string Affects, string Explanation,
    IReadOnlyList<NvChoice> Choices, bool Writable = true);

public sealed record NvPlanItem(NvTextureSetting Setting, NvSettingState? Current, uint? Target, string? Refused)
{
    public bool Changes => Refused is null && (Target is { } t ? Current is not { InProfile: true } c || c.Current != t : Current is { InProfile: true, IsCurrentPredefined: false });
}

/// <summary>Values before Money Tweaks' first change, per setting ID.</summary>
public sealed class NvBackup
{
    public DateTime At { get; set; }
    public string ProfileName { get; set; } = "";
    public bool CreatedProfile { get; set; }
    public Dictionary<uint, NvBackupEntry> Settings { get; set; } = [];
}

public sealed record NvBackupEntry(bool InProfile, uint Value);

public sealed class NvFortniteState
{
    public string Preset { get; set; } = "Default";
    public Dictionary<string, uint?> Values { get; set; } = [];
    public NvBackup? Backup { get; set; }

    internal static string PathFor => Path.Combine(AppPaths.Root, "nvidia-fortnite.json");

    public static NvFortniteState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<NvFortniteState>(File.ReadAllText(PathFor)) ?? new(); }
        catch (Exception ex) { Log.Warn("nvidia-fortnite.json unreadable: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(PathFor, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Fortnite's NVIDIA driver profile: texture filtering and texture LOD bias, through NVAPI DRS. Before writing, every
/// setting ID is checked against the driver's own name for it and every value against both Money Tweaks' fixed list and
/// the values the driver reports. The original values are backed up on the first change; after saving, a fresh session
/// reads everything back and rolls back on any mismatch.
/// </summary>
public static class NvidiaFortnite
{
    public const string Exe = "FortniteClient-Win64-Shipping.exe";
    public const string OwnProfileName = "Fortnite (Money Tweaks)";
    public const uint QualityId = 0x00CE2691, AnisoSampleOptId = 0x00E73211, TrilinearOptId = 0x002ECAF2, NegLodBiasId = 0x0019BB68,
        DriverLodBiasId = 0x00638E8F, LodBiasDxId = 0x00738E8F, LodBiasOglId = 0x20403F79;
    /// <summary>LOD bias is stored in 1/8 steps (driver range −128…+128 = Direct3D's −16…+16). 0x20 would be +4.0.</summary>
    public const uint UndocumentedLodValue = 0x00000020;

    public static readonly NvTextureSetting Quality = new("quality", QualityId, "Texture filtering - Quality", "Texture filtering – Quality",
        "Texture filtering (sharpness at angles), small performance cost",
        "High performance lets the driver use the cheapest filtering it allows. Textures can look blurrier at angles and shimmer more. Geometry is unchanged.",
        [new("Driver default", null), new("High quality", 0xFFFFFFF6), new("Quality", 0x00000000), new("Performance", 0x0000000A), new("High performance", 0x00000014)]);
    public static readonly NvTextureSetting AnisoSampleOpt = new("aniso", AnisoSampleOptId, "Texture filtering - Anisotropic sample optimization", "Anisotropic sample optimization",
        "Texture filtering only",
        "Takes fewer samples when anisotropic filtering is used. Slightly blurrier ground textures at steep angles; no effect on models or skins' shapes.",
        [new("Driver default", null), new("Off", 0), new("On", 1)]);
    public static readonly NvTextureSetting TrilinearOpt = new("trilinear", TrilinearOptId, "Texture filtering - Trilinear optimization", "Trilinear optimization",
        "Texture filtering only",
        "Uses bilinear filtering where the driver thinks you won't notice. Can show faint lines between mip levels; textures only.",
        [new("Driver default", null), new("Off", 0), new("On", 1)]);
    public static readonly NvTextureSetting NegLodBias = new("neglod", NegLodBiasId, "Texture filtering - Negative LOD bias", "Negative LOD bias",
        "Texture sharpness only",
        "Clamp stops the game from forcing sharper-than-normal textures (a negative bias), which reduces shimmer. It doesn't limit the positive bias below.",
        [new("Driver default", null), new("Allow", 0), new("Clamp", 1)]);
    public static readonly NvTextureSetting LodBiasDx = new("lodbias", LodBiasDxId, "Texture filtering - LOD Bias", "Texture LOD bias (DirectX)",
        "Texture detail (the flatter 'potato' textures)",
        "A positive bias makes the GPU use smaller, lower-detail texture levels sooner, so surfaces and skins look flatter and blurrier. It does not change geometry: models, builds and terrain keep the same shapes (that comes from Fortnite's Meshes: Low). Works in DirectX 11 (Performance mode); DirectX 12 games may ignore driver LOD bias. Money Tweaks also turns 'Driver Controlled LOD Bias' off, otherwise the driver ignores this value.",
        [new("Driver default (+0.0000)", null), new("+0.3750", 3), new("+0.5000", 4)]);
    public static readonly NvTextureSetting DriverLodBias = new("drvlod", DriverLodBiasId, "Texture filtering - Driver Controlled LOD Bias", "Driver controlled LOD bias",
        "Enables the LOD bias above", "Set automatically: Off when a LOD bias is chosen, driver default otherwise.",
        [new("Driver default", null), new("Off", 0)]);
    public static readonly NvTextureSetting LodBiasOgl = new("lodogl", LodBiasOglId, "", "Texture LOD bias (OpenGL)",
        "Nothing in Fortnite", "OpenGL only. Fortnite renders with DirectX, so this has no effect on it. Kept at +0.0000 and never written.",
        [new("+0.0000 (not written)", null)], Writable: false);

    /// <summary>User-adjustable settings in display order (Driver Controlled LOD Bias is derived, OGL is display-only).</summary>
    public static IReadOnlyList<NvTextureSetting> Controls { get; } = [Quality, AnisoSampleOpt, TrilinearOpt, NegLodBias, LodBiasDx, LodBiasOgl];
    public static IReadOnlyList<NvTextureSetting> Managed { get; } = [Quality, AnisoSampleOpt, TrilinearOpt, NegLodBias, DriverLodBias, LodBiasDx];

    public static IReadOnlyList<string> Presets { get; } = ["Default", "Mild Potato", "Extreme Potato"];

    public static Dictionary<string, uint?> Preset(string name) => name switch
    {
        "Mild Potato" => new() { ["quality"] = 0x14, ["aniso"] = 1, ["trilinear"] = 1, ["neglod"] = 1, ["lodbias"] = 3 },
        "Extreme Potato" => new() { ["quality"] = 0x14, ["aniso"] = 1, ["trilinear"] = 1, ["neglod"] = 1, ["lodbias"] = 4 },
        _ => new() { ["quality"] = null, ["aniso"] = null, ["trilinear"] = null, ["neglod"] = null, ["lodbias"] = null },
    };

    /// <summary>Target value per managed setting ID for a selection (adds the derived Driver Controlled LOD Bias).</summary>
    public static Dictionary<uint, uint?> Targets(IReadOnlyDictionary<string, uint?> sel)
    {
        uint? V(string k) => sel.TryGetValue(k, out var v) ? v : null;
        return new()
        {
            [QualityId] = V("quality"), [AnisoSampleOptId] = V("aniso"), [TrilinearOptId] = V("trilinear"), [NegLodBiasId] = V("neglod"),
            [LodBiasDxId] = V("lodbias"), [DriverLodBiasId] = V("lodbias") is null ? null : 0,
        };
    }

    /// <summary>
    /// Why a value may not be written, or null if it may. Checks Money Tweaks' fixed list, the driver's name for the ID and
    /// the driver's own value list (for LOD bias the driver reports a min/max range).
    /// </summary>
    public static string? Refusal(NvTextureSetting s, uint value, string? driverName, IReadOnlyList<uint> driverValues)
    {
        if (!s.Writable) return $"{s.Label} is never written.";
        if (!s.Choices.Any(c => c.Value == value))
            return $"0x{value:X8} isn't one of the documented values for {s.Label}" + (s.Id == LodBiasDxId && value == UndocumentedLodValue ? " (0x00000020 = +4.0 LOD bias, known to make skins look flat/green)." : ".");
        if (!string.Equals(driverName, s.DriverName, StringComparison.Ordinal))
            return $"This driver doesn't report setting 0x{s.Id:X8} as '{s.DriverName}' (it reports '{driverName ?? "nothing"}').";
        if (driverValues.Count > 0)
        {
            if (s.Id == LodBiasDxId && driverValues.Count == 2)
            {
                int lo = unchecked((int)driverValues[0]), hi = unchecked((int)driverValues[1]), v = unchecked((int)value);
                if (v < Math.Min(lo, hi) || v > Math.Max(lo, hi)) return $"The driver's LOD bias range is {lo / 8.0:+0.###;-0.###} to {hi / 8.0:+0.###;-0.###}.";
            }
            else if (!driverValues.Contains(value)) return $"The driver doesn't list 0x{value:X8} for {s.Label}.";
        }
        return null;
    }

    public static string Describe(NvTextureSetting s, uint? value) =>
        value is null ? "driver default" : s.Choices.FirstOrDefault(c => c.Value == value)?.Label ?? $"0x{value:X8} (not a Money Tweaks value)";

    public sealed record Status(bool Available, string Message, string? ProfileName, bool OwnProfile, Dictionary<uint, NvSettingState?> Current, Dictionary<uint, (string? name, List<uint> values)> Driver);

    /// <summary>Read-only: driver availability, Fortnite's profile and the current value of every managed setting.</summary>
    public static Status Read(string exe = Exe)
    {
        var cur = new Dictionary<uint, NvSettingState?>();
        var drv = new Dictionary<uint, (string?, List<uint>)>();
        if (!NvApi.DriverPresent) return new(false, "No NVIDIA driver on this PC. These settings only exist in NVIDIA's driver.", null, false, cur, drv);
        try
        {
            foreach (var s in Managed.Append(LodBiasOgl)) drv[s.Id] = (NvApi.SettingName(s.Id), NvApi.SettingValues(s.Id).values);
            using var session = new NvApi.Session();
            var p = session.FindProfileForApp(exe);
            if (p == IntPtr.Zero)
                return new(true, $"No NVIDIA profile lists {exe} yet; Money Tweaks will create '{OwnProfileName}' when you apply.", null, false, cur, drv);
            foreach (var s in Managed) cur[s.Id] = session.Get(p, s.Id);
            var name = session.ProfileName(p);
            return new(true, $"NVIDIA profile '{name}' ({exe}).", name, name == OwnProfileName, cur, drv);
        }
        catch (Exception ex) when (ex is NvApiException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("NVIDIA profile read failed: " + ex.Message);
            return new(false, "Couldn't read the NVIDIA driver settings: " + ex.Message, null, false, cur, drv);
        }
    }

    public static List<NvPlanItem> Plan(IReadOnlyDictionary<string, uint?> selection, Status st)
    {
        var targets = Targets(selection);
        return Managed.Select(s =>
        {
            var t = targets[s.Id];
            st.Current.TryGetValue(s.Id, out var c);
            var (name, values) = st.Driver.TryGetValue(s.Id, out var d) ? d : (null, []);
            var refused = t is { } v ? Refusal(s, v, name, values) : null;
            return new NvPlanItem(s, c, t, refused);
        }).ToList();
    }

    /// <summary>Applies a selection. Needs administrator rights. Backs up first, verifies after, rolls back on mismatch.</summary>
    public static (bool ok, string message, List<string> log) Apply(IReadOnlyDictionary<string, uint?> selection, NvFortniteState state, string exe = Exe)
    {
        var log = new List<string>();
        var st = Read(exe);
        if (!st.Available) return (false, st.Message, log);
        var plan = Plan(selection, st);
        if (plan.FirstOrDefault(p => p.Refused is not null) is { } bad) return (false, "Nothing was changed. " + bad.Refused, log);
        if (!plan.Any(p => p.Changes)) return (true, "The NVIDIA profile already has these values; nothing changed.", log);

        bool created = false;
        using (var s = new NvApi.Session())
        {
            var prof = s.FindProfileForApp(exe);
            if (prof == IntPtr.Zero)
            {
                prof = s.CreateProfileWithApp(OwnProfileName, exe, "Fortnite");
                created = true;
                log.Add($"Created profile '{OwnProfileName}' for {exe}");
            }
            if (state.Backup is null)
            {
                state.Backup = new NvBackup
                {
                    At = DateTime.Now, ProfileName = created ? OwnProfileName : st.ProfileName ?? "", CreatedProfile = created,
                    Settings = Managed.ToDictionary(m => m.Id, m => s.Get(prof, m.Id) is { InProfile: true } g ? new NvBackupEntry(true, g.Current) : new NvBackupEntry(false, 0)),
                };
                state.Save(); // on disk before anything is written to the driver
                log.Add("Backed up the original values");
            }
            foreach (var p in plan.Where(p => p.Changes))
            {
                if (p.Target is { } v) s.Set(prof, p.Setting.Id, v); else s.Reset(prof, p.Setting.Id);
                log.Add($"{p.Setting.Label}: {Describe(p.Setting, p.Current is { InProfile: true } c ? c.Current : null)} → {Describe(p.Setting, p.Target)}");
            }
            s.Save();
        }
        var mismatch = Verify(Targets(selection), exe);
        if (mismatch.Count > 0)
        {
            log.AddRange(mismatch);
            var (rok, rmsg, rlog) = Restore(state, exe);
            log.AddRange(rlog);
            Log.Warn("NVIDIA profile change didn't verify; rolled back: " + string.Join(" | ", log));
            return (false, "The driver didn't keep the new values, so the originals were put back" + (rok ? "." : ": " + rmsg), log);
        }
        state.Values = new(selection);
        state.Save();
        Log.Info("NVIDIA Fortnite profile applied and verified: " + string.Join(" | ", log));
        return (true, "Applied to the NVIDIA driver profile and read back from the driver. Restart Fortnite for it to take effect.", log);
    }

    /// <summary>Reads the profile in a fresh session and lists values that differ from the targets.</summary>
    public static List<string> Verify(Dictionary<uint, uint?> targets, string exe = Exe)
    {
        var bad = new List<string>();
        using var s = new NvApi.Session();
        var p = s.FindProfileForApp(exe);
        if (p == IntPtr.Zero) { bad.Add("Profile not found after saving."); return bad; }
        foreach (var (id, t) in targets)
        {
            var g = s.Get(p, id);
            var ok = t is { } v ? g is { InProfile: true } && g.Current == v : g is not { InProfile: true } || g.IsCurrentPredefined;
            if (!ok) bad.Add($"0x{id:X8}: expected {(t is { } x ? "0x" + x.ToString("X8") : "default")}, driver has {(g is null ? "nothing" : $"0x{g.Current:X8} (location {g.Location})")}");
        }
        return bad;
    }

    /// <summary>Puts back the values from before Money Tweaks' first change (deletes the profile if Money Tweaks created it).</summary>
    public static (bool ok, string message, List<string> log) Restore(NvFortniteState state, string exe = Exe)
    {
        var log = new List<string>();
        if (state.Backup is not { } b) return (false, "There is no backup to restore.", log);
        using (var s = new NvApi.Session())
        {
            var p = s.FindProfileForApp(exe);
            if (p != IntPtr.Zero)
            {
                if (b.CreatedProfile && s.ProfileName(p) == OwnProfileName) { s.DeleteProfile(p); log.Add($"Deleted '{OwnProfileName}' (created by Money Tweaks)"); }
                else
                    foreach (var (id, e) in b.Settings)
                    {
                        if (e.InProfile) s.Set(p, id, e.Value); else s.Reset(p, id);
                        log.Add($"0x{id:X8} → {(e.InProfile ? "0x" + e.Value.ToString("X8") : "default")}");
                    }
                s.Save();
            }
        }
        if (!b.CreatedProfile)
        {
            var bad = Verify(b.Settings.ToDictionary(kv => kv.Key, kv => kv.Value.InProfile ? kv.Value.Value : (uint?)null), exe);
            if (bad.Count > 0) { log.AddRange(bad); return (false, "Restored, but the driver reports different values: " + string.Join("; ", bad), log); }
        }
        state.Backup = null;
        state.Values = Preset("Default");
        state.Preset = "Default";
        state.Save();
        Log.Info("NVIDIA Fortnite profile restored from backup: " + string.Join(" | ", log));
        return (true, "Original NVIDIA profile values restored and verified. Restart Fortnite for it to take effect.", log);
    }

    /// <summary>True if the backup holds a value Money Tweaks would never write (e.g. an earlier manual 0x00000020 LOD bias).</summary>
    public static List<string> UnusualBackupValues(NvBackup b) =>
        b.Settings.Where(kv => kv.Value.InProfile && Managed.FirstOrDefault(m => m.Id == kv.Key) is { } m && !m.Choices.Any(c => c.Value == kv.Value.Value))
            .Select(kv => $"{Managed.First(m => m.Id == kv.Key).Label} = 0x{kv.Value.Value:X8}").ToList();
}
