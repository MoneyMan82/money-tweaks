using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

[Collection("registry")]
public class PresetTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-presets-" + Guid.NewGuid().ToString("N"));
    public PresetTests() { Directory.CreateDirectory(_tmp); AppPaths.OverrideRoot(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Builtin_presets_only_use_real_tweaks()
    {
        foreach (var p in PresetStore.BuiltIns)
        {
            Assert.NotEmpty(p.TweakIds);
            Assert.All(p.TweakIds, id => Assert.True(TweakCatalog.Find(id) is not null, $"{p.Name}: unknown id {id}"));
            Assert.Empty(PresetStore.Validate(p, []));
        }
        Assert.Equal(5, PresetStore.BuiltIns.Count);
    }

    [Fact]
    public void Validation_reports_clear_problems()
    {
        var bad = new TweakPreset { Name = " ", TweakIds = ["nope.missing"] };
        var errors = PresetStore.Validate(bad, []);
        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("nope.missing"));
        var dup = new TweakPreset { Name = "Low latency", TweakIds = ["gen.game-mode"] };
        Assert.Contains(PresetStore.Validate(dup, PresetStore.BuiltIns), e => e.Contains("already exists"));
        Assert.Contains(PresetStore.Validate(new TweakPreset { Name = "x" }, []), e => e.Contains("at least one"));
    }

    [Fact]
    public void Save_and_load_survive_restart()
    {
        var f = new PresetFile();
        f.Presets.Add(new TweakPreset { Name = "Mine", Description = "d", TweakIds = ["gen.game-mode", "net.nagle"] });
        f.History.Add(new PresetRun { PresetId = f.Presets[0].Id, AppliedIds = ["net.nagle"] });
        PresetStore.Save(f);
        var loaded = PresetStore.Load();
        Assert.Equal("Mine", loaded.Presets.Single().Name);
        Assert.Equal(["gen.game-mode", "net.nagle"], loaded.Presets[0].TweakIds);
        Assert.Equal("net.nagle", loaded.History.Single().AppliedIds.Single());
    }

    [Fact]
    public void Corrupt_file_is_kept_and_does_not_crash()
    {
        File.WriteAllText(Path.Combine(_tmp, "presets.json"), "{ not json");
        var f = PresetStore.Load();
        Assert.Empty(f.Presets);
        Assert.NotEmpty(Directory.GetFiles(_tmp, "presets.json.corrupt-*"));
    }

    [Fact]
    public void Export_then_import_roundtrips_and_dedupes_names()
    {
        var path = Path.Combine(_tmp, "export.json");
        var mine = new TweakPreset { Name = "Streaming", Description = "for OBS", TweakIds = ["gen.game-mode", "adv.power-throttling"] };
        PresetStore.Export([mine], path);
        var (presets, problems) = PresetStore.Import(path, [mine]);
        Assert.Empty(problems);
        var p = Assert.Single(presets);
        Assert.Equal("Streaming (2)", p.Name);
        Assert.Equal(mine.TweakIds, p.TweakIds);
        Assert.NotEqual(mine.Id, p.Id);
    }

    [Theory]
    [InlineData("{ broken", "Not valid JSON")]
    [InlineData("{\"format\":\"something-else\",\"presets\":[]}", "isn't a Money Tweaks preset export")]
    [InlineData("{\"format\":\"money-tweaks-presets\",\"version\":9,\"presets\":[]}", "newer Money Tweaks")]
    [InlineData("{\"format\":\"money-tweaks-presets\",\"version\":1}", "no \"presets\" list")]
    [InlineData("{\"format\":\"money-tweaks-presets\",\"version\":1,\"presets\":[{\"Name\":\"X\",\"TweakIds\":[\"fake.id\"]}]}", "no usable tweaks")]
    public void Import_rejects_bad_files_with_clear_messages(string json, string expected)
    {
        var path = Path.Combine(_tmp, "bad.json");
        File.WriteAllText(path, json);
        var (presets, problems) = PresetStore.Import(path, []);
        Assert.Empty(presets);
        Assert.Contains(problems, m => m.Contains(expected));
    }

    [Fact]
    public void Import_keeps_known_tweaks_and_warns_about_unknown_ones()
    {
        var path = Path.Combine(_tmp, "mixed.json");
        File.WriteAllText(path, "{\"format\":\"money-tweaks-presets\",\"version\":1,\"presets\":[{\"Name\":\"Mixed\",\"TweakIds\":[\"gen.game-mode\",\"fake.id\"]}]}");
        var (presets, problems) = PresetStore.Import(path, []);
        Assert.Equal(["gen.game-mode"], Assert.Single(presets).TweakIds);
        Assert.Contains(problems, m => m.Contains("fake.id"));
    }
}
