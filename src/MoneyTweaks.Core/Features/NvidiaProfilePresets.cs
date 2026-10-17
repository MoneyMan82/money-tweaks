using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MoneyTweaks.Core.Display;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Features;

/// <summary>
/// One field of a driver-profile preset. DriverName is the name NVIDIA's driver itself reports for the setting; the ID is
/// resolved from that name through NVAPI and must equal ExpectedId (the ID NVIDIA Profile Inspector shows), so nothing is
/// written under an invented or mismatched ID. Value is written exactly as given (hex shown everywhere).
/// </summary>
public sealed record NvPresetItem(string Field, string DriverName, uint ExpectedId, uint Value, string ValueLabel, bool Mandatory, string Note);

public sealed record NvPreset(string Name, string Description, IReadOnlyList<NvPresetItem> Items);

public enum NvItemStatus { Applied, AlreadySet, Unsupported, Rejected, NotApplied }

public sealed record NvItemResult(NvPresetItem Item, uint? ResolvedId, NvItemStatus Status, string Detail, uint? Before, uint? After)
{
    /// <summary>What NVIDIA Profile Inspector's own metadata says about this field (when Inspector is available).</summary>
    public string? Inspector { get; init; }
}

public enum RenderSupport { Supported, LikelyIgnored, Unknown }

public sealed record NvReport(bool Ok, string Summary, List<NvItemResult> Items, List<string> UnrelatedChanges,
    RenderSupport Render, string RenderText, string Display, string? BackupNip, string? AppliedNip, string Method, List<string> Log);

/// <summary>Every setting the Fortnite profile had, saved before a change. Also written as an NVIDIA Profile Inspector .nip.</summary>
public sealed class NvSnapshot
{
    public DateTime At { get; set; }
    public string ProfileName { get; set; } = "";
    public string Exe { get; set; } = "";
    public bool CreatedProfile { get; set; }
    public List<NvSnapSetting> Settings { get; set; } = [];
}

public sealed record NvSnapSetting(uint Id, string Name, uint Type, bool Predefined, uint Value, string? Text);

/// <summary>Where the newest backup is and which NVIDIA Profile Inspector the user pointed Money Tweaks at (never downloaded).</summary>
public sealed class NvProfileState
{
    public string? LastBackupJson { get; set; }
    public string? InspectorPath { get; set; }
    /// <summary>SHA-256 of the Inspector exe when the user chose it; a different file at that path is never started.</summary>
    public string? InspectorSha256 { get; set; }
    public string? LastPreset { get; set; }
    internal static string PathFor => Path.Combine(AppPaths.Root, "nvidia-profile.json");

