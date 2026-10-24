using System.Text.Json;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>A bundled third-party 3D model (see Assets/Gpu/manifest.json).</summary>
public sealed record GpuAsset(string File, string Title, string Author, string Source, string Download, string License, string LicenseUrl,
    string Attribution, string Sha256, string Retrieved, string Depicts, string? Design, string[] Matches)
{
    /// <summary>Board maker the model depicts (partner cards): only used for cards from that maker. Null = any.</summary>
    public string? Maker { get; init; }
    /// <summary>Rotation (degrees about X, Y, Z) that stands the model up: length along X, fans facing +Z.</summary>
    public double[]? Rotate { get; init; }
}

public sealed record GpuAssetRejected(string Title, string Author, string Source, string License, string Reason);

public sealed record GpuAssetManifest(string About, GpuAsset[] Assets, GpuAssetRejected[] EvaluatedNotBundled)
{
    /// <summary>Licenses that explicitly allow commercial use, modification and redistribution.</summary>
    public static readonly string[] AllowedLicenses = ["CC0-1.0", "CC-BY-3.0", "CC-BY-4.0"];

    public static GpuAssetManifest Parse(string json) =>
        JsonSerializer.Deserialize<GpuAssetManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new("", [], []);

    /// <summary>
    /// The verified model for a matched card: same library entry, same design (a Founders Edition model is never used for
    /// a partner card of that chip) and, for a maker-specific model, the same board maker. The first listed wins.
    /// Null = no verified model; the generated generic model is shown instead.
    /// </summary>
    public GpuAsset? For(GpuMatch m) => Candidates(m).FirstOrDefault();

    /// <summary>Every verified model for a matched card, best first (a later one is used if an earlier file can't be read).</summary>
    public IEnumerable<GpuAsset> Candidates(GpuMatch m) =>
        m.Spec is not { } s ? []
        : Assets.Where(a => AllowedLicenses.Contains(a.License) && a.Matches.Contains(s.Id) && (a.Design is null || a.Design == m.Design.ToString())
            && (a.Maker is null || string.Equals(a.Maker, m.Maker, StringComparison.OrdinalIgnoreCase)));
}
