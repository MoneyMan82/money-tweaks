using System.Text.Json;
using MoneyTweaks.Community;
using MoneyTweaks.Core.Catalog;
using MoneyTweaks.Core.Community;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Profiles;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Community Hub imports: previews, validation against this PC's catalog, and nothing applied.</summary>
[Collection("registry")]
public sealed class CommunityImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-community-import-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _origRoot = AppPaths.Root;

    public CommunityImportTests() { Directory.CreateDirectory(_dir); AppPaths.OverrideRoot(_dir); }
    public void Dispose() { AppPaths.OverrideRoot(_origRoot); try { Directory.Delete(_dir, true); } catch { } }

    private static string Usable() => TweakCatalog.All.First(t => t.Kind == TweakKind.Toggle && ProfileStore.Usable(t, out _)).Id;
    private static string Caution() => TweakCatalog.All.First(t => t.Kind == TweakKind.Toggle && t.Risk == Risk.Caution).Id;
    private static string Guide() => TweakCatalog.All.First(t => t.Kind == TweakKind.Guide).Id;

    [Fact]
    public void Preview_marks_unknown_guides_and_caution_tweaks()
    {
        var b = new TweakBundle { Name = "x", TweakIds = [Usable(), "does.not.exist", Guide(), Caution()] };
        var lines = CommunityImport.Preview(b, forProfile: false);
        Assert.True(lines.Single(l => l.Id == b.TweakIds[0]).WillImport);
        Assert.False(lines.Single(l => l.Id == "does.not.exist").WillImport);
        Assert.False(lines.Single(l => l.Id == Guide()).WillImport);
        var c = lines.Single(l => l.Id == Caution());
        Assert.True(c.Caution && c.WillImport);
    }

    [Fact]
    public void Gaming_profile_import_is_validated_renamed_and_not_activated()
    {
        var existing = ProfileStore.BuiltIns.ToList();
        var b = new TweakBundle
        {
            Name = existing[0].Name, Description = "shared", TweakIds = [Usable(), "nope.nope"], Power = "HighPerformance",
            TimerResolutionMs = 5, RaiseGamePriority = true, GameExes = ["Game.exe"],
        };
        var (p, notes) = CommunityImport.ToGamingProfile(b, existing);
        Assert.NotNull(p);
        Assert.NotEqual(existing[0].Name, p!.Name); // unique name
        Assert.Equal([Usable()], p.TweakIds);
        Assert.Equal(PowerChoice.HighPerformance, p.Power);
        Assert.Null(p.TimerResolutionMs); // 5 ms is outside what profiles support
        Assert.Contains(notes, n => n.Contains("skipped"));
        Assert.Contains(notes, n => n.Contains("Timer resolution"));
        Assert.Null(ProfileStore.Load().Active); // importing never activates anything
        Assert.Empty(ProfileStore.Load().AutoSwitch);
    }

    [Fact]
    public void Preset_import_skips_unknown_tweaks_and_refuses_empty_ones()
    {
        var (p, _) = CommunityImport.ToPreset(new TweakBundle { Name = "Mine", TweakIds = [Usable(), "x.unknown"] }, PresetStore.BuiltIns);
        Assert.Equal([Usable()], p!.TweakIds);
        var (none, notes) = CommunityImport.ToPreset(new TweakBundle { Name = "Empty", TweakIds = ["x.unknown"] }, []);
        Assert.Null(none);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public async Task Crosshair_import_clamps_values_and_stores_post_images()
    {
        var p = CrosshairTemplates.Create("Cross + dot");
        p.Layers[0].Thickness = 2;
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Image, Name = "logo", ImageFile = "img:0123456789abcdef0123456789abcdef", Size = 32 });
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Image, Name = "missing", ImageFile = "img:fedcba9876543210fedcba9876543210" });
        var json = CommunityImport.CrosshairForPost(p, f => f[4..]); // "img:<id>" → id
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.GetRawText())!;
        Assert.True(raw.ContainsKey("layers"));

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAAEUlEQVR42mP8z8DwnwEIGGEAAB0gAf8dwsEgAAAAAElFTkSuQmCC");
        var (imported, notes) = await CommunityImport.ToCrosshairAsync(json, [p], id => Task.FromResult(id == "0123456789abcdef0123456789abcdef" ? png : null));
        Assert.NotNull(imported);
        Assert.NotEqual(p.Name, imported!.Name);
        var logo = imported.Layers.Single(l => l.Name == "logo");
        Assert.True(logo.ImageFile is { } f && CrosshairImages.Exists(f));
        Assert.Equal(0.5, logo.ImageAspect, 3);
        Assert.Null(imported.Layers.Single(l => l.Name == "missing").ImageFile);
        Assert.Contains(notes, n => n.Contains("missing"));

        var (bad, badNotes) = await CommunityImport.ToCrosshairAsync(JsonDocument.Parse("[1,2]").RootElement, [], _ => Task.FromResult<byte[]?>(null));
        Assert.Null(bad);
        Assert.NotEmpty(badNotes);
    }

    [Fact]
    public void Local_crosshair_images_are_only_shared_when_uploaded()
    {
        var p = new CrosshairProfile { Name = "x" };
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Image, ImageFile = new string('a', 40) + ".png" });
        var withoutUpload = CommunityImport.CrosshairForPost(p, _ => null);
        Assert.Equal(JsonValueKind.Null, withoutUpload.GetProperty("layers")[0].GetProperty("imageFile").ValueKind);
        var withUpload = CommunityImport.CrosshairForPost(p, _ => "0123456789abcdef0123456789abcdef");
        Assert.Equal("img:0123456789abcdef0123456789abcdef", withUpload.GetProperty("layers")[0].GetProperty("imageFile").GetString());
        Assert.Equal(new string('a', 40) + ".png", p.Layers[0].ImageFile); // the local profile isn't changed
    }

    [Fact]
    public void Shared_post_rules_normalize_and_reject()
    {
        var ok = new PostInput { Title = "  Fortnite   settings​ ", Game = "Fortnite", Category = "Competitive", Tags = ["Stretched", "stretched", "240HZ"], Content = new PostContent { Settings = new GameSettings { FpsCap = 240 } } };
        Assert.Empty(PostRules.Validate(ok));
        Assert.Equal(["stretched", "240hz"], ok.Tags);
        Assert.DoesNotContain('​', ok.Title);
        var bad = new PostInput { Title = "Visit http://x.com", Game = "Fortnite", Category = "Competitive", Content = new PostContent { Settings = new GameSettings { ResolutionWidth = 99999, ResolutionHeight = 10 } } };
        var errors = PostRules.Validate(bad);
        Assert.Contains(errors, e => e.Contains("Links"));
        Assert.Contains(errors, e => e.Contains("Resolution"));
        Assert.Equal(["a = b", "Wall = Q"], CommunityImport.ParsePairs("a = b\n\nWall: Q\n").Select(p => p.a + " = " + p.b));
    }
}
