using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Crosshair;

/// <summary>
/// Saves and loads crosshair.json (atomic writes; a damaged file is kept aside, never overwritten silently). The
/// first load after updating from 1.40 or earlier turns the old single crosshair into the first profile.
/// </summary>
public static class CrosshairStore
{
    public static string Folder => Path.Combine(AppPaths.Root, "crosshair");
    public static string FilePath => Path.Combine(Folder, "crosshair.json");
    internal static JsonSerializerOptions Json => CrosshairProfile.Json;

    public static CrosshairData Load(Features.CrosshairSettings legacy, out string? notice)
    {
        notice = null;
        if (File.Exists(FilePath))
        {
            try
            {
                var d = JsonSerializer.Deserialize<CrosshairData>(File.ReadAllText(FilePath), Json) ?? throw new InvalidDataException("empty file");
                CrosshairSanitizer.Clean(d);
                return d;
            }
            catch (Exception ex)
            {
                var aside = FilePath + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(FilePath, aside); } catch { }
                notice = $"Your crosshair file couldn't be read ({ex.Message}). It was kept as {Path.GetFileName(aside)} and your crosshair from before the update was restored.";
                Log.Warn("crosshair.json unreadable: " + ex.Message);
            }
        }
        return Migrate(legacy);
    }

    public static CrosshairData Migrate(Features.CrosshairSettings legacy)
    {
        var d = new CrosshairData();
        var p = CrosshairTemplates.FromLegacy(legacy);
        d.Profiles.Add(p);
        d.ActiveId = p.Id;
        d.Overlay.MonitorIndex = Math.Max(0, legacy.MonitorIndex);
        d.Overlay.PositionX = legacy.OffsetX;
        d.Overlay.PositionY = legacy.OffsetY;
        CrosshairSanitizer.Clean(d);
        return d;
    }

    public static void Save(CrosshairData d)
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(d, Json));
    }
}

/// <summary>
/// Imported PNG crosshairs, stored once each by content hash in the crosshair folder. Only real PNG files are accepted
/// (signature, header and size checked); nothing is executed or decoded beyond the header here.
/// </summary>
public static class CrosshairImages
{
    public const int MaxBytes = 4 * 1024 * 1024, MaxSide = 2048;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string Folder => Path.Combine(CrosshairStore.Folder, "images");
    public static bool IsValidName(string name) => CrosshairSanitizer.IsLocalImageName(name);
    public static string PathOf(string name) => Path.Combine(Folder, name);
    public static bool Exists(string name) => IsValidName(name) && File.Exists(PathOf(name));

    public sealed record PngInfo(int Width, int Height, bool HasAlpha);

    /// <summary>Reads the PNG header; null if the bytes aren't a PNG Money Tweaks accepts.</summary>
    public static PngInfo? Inspect(byte[] data, out string? problem)
    {
        problem = null;
        if (data.Length > MaxBytes) { problem = $"The image is larger than {MaxBytes / 1024 / 1024} MB."; return null; }
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature) || Encoding.ASCII.GetString(data, 12, 4) != "IHDR")
        { problem = "That file isn't a PNG image."; return null; }
        int w = BE(data, 16), h = BE(data, 20);
        byte colorType = data[25];
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { problem = $"The image must be at most {MaxSide}×{MaxSide} pixels (this one is {w}×{h})."; return null; }
        bool alpha = colorType is 4 or 6;
        // Palette/greyscale/RGB images can still be transparent through a tRNS chunk.
        for (int pos = 8; !alpha && pos + 8 <= data.Length;)
        {
            int len = BE(data, pos);
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type == "tRNS") alpha = true;
            if (type is "IDAT" or "IEND" || len < 0) break;
            pos += 12 + len;
        }
        return new PngInfo(w, h, alpha);
    }

    /// <summary>Stores a PNG and returns its file name. Throws <see cref="InvalidDataException"/> with a readable message if it's not acceptable.</summary>
    public static string Store(byte[] data, out PngInfo info)
    {
        info = Inspect(data, out var problem) ?? throw new InvalidDataException(problem);
        var name = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant() + ".png";
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Folder);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        return name;
    }

    public static byte[]? Read(string name)
    {
        try { return Exists(name) ? File.ReadAllBytes(PathOf(name)) : null; } catch { return null; }
    }

    private static int BE(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
}

