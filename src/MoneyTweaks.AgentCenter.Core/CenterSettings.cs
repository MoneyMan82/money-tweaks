using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoneyTweaks.AgentCenter.Core;

/// <summary>The Command Center's own settings (%LOCALAPPDATA%\MoneyTweaksAgentCenter\settings.json).</summary>
public sealed class CenterSettings
{
    public string? Workspace { get; set; }
    /// <summary>Optional path to claude.exe if it isn't found automatically.</summary>
    public string? ClaudePath { get; set; }
    public RunSchedule Schedule { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CenterSettings Load(string path)
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<CenterSettings>(File.ReadAllText(path), Json) ?? new(); }
        catch { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}