    public static NvProfileState Load()
    {
        try { if (File.Exists(PathFor)) return JsonSerializer.Deserialize<NvProfileState>(File.ReadAllText(PathFor)) ?? new(); }
        catch (Exception ex)
        {
            // Keep the damaged file (it may hold the only backup of original values) before the next Save replaces it.
            Log.Warn("nvidia-profile.json unreadable (kept as .bad): " + ex.Message);
            try { File.Copy(PathFor, PathFor + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: false); } catch { }
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(PathFor, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Applies driver-profile presets to Fortnite's NVIDIA profile through a .nip profile import: back up the whole profile
/// (JSON + .nip), write the preset as a .nip, import it (NVIDIA Profile Inspector if the user chose one, otherwise Money
/// Tweaks' NVAPI importer, the same driver API Inspector uses), then read everything back in a fresh session. Unrelated
/// settings must be unchanged and every mandatory field must read back exactly, or the backup is imported again.
/// </summary>
public static class NvidiaProfilePresets
{
    public const string Exe = NvidiaFortnite.Exe;
    public static string BackupDir => Path.Combine(AppPaths.Root, "nvidia-backups");

    public const string LegacyName = "Legacy Potato — LOD Bias 0x20";

    public static readonly NvPreset Legacy = new(LegacyName,
        "From the NVIDIA Inspector low-spec guide (Steam guide 1884111121, written for Destiny 2): extreme positive LOD bias 0x00000020 with transparency supersampling and driver-controlled LOD bias on. Makes textures, including skins, very flat and blurry. Geometry is unchanged.",
        [
            new("Antialiasing – Transparency Supersampling", "Antialiasing - Transparency Supersampling", 0x10D48A85, 0x00000008, "AA_MODE_REPLAY_MODE_ALL", false,
                "0x00000008 is NVIDIA's AA_MODE_REPLAY_MODE_ALL constant (the name Inspector shows for it). Inspector's own list only names combined modes, e.g. 0x18 = 2x sparse grid."),
            new("Texture Filtering – LOD Bias (DX)", "Texture filtering - LOD Bias", 0x00738E8F, 0x00000020, "0x00000020", true,
                "Mandatory. Written exactly as 0x00000020 (1/8 steps, so +4.0). Outside Inspector's named −3.0…+3.0 list but inside the driver's range; the guide enters it as hex. Higher = flatter, blurrier textures. DirectX 9–11 only."),
            new("Texture Filtering – Driver Controlled LOD Bias", "Texture filtering - Driver Controlled LOD Bias", 0x00638E8F, 0x00000001, "On", false,
                "On, as the original guide specifies (\"LOD Bias won't work without this on\"). Newer Inspector builds call it 'LOD Bias Auto-Adjust (for SGSSAA)'."),
            new("Texture Filtering – Quality", "Texture filtering - Quality", 0x00CE2691, 0x00000014, "High performance", false, "Cheapest texture filtering the driver allows."),
            new("Texture Filtering – Anisotropic Filter Optimization", "Texture filtering - Anisotropic filter optimization", 0x0084CD70, 0x00000001, "On", false, "Bilinear instead of trilinear inside anisotropic filtering."),
            new("Texture Filtering – Anisotropic Sample Optimization", "Texture filtering - Anisotropic sample optimization", 0x00E73211, 0x00000001, "On", false, "Fewer anisotropic samples."),
            new("Anisotropic Filtering – Setting", "Anisotropic filtering setting", 0x101E61A9, 0x00000000, "Off [Point]", false, "Point sampling instead of anisotropic filtering."),
            new("Anisotropic Filtering – Mode", "Anisotropic filtering mode", 0x10D2BB16, 0x00000001, "User-defined / Off", false,
                "Needed for the setting above to override the game (with Application-controlled the driver ignores it). Not in the guide's list; added so Off [Point] takes effect."),
            new("Threaded Optimization", "Threaded optimization", 0x20C1221E, 0x00000001, "On", false, "OpenGL only; no effect on Fortnite's DirectX renderer."),
            new("Power Management Mode", "Power management mode", 0x1057EB71, 0x00000001, "Prefer maximum performance", false, "Fortnite profile only; the global profile is not touched."),
        ]);

    /// <summary>Earlier Money Tweaks presets, kept for comparison (driver-controlled LOD bias off, small positive bias).</summary>
    public static readonly NvPreset MildCompare = Compare("Mild Potato (comparison)", 3, "+0.3750");
    public static readonly NvPreset ExtremeCompare = Compare("Extreme Potato (comparison)", 4, "+0.5000");

    private static NvPreset Compare(string name, uint lod, string lodLabel) => new(name,
        "Money Tweaks' earlier preset: high-performance filtering, optimizations on, negative LOD bias clamped, a small positive LOD bias with driver-controlled LOD bias off.",
        [
            new("Texture Filtering – Quality", "Texture filtering - Quality", 0x00CE2691, 0x00000014, "High performance", false, ""),
            new("Texture Filtering – Anisotropic Sample Optimization", "Texture filtering - Anisotropic sample optimization", 0x00E73211, 1, "On", false, ""),
            new("Texture Filtering – Trilinear Optimization", "Texture filtering - Trilinear optimization", 0x002ECAF2, 1, "On", false, ""),
            new("Texture Filtering – Negative LOD Bias", "Texture filtering - Negative LOD bias", 0x0019BB68, 1, "Clamp", false, ""),
            new("Texture Filtering – Driver Controlled LOD Bias", "Texture filtering - Driver Controlled LOD Bias", 0x00638E8F, 0, "Off", false, ""),
            new("Texture Filtering – LOD Bias (DX)", "Texture filtering - LOD Bias", 0x00738E8F, lod, lodLabel, true, ""),
        ]);

    public static IReadOnlyList<NvPreset> All { get; } = [Legacy, MildCompare, ExtremeCompare];

    /// <summary>Every setting any preset can touch; Reset returns these (and only these) to NVIDIA's values.</summary>
    public static IReadOnlyList<uint> ManagedIds { get; } = All.SelectMany(p => p.Items).Select(i => i.ExpectedId).Distinct().ToList();

    public static string Hex(uint v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);

    // ---- resolution against the driver's own metadata ------------------------------------------------------------------

    /// <summary>Resolves the field through the driver: name → ID must match, and the value must be one the driver lists.</summary>
    public static (uint? id, NvItemStatus? problem, string detail) Resolve(NvPresetItem item, Func<string, uint?> idFromName, Func<uint, string?> nameFromId, Func<uint, IReadOnlyList<uint>> values)
    {
        var id = idFromName(item.DriverName);
        if (id is null) return (null, NvItemStatus.Unsupported, $"This driver doesn't know a setting named '{item.DriverName}'.");
        if (id != item.ExpectedId) return (id, NvItemStatus.Rejected, $"The driver maps '{item.DriverName}' to {Hex(id.Value)}, not {Hex(item.ExpectedId)}; not written.");
        if (!string.Equals(nameFromId(id.Value), item.DriverName, StringComparison.Ordinal)) return (id, NvItemStatus.Rejected, $"The driver's name for {Hex(id.Value)} doesn't match.");
        var v = values(id.Value);
        if (v.Count == 0) return (id, NvItemStatus.Unsupported, "The driver lists no values for this setting.");
        if (item.ExpectedId == NvidiaFortnite.LodBiasDxId && v.Count == 2)
        {
            int lo = unchecked((int)Math.Min(unchecked((int)v[0]), unchecked((int)v[1]))), hi = Math.Max(unchecked((int)v[0]), unchecked((int)v[1])), x = unchecked((int)item.Value);
            return x >= lo && x <= hi ? (id, null, $"Driver range {Hex(unchecked((uint)lo))}…{Hex(unchecked((uint)hi))}") : (id, NvItemStatus.Rejected, $"{Hex(item.Value)} is outside the driver's range.");
        }
        return v.Contains(item.Value) ? (id, null, "Value listed by the driver") : (id, NvItemStatus.Unsupported, $"The driver doesn't list {Hex(item.Value)} for this setting.");
    }

    private static (uint? id, NvItemStatus? problem, string detail) ResolveLive(NvPresetItem i) =>
        Resolve(i, NvApi.SettingIdFromName, NvApi.SettingName, id => NvApi.SettingValues(id).values);

    // ---- .nip (NVIDIA Profile Inspector) format -------------------------------------------------------------------------

    /// <summary>Profile export in NVIDIA Profile Inspector's .nip format (DWORD values are decimal in this format: 32 = 0x00000020).</summary>
    public static string BuildNip(string profileName, string exe, IEnumerable<NvSnapSetting> settings)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance", xsd = "http://www.w3.org/2001/XMLSchema";
        var doc = new XDocument(new XDeclaration("1.0", "utf-16", null),
            new XElement("ArrayOfProfile", new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XAttribute(XNamespace.Xmlns + "xsd", xsd),
                new XElement("Profile",
                    new XElement("ProfileName", profileName),
                    new XElement("Executeables", new XElement("string", exe.ToLowerInvariant())),
                    new XElement("Settings", settings.Where(s => s.Type is 0 or 2 or 3).Select(s => new XElement("ProfileSetting",
                        new XElement("SettingNameInfo", s.Name),
                        new XElement("SettingID", s.Id.ToString(CultureInfo.InvariantCulture)),
                        new XElement("SettingValue", s.Type == 0 ? s.Value.ToString(CultureInfo.InvariantCulture) : s.Text ?? ""),
                        new XElement("ValueType", s.Type == 0 ? "Dword" : "String")))))));
        var sb = new StringBuilder();
        using (var w = new Utf16StringWriter(sb)) doc.Save(w);
        return sb.ToString();
    }

    private sealed class Utf16StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.Unicode;
    }

    public static (string profile, List<string> exes, List<NvSnapSetting> settings) ParseNip(string xml)
    {
        var p = XDocument.Parse(xml).Root?.Element("Profile") ?? throw new FormatException("No <Profile> in the .nip file.");
        var settings = p.Element("Settings")?.Elements("ProfileSetting").Select(s =>
        {
            var type = (string?)s.Element("ValueType") ?? "Dword";
            var raw = (string?)s.Element("SettingValue") ?? "";
            var id = uint.Parse((string?)s.Element("SettingID") ?? throw new FormatException("SettingID missing"), CultureInfo.InvariantCulture);
            return type.Equals("Dword", StringComparison.OrdinalIgnoreCase)
                ? new NvSnapSetting(id, (string?)s.Element("SettingNameInfo") ?? "", 0, false, ParseDword(raw), null)
                : new NvSnapSetting(id, (string?)s.Element("SettingNameInfo") ?? "", 3, false, 0, raw);
        }).ToList() ?? [];
        return ((string?)p.Element("ProfileName") ?? "", p.Element("Executeables")?.Elements("string").Select(e => e.Value).ToList() ?? [], settings);
    }

    private static uint ParseDword(string raw) => raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : unchecked((uint)long.Parse(raw, CultureInfo.InvariantCulture));

    // ---- profile access ------------------------------------------------------------------------------------------------

    /// <summary>Fortnite's own profile (the one listing the exe). Never the global or base profile.</summary>
    private static IntPtr Target(NvApi.Session s, string exe, bool create, List<string> log, out bool created)
    {
        created = false;
        var p = s.FindProfileForApp(exe);
        if (p == IntPtr.Zero)
        {
            if (!create) return IntPtr.Zero;
            p = s.CreateProfileWithApp(NvidiaFortnite.OwnProfileName, exe, "Fortnite");
            created = true;
            log.Add($"No NVIDIA profile listed {exe}; created '{NvidiaFortnite.OwnProfileName}' for it.");
        }
        if (s.IsGlobalOrBase(p)) throw new InvalidOperationException("The profile found for Fortnite is the global/base profile; refusing to change it.");
        return p;
    }

    private static List<NvSnapSetting> Read(NvApi.Session s, IntPtr p) =>
        s.EnumSettings(p).Where(x => x.Location == 0).Select(x => new NvSnapSetting(x.Id, x.Name, x.Type, x.IsCurrentPredefined, x.Value, x.Text)).ToList();

    private static bool Same(NvSnapSetting a, NvSnapSetting b) => a.Type == b.Type && (a.Type == 0 ? a.Value == b.Value : a.Text == b.Text);

    /// <summary>Saves the full profile as JSON (for exact restore) and .nip (for NVIDIA Profile Inspector).</summary>
    public static (string json, string nip) SaveBackup(NvSnapshot snap)
    {
        Directory.CreateDirectory(BackupDir);
        var stamp = snap.At.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var json = Path.Combine(BackupDir, $"fortnite-profile-{stamp}.json");
        var nip = Path.Combine(BackupDir, $"fortnite-profile-{stamp}.nip");
        AtomicFile.WriteAllText(json, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(nip, BuildNip(snap.ProfileName, snap.Exe, snap.Settings.Where(x => !x.Predefined)), Encoding.Unicode);
        return (json, nip);
    }

    /// <summary>
    /// Imports a .nip into the Fortnite profile through NVAPI. Merge sets the listed settings; Replace also returns settings
    /// not in the file to NVIDIA's values (used to restore a backup). Only DWORD and string settings are imported.
    /// </summary>
    public static List<string> ImportNip(string nipPath, string exe, bool replace, IReadOnlyCollection<NvSnapSetting>? exact = null)
    {
        var (_, _, settings) = ParseNip(File.ReadAllText(nipPath));
        var log = new List<string>();
        using var s = new NvApi.Session();
        var p = Target(s, exe, create: true, log, out _);
        var want = (exact ?? settings).ToList();
        var now = Read(s, p);
        if (replace)
            foreach (var cur in now.Where(c => !want.Any(w => w.Id == c.Id)))
            {
                s.Reset(p, cur.Id);
                log.Add($"{Hex(cur.Id)} {cur.Name}: removed (not in backup)");
            }
        foreach (var w in want)
        {
            var cur = now.FirstOrDefault(c => c.Id == w.Id);
            if (cur is not null && Same(cur, w) && cur.Predefined == w.Predefined) continue;
            if (w.Predefined) { s.Reset(p, w.Id); log.Add($"{Hex(w.Id)} {w.Name}: NVIDIA's predefined value"); continue; }
            if (w.Type == 1) { log.Add($"{Hex(w.Id)} {w.Name}: binary setting not imported"); continue; }
            if (w.Type == 0) s.Set(p, w.Id, w.Value); else s.SetString(p, w.Id, w.Text ?? "");
            log.Add($"{Hex(w.Id)} {w.Name}: {(cur is null ? "not set" : cur.Type == 0 ? Hex(cur.Value) : cur.Text)} → {(w.Type == 0 ? Hex(w.Value) : w.Text)}");
        }
        s.Save();
        return log;
    }

    // ---- checks around the change --------------------------------------------------------------------------------------

    /// <summary>Whether Fortnite's current renderer honours a driver LOD-bias override (read from GameUserSettings.ini).</summary>
    public static (RenderSupport support, string text) RenderingMode(string? iniText)
    {
        if (iniText is null) return (RenderSupport.Unknown, "Fortnite's settings file wasn't found, so the rendering mode is unknown.");
        string? V(string key) => Regex.Match(iniText, $@"^\s*{key}\s*=\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        var rhi = V("PreferredRHI")?.ToLowerInvariant();
        var fl = V("PreferredFeatureLevel")?.ToLowerInvariant();
        return rhi switch
        {
            "dx11" when fl == "es31" => (RenderSupport.Supported, "Performance mode (DirectX 11 renderer, PreferredRHI=dx11, feature level es31): NVIDIA's driver texture overrides apply to DirectX 11."),
            "dx11" => (RenderSupport.Supported, $"DirectX 11 (PreferredRHI=dx11{(fl is null ? "" : ", " + fl)}): NVIDIA's driver texture overrides apply to DirectX 11."),
            "dx12" => (RenderSupport.LikelyIgnored, "DirectX 12 (PreferredRHI=dx12): NVIDIA's LOD-bias override generally doesn't apply to DirectX 12, so the profile can be correct with no visible change. Switch Fortnite to Performance mode or DirectX 11."),
            null => (RenderSupport.Unknown, "PreferredRHI isn't in Fortnite's settings file; launch Fortnite once and check again."),
            _ => (RenderSupport.Unknown, $"Unrecognised renderer PreferredRHI={rhi}."),
        };
    }

    private static (RenderSupport, string) RenderingModeNow()
    {
        var g = FortnitePotato.Game;
        try { return RenderingMode(g.Installed ? File.ReadAllText(g.ConfigPath) : null); }
        catch (Exception ex) { return (RenderSupport.Unknown, "Couldn't read Fortnite's settings file: " + ex.Message); }
    }

    private static string DisplaySummary()
    {
        try
        {
            return string.Join("; ", DisplayManager.Displays().Select(d => $"{d.DeviceName.Replace(@"\\.\", "")}: Windows {d.Registry}{(d.Current == d.Registry ? "" : $" (now {d.Current}, e.g. while Fortnite runs)")}"));
        }
        catch (Exception ex) { return "display unreadable: " + ex.Message; }
    }

    // ---- verify / apply / restore / reset ------------------------------------------------------------------------------

    /// <summary>Reads Fortnite's profile and compares each preset field (read-only).</summary>
    /// <summary>Adds Inspector's view of each field to a report (metadata is optional).</summary>
    public static NvReport Annotate(NvReport r, IReadOnlyDictionary<uint, InspectorSetting>? meta) =>
        r with { Items = r.Items.Select(i => i with { Inspector = InspectorMetadata.Describe(i.Item, meta) }).ToList() };

    public static NvReport Verify(NvPreset preset, string exe = Exe)
    {
        var log = new List<string>();
        var (render, renderText) = RenderingModeNow();
        if (!NvApi.DriverPresent) return new(false, "No NVIDIA driver on this PC.", [], [], render, renderText, DisplaySummary(), null, null, "-", log);
        using var s = new NvApi.Session();
        var p = Target(s, exe, create: false, log, out _);
        if (p == IntPtr.Zero) return new(false, $"No NVIDIA profile lists {exe}.", [], [], render, renderText, DisplaySummary(), null, null, "-", log);
        var cur = Read(s, p);
        var items = preset.Items.Select(i =>
        {
            var (id, problem, detail) = ResolveLive(i);
            var c = cur.FirstOrDefault(x => x.Id == i.ExpectedId);
            if (problem is { } pr) return new NvItemResult(i, id, pr, detail, c?.Value, c?.Value);
            return c is not null && c.Type == 0 && c.Value == i.Value
                ? new NvItemResult(i, id, NvItemStatus.AlreadySet, "Profile has " + Hex(i.Value), c.Value, c.Value)
                : new NvItemResult(i, id, NvItemStatus.NotApplied, c is null ? "Not set in the Fortnite profile" : "Profile has " + Hex(c.Value), c?.Value, c?.Value);
        }).ToList();
        bool ok = items.Where(x => x.Item.Mandatory).All(x => x.Status == NvItemStatus.AlreadySet) && items.All(x => x.Status is NvItemStatus.AlreadySet or NvItemStatus.Unsupported);
        var summary = ok ? $"'{s.ProfileName(p)}' has every supported {preset.Name} value." : $"'{s.ProfileName(p)}' doesn't match {preset.Name}: {items.Count(x => x.Status == NvItemStatus.NotApplied)} field(s) differ.";
        return new(ok, summary, items, [], render, renderText, DisplaySummary(), null, null, "read-only", log);
    }

    /// <summary>Backs up the profile, imports the preset as a .nip, reads it back; rolls back on any failure.</summary>
    public static NvReport Apply(NvPreset preset, NvProfileState state, string exe = Exe, string? inspector = null)
    {
        var log = new List<string>();
        var (render, renderText) = RenderingModeNow();
        var displayBefore = DisplaySummary();
        if (!NvApi.DriverPresent) return new(false, "No NVIDIA driver on this PC; nothing changed.", [], [], render, renderText, displayBefore, null, null, "-", log);

        // 1. Resolve every field against the driver's own metadata.
        var resolved = preset.Items.Select(i => (item: i, r: ResolveLive(i))).ToList();
        if (resolved.FirstOrDefault(x => x.item.Mandatory && x.r.problem is not null) is { item: { } mi } bad)
            return new(false, $"Nothing changed: the mandatory field {mi.Field} can't be written ({bad.r.detail}).",
                resolved.Select(x => new NvItemResult(x.item, x.r.id, x.r.problem ?? NvItemStatus.NotApplied, x.r.detail, null, null)).ToList(), [], render, renderText, displayBefore, null, null, "-", log);

        // 2. Full backup of the Fortnite profile before anything is written.
        NvSnapshot snap;
        string backupJson, backupNip;
        using (var s = new NvApi.Session())
        {
            var p = Target(s, exe, create: true, log, out var created);
            if (created) s.Save();
            snap = new NvSnapshot { At = DateTime.Now, ProfileName = s.ProfileName(p), Exe = exe, CreatedProfile = created, Settings = Read(s, p) };
        }
        (backupJson, backupNip) = SaveBackup(snap);
        state.LastBackupJson = backupJson;
        state.Save();
        log.Add($"Backed up '{snap.ProfileName}' ({snap.Settings.Count} settings) to {backupNip}");

        // 3. Write the preset as a .nip and import it.
        var toWrite = resolved.Where(x => x.r.problem is null).Select(x => new NvSnapSetting(x.item.ExpectedId, x.item.DriverName, 0, false, x.item.Value, null)).ToList();
        var appliedNip = Path.Combine(BackupDir, $"{Regex.Replace(preset.Name, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant()}-{snap.At:yyyyMMdd-HHmmss}.nip");
        string method;
        if (inspector is not null && File.Exists(inspector))
        {
            // Only start the exact file the user chose: a changed or swapped exe at the remembered path is refused.
            if (!InspectorTrusted(state, inspector))
                return Finish(false, $"NVIDIA Profile Inspector at {inspector} isn't the file you chose earlier (it changed), so it wasn't started. Choose it again to use it.",
                    preset, resolved, snap, exe, backupNip, appliedNip, "none", render, renderText, displayBefore, log, rollback: true);
            // Inspector may replace the whole profile on import, so its file carries every user setting plus the preset.
            var merged = snap.Settings.Where(x => !x.Predefined && !toWrite.Any(t => t.Id == x.Id)).Concat(toWrite);
            File.WriteAllText(appliedNip, BuildNip(snap.ProfileName, exe, merged), Encoding.Unicode);
            method = "NVIDIA Profile Inspector import";
            var (ok, msg) = RunInspector(inspector, appliedNip);
            log.Add(msg);
            if (!ok) return Finish(false, "NVIDIA Profile Inspector didn't import the file: " + msg, preset, resolved, snap, exe, backupNip, appliedNip, method, render, renderText, displayBefore, log, rollback: true);
        }
        else
        {
            File.WriteAllText(appliedNip, BuildNip(snap.ProfileName, exe, toWrite), Encoding.Unicode);
            method = "NVAPI profile import (the driver API NVIDIA Profile Inspector uses)";
            log.AddRange(ImportNip(appliedNip, exe, replace: false));
        }
        return Finish(true, "", preset, resolved, snap, exe, backupNip, appliedNip, method, render, renderText, displayBefore, log, rollback: false);
    }

    private static NvReport Finish(bool imported, string failMsg, NvPreset preset, List<(NvPresetItem item, (uint? id, NvItemStatus? problem, string detail) r)> resolved,
        NvSnapshot snap, string exe, string backupNip, string appliedNip, string method, RenderSupport render, string renderText, string displayBefore, List<string> log, bool rollback)
    {
        // 4. Read everything back in a fresh session.
        List<NvSnapSetting> after;
        using (var s = new NvApi.Session())
        {
            var p = Target(s, exe, create: false, log, out _);
            after = p == IntPtr.Zero ? [] : Read(s, p);
        }
        var items = resolved.Select(x =>
        {
            var before = snap.Settings.FirstOrDefault(b => b.Id == x.item.ExpectedId)?.Value;
            var a = after.FirstOrDefault(b => b.Id == x.item.ExpectedId);
            if (x.r.problem is { } pr) return new NvItemResult(x.item, x.r.id, pr, x.r.detail, before, a?.Value);
            bool matches = a is { Type: 0 } && a.Value == x.item.Value;
            var status = !matches ? NvItemStatus.NotApplied : before == x.item.Value && !(snap.Settings.FirstOrDefault(b => b.Id == x.item.ExpectedId)?.Predefined ?? false) ? NvItemStatus.AlreadySet : NvItemStatus.Applied;
            return new NvItemResult(x.item, x.r.id, status, matches ? "Read back " + Hex(a!.Value) : $"Driver has {(a is null ? "nothing" : Hex(a.Value))}, expected {Hex(x.item.Value)}", before, a?.Value);
        }).ToList();
        var presetIds = preset.Items.Select(i => i.ExpectedId).ToHashSet();
        var unrelated = new List<string>();
        foreach (var b in snap.Settings.Where(b => !presetIds.Contains(b.Id)))
        {
            var a = after.FirstOrDefault(x => x.Id == b.Id);
            if (a is null) unrelated.Add($"{Hex(b.Id)} {b.Name}: was {(b.Type == 0 ? Hex(b.Value) : b.Text)}, now missing");
            else if (!Same(a, b)) unrelated.Add($"{Hex(b.Id)} {b.Name}: {(b.Type == 0 ? Hex(b.Value) : b.Text)} → {(a.Type == 0 ? Hex(a.Value) : a.Text)}");
        }
        foreach (var a in after.Where(a => !presetIds.Contains(a.Id) && !snap.Settings.Any(b => b.Id == a.Id)))
            unrelated.Add($"{Hex(a.Id)} {a.Name}: added {(a.Type == 0 ? Hex(a.Value) : a.Text)}");
        var displayAfter = DisplaySummary();
        if (displayAfter != displayBefore) unrelated.Add($"Windows display changed: {displayBefore} → {displayAfter}");

        bool mandatoryOk = items.Where(i => i.Item.Mandatory).All(i => i.Status is NvItemStatus.Applied or NvItemStatus.AlreadySet);
        bool ok = imported && mandatoryOk && unrelated.Count == 0 && !items.Any(i => i.Status == NvItemStatus.NotApplied);
        string summary;
        if (!ok)
        {
            // 5. Roll back to the backup and confirm it.
            var why = !imported ? failMsg : !mandatoryOk ? "a mandatory field didn't read back" : unrelated.Count > 0 ? "unrelated settings changed" : "some fields didn't read back";
            try
            {
                log.AddRange(RestoreSnapshot(snap, exe));
                summary = $"Not applied: {why}. The previous profile was imported again from {backupNip} and checked.";
            }
            catch (Exception ex) { summary = $"Not applied: {why}. Rolling back failed too ({ex.Message}); import {backupNip} manually."; }
            Log.Warn($"NVIDIA preset '{preset.Name}' failed ({why}): " + string.Join(" | ", log));
        }
        else
        {
            var skipped = items.Count(i => i.Status is NvItemStatus.Unsupported or NvItemStatus.Rejected);
            summary = $"{preset.Name}: {items.Count(i => i.Status == NvItemStatus.Applied)} field(s) written, {items.Count(i => i.Status == NvItemStatus.AlreadySet)} already set" +
                      (skipped > 0 ? $", {skipped} not supported by this driver" : "") + $"; every written value read back from the driver. Unrelated settings and the Windows display are unchanged.";
            Log.Info($"NVIDIA preset '{preset.Name}' applied via {method}: " + string.Join(" | ", items.Select(i => $"{Hex(i.Item.ExpectedId)}={i.Status}")));
        }
        return new(ok, summary, items, unrelated, render, renderText, displayAfter, backupNip, appliedNip, method, log);
    }

    /// <summary>Puts the profile back exactly as in the snapshot and verifies it. Deletes the profile if Money Tweaks created it.</summary>
    public static List<string> RestoreSnapshot(NvSnapshot snap, string exe = Exe)
    {
        var log = new List<string>();
        if (snap.CreatedProfile)
        {
            using var s = new NvApi.Session();
            var p = s.FindProfileForApp(exe);
            if (p != IntPtr.Zero && s.ProfileName(p) == NvidiaFortnite.OwnProfileName && !s.IsGlobalOrBase(p)) { s.DeleteProfile(p); s.Save(); log.Add($"Deleted '{NvidiaFortnite.OwnProfileName}' (created by Money Tweaks)"); }
            return log;
        }
        var tmp = Path.Combine(Path.GetTempPath(), $"mt-restore-{Guid.NewGuid():N}.nip");
        try
        {
            File.WriteAllText(tmp, BuildNip(snap.ProfileName, exe, snap.Settings), Encoding.Unicode);
            log.AddRange(ImportNip(tmp, exe, replace: true, exact: snap.Settings));
        }
        finally { try { File.Delete(tmp); } catch { } }
        using (var s = new NvApi.Session())
        {
            var p = Target(s, exe, create: false, log, out _);
            var now = Read(s, p);
            var diff = snap.Settings.Where(b => now.FirstOrDefault(a => a.Id == b.Id) is not { } a || !Same(a, b)).Select(b => Hex(b.Id)).ToList();
            diff.AddRange(now.Where(a => !snap.Settings.Any(b => b.Id == a.Id)).Select(a => Hex(a.Id) + " (extra)"));
            if (diff.Count > 0) throw new InvalidOperationException("After restoring, these differ from the backup: " + string.Join(", ", diff));
        }
        log.Add($"Restored and verified '{snap.ProfileName}' from the backup of {snap.At:g}");
        return log;
    }

    public static NvSnapshot? LoadSnapshot(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<NvSnapshot>(File.ReadAllText(path));
    }

    /// <summary>Returns only the settings Money Tweaks' presets can touch to NVIDIA's values (predefined or inherited).</summary>
    public static List<string> Reset(string exe = Exe)
    {
        var log = new List<string>();
        using (var s = new NvApi.Session())
        {
            var p = Target(s, exe, create: false, log, out _);
            if (p == IntPtr.Zero) { log.Add($"No NVIDIA profile lists {exe}; nothing to reset."); return log; }
            var cur = Read(s, p);
            foreach (var id in ManagedIds)
                if (cur.FirstOrDefault(c => c.Id == id) is { Predefined: false } c) { s.Reset(p, id); log.Add($"{Hex(id)} {c.Name}: {Hex(c.Value)} → NVIDIA default"); }
            s.Save();
        }
        using (var s = new NvApi.Session())
        {
            var p = Target(s, exe, create: false, log, out _);
            var left = Read(s, p).Where(c => ManagedIds.Contains(c.Id) && !c.Predefined).ToList();
            if (left.Count > 0) throw new InvalidOperationException("Still set after reset: " + string.Join(", ", left.Select(l => Hex(l.Id))));
        }
        Log.Info("NVIDIA Fortnite profile reset to NVIDIA defaults: " + string.Join(" | ", log));
        return log;
    }

    // ---- NVIDIA Profile Inspector (optional, never downloaded) -------------------------------------------------------

    /// <summary>Looks for nvidiaProfileInspector.exe in the usual places. Returns null if it isn't installed.</summary>
    public static string? FindInspector(string? remembered)
    {
        if (remembered is not null && File.Exists(remembered)) return remembered;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var hit = Directory.EnumerateFiles(dir, "nvidiaProfileInspector.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Remembers the Inspector exe the user chose, pinned to its SHA-256 (Inspector releases aren't code-signed).</summary>
    public static void TrustInspector(NvProfileState state, string? path)
    {
        state.InspectorPath = path;
        state.InspectorSha256 = path is not null && File.Exists(path) ? Sha256(path) : null;
    }

    /// <summary>True only for the exact file (same path and contents) the user chose.</summary>
    public static bool InspectorTrusted(NvProfileState state, string path) =>
        state.InspectorPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
        && state.InspectorSha256 is { } h && File.Exists(path) && string.Equals(Sha256(path), h, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>Runs Inspector on a .nip (Inspector imports a .nip passed on its command line; it asks for admin rights itself).</summary>
    public static (bool ok, string message) RunInspector(string inspector, string nip)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(inspector, $"\"{nip}\"") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(inspector) });
            if (p is null) return (false, "NVIDIA Profile Inspector didn't start.");
            if (!p.WaitForExit(180_000)) return (false, "NVIDIA Profile Inspector was still open after 3 minutes; close it and press Verify.");
            return (true, $"NVIDIA Profile Inspector finished (exit code {p.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, "The administrator prompt for NVIDIA Profile Inspector was declined."); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