/// <summary>
/// Export files and shareable codes. A code is "MTX1:" + base64url(deflate(JSON)); images ride along when small
/// enough for a code (larger ones go in export files). Everything imported is validated and clamped.
/// </summary>
public static class CrosshairSharing
{
    public const string CodePrefix = "MTX1:", FileFormat = "moneytweaks-crosshair";
    public const int MaxCodeImageBytes = 24 * 1024, MaxDecodedBytes = 12 * 1024 * 1024;

    private static readonly JsonSerializerOptions Compact = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public sealed class Package
    {
        public string Format { get; set; } = "";
        public int Version { get; set; } = 1;
        public CrosshairProfile Profile { get; set; } = new();
        /// <summary>Image file name → base64 PNG.</summary>
        public Dictionary<string, string> Images { get; set; } = [];
    }

    public sealed record ImportResult(CrosshairProfile Profile, List<string> Notes);

    public static Package Pack(CrosshairProfile p, int maxImageBytes, List<string>? notes = null)
    {
        var pkg = new Package { Format = FileFormat, Profile = p.Clone() };
        foreach (var l in pkg.Profile.Layers.Where(l => l.Kind == LayerKind.Image && l.ImageFile is not null))
        {
            if (pkg.Images.ContainsKey(l.ImageFile!)) continue;
            var bytes = CrosshairImages.Read(l.ImageFile!);
            if (bytes is null) { notes?.Add($"Image for \"{l.Name}\" is missing on this PC and wasn't included."); continue; }
            if (bytes.Length > maxImageBytes) { notes?.Add($"Image \"{l.Name}\" is {bytes.Length / 1024} KB, too big for a code; use Export to file to share it."); continue; }
            pkg.Images[l.ImageFile!] = Convert.ToBase64String(bytes);
        }
        return pkg;
    }

    public static string ToCode(CrosshairProfile p, List<string>? notes = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(Pack(p, MaxCodeImageBytes, notes), Compact);
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.SmallestSize)) z.Write(json);
        return CodePrefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string ToFile(CrosshairProfile p, List<string>? notes = null) =>
        JsonSerializer.Serialize(Pack(p, CrosshairImages.MaxBytes, notes), CrosshairStore.Json);

    /// <summary>Accepts a share code or the contents of an export file.</summary>
    public static ImportResult Import(string text)
    {
        text = (text ?? "").Trim();
        Package? pkg;
        if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var b64 = Regex.Replace(text[CodePrefix.Length..], @"\s+", "").Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            byte[] raw;
            try { raw = Convert.FromBase64String(b64); } catch { throw new InvalidDataException("That code is damaged or incomplete (copy the whole code, starting with MTX1:)."); }
            using var z = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var buf = new byte[81920];
            int n;
            try
            {
                while ((n = z.Read(buf)) > 0)
                {
                    outMs.Write(buf, 0, n);
                    if (outMs.Length > MaxDecodedBytes) throw new InvalidDataException("That code is too large.");
                }
            }
            catch (InvalidDataException) { throw; }
            catch { throw new InvalidDataException("That code is damaged or incomplete (copy the whole code, starting with MTX1:)."); }
            pkg = Parse(outMs.ToArray());
        }
        else if (text.StartsWith('{'))
        {
            if (text.Length > MaxDecodedBytes) throw new InvalidDataException("That file is too large.");
            pkg = Parse(Encoding.UTF8.GetBytes(text));
        }
        else throw new InvalidDataException("Paste a Money Tweaks crosshair code (it starts with MTX1:) or choose an exported .mtcrosshair file.");

        var notes = new List<string>();
        var profile = pkg.Profile;
        CrosshairSanitizer.Clean(profile);
        var renamed = new Dictionary<string, string>();
        foreach (var (name, b64) in pkg.Images)
        {
            try
            {
                var stored = CrosshairImages.Store(Convert.FromBase64String(b64), out _);
                renamed[name] = stored;
            }
            catch (Exception ex) { notes.Add("An image wasn't imported: " + ex.Message); }
        }
        foreach (var l in profile.Layers.Where(l => l.Kind == LayerKind.Image))
        {
            if (l.ImageFile is not null && renamed.TryGetValue(l.ImageFile, out var s)) l.ImageFile = s;
            else if (l.ImageFile is null || !CrosshairImages.Exists(l.ImageFile))
            {
                notes.Add($"Layer \"{l.Name}\" has no image (it wasn't included); import the PNG again to use it.");
                l.ImageFile = null;
            }
        }
        return new ImportResult(profile, notes);
    }

    private static Package Parse(byte[] json)
    {
        Package? pkg;
        try { pkg = JsonSerializer.Deserialize<Package>(json, CrosshairStore.Json); }
        catch (JsonException) { throw new InvalidDataException("That isn't a Money Tweaks crosshair."); }
        if (pkg is null || pkg.Format != FileFormat || pkg.Profile is null) throw new InvalidDataException("That isn't a Money Tweaks crosshair.");
        if (pkg.Version > 1) throw new InvalidDataException("That crosshair was made with a newer Money Tweaks; update to import it.");
        pkg.Images ??= [];
        return pkg;
    }
}

