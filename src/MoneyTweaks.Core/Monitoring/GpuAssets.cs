using System.Text.Json;

namespace MoneyTweaks.Core.Monitoring;

/// <summary>A bundled third-party 3D model (see Assets/Gpu/manifest.json).</summary>
public sealed record GpuAsset(string File, string Title, string Author, string Source, string Download, string License, string LicenseUrl,
    string Attribution, string Sha256, string Retrieved, string Depicts, string? Design, string[] Matches);

public sealed record GpuAssetRejected(string Title, string Author, string Source, string License, string Reason);

public sealed record GpuAssetManifest(string About, GpuAsset[] Assets, GpuAssetRejected[] EvaluatedNotBundled)
{
    /// <summary>Licenses that explicitly allow commercial use, modification and redistribution.</summary>
    public static readonly string[] AllowedLicenses = ["CC0-1.0", "CC-BY-3.0", "CC-BY-4.0"];

    public static GpuAssetManifest Parse(string json) =>
        JsonSerializer.Deserialize<GpuAssetManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new("", [], []);

    /// <summary>
    /// The verified model for a matched card: same library entry and the same design (a Founders Edition model is never
    /// used for a partner card of that chip). Null = no verified model; the generated generic model is shown instead.
    /// </summary>
    public GpuAsset? For(GpuMatch m) =>
        m.Spec is not { } s ? null
        : Assets.FirstOrDefault(a => AllowedLicenses.Contains(a.License) && a.Matches.Contains(s.Id) && (a.Design is null || a.Design == m.Design.ToString()));
}