/// <summary>Undo/redo per profile, as snapshots. Edits are committed by the editor after a short pause, so dragging a slider is one step.</summary>
public sealed class CrosshairHistory
{
    public const int Limit = 200;
    private sealed class Steps { public List<string> Undo = []; public List<string> Redo = []; public string Current = ""; }
    private readonly Dictionary<string, Steps> _tracks = [];

    private static string Snap(CrosshairProfile p) => JsonSerializer.Serialize(p, CrosshairProfile.Json);

    private Steps For(CrosshairProfile p)
    {
        if (!_tracks.TryGetValue(p.Id, out var t)) _tracks[p.Id] = t = new Steps { Current = Snap(p) };
        return t;
    }

    /// <summary>Starts tracking a profile (no-op if it's already tracked).</summary>
    public void Track(CrosshairProfile p) => For(p);

    /// <summary>Records the profile's current state as a step. Returns false if nothing changed.</summary>
    public bool Commit(CrosshairProfile p)
    {
        var t = For(p);
        var now = Snap(p);
        if (now == t.Current) return false;
        t.Undo.Add(t.Current);
        if (t.Undo.Count > Limit) t.Undo.RemoveAt(0);
        t.Redo.Clear();
        t.Current = now;
        return true;
    }

    public bool CanUndo(CrosshairProfile p) => For(p).Undo.Count > 0;
    public bool CanRedo(CrosshairProfile p) => For(p).Redo.Count > 0;

    public CrosshairProfile? Undo(CrosshairProfile p)
    {
        Commit(p);
        var t = For(p);
        if (t.Undo.Count == 0) return null;
        t.Redo.Add(t.Current);
        t.Current = t.Undo[^1];
        t.Undo.RemoveAt(t.Undo.Count - 1);
        return JsonSerializer.Deserialize<CrosshairProfile>(t.Current, CrosshairProfile.Json);
    }

    public CrosshairProfile? Redo(CrosshairProfile p)
    {
        Commit(p);
        var t = For(p);
        if (t.Redo.Count == 0) return null;
        t.Undo.Add(t.Current);
        t.Current = t.Redo[^1];
        t.Redo.RemoveAt(t.Redo.Count - 1);
        return JsonSerializer.Deserialize<CrosshairProfile>(t.Current, CrosshairProfile.Json);
    }

    public void Forget(string profileId) => _tracks.Remove(profileId);
}
