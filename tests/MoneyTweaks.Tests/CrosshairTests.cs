using System.Text;
using MoneyTweaks.Core.Crosshair;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Tests;

/// <summary>Crosshair editor core: geometry and pixel snapping, firing bloom, legacy migration, storage, sharing, undo/redo.</summary>
[Collection("registry")]
public sealed class CrosshairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mt-crosshair-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _origRoot = AppPaths.Root;

    public CrosshairTests()
    {
        Directory.CreateDirectory(_dir);
        AppPaths.OverrideRoot(_dir);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_origRoot);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CrosshairProfile Cross(double len = 10, double t = 1, double gap = 4, bool outline = false)
    {
        var p = new CrosshairProfile();
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Lines, Length = len, Thickness = t, Gap = gap, Outline = outline });
        return p;
    }

    /// <summary>Pixels covered by a snapped rect, as integer columns/rows.</summary>
    private static (int x0, int x1, int y0, int y1) Px(CrosshairShape s) => ((int)s.X, (int)(s.X + s.W) - 1, (int)s.Y, (int)(s.Y + s.H) - 1);

    [Fact]
    public void Lines_are_pixel_exact_and_mirror_symmetric()
    {
        var shapes = CrosshairGeometry.Build(Cross(len: 10, t: 1, gap: 4))[0].Shapes;
        Assert.Equal(4, shapes.Count);
        var left = Px(shapes[0]); var right = Px(shapes[1]); var top = Px(shapes[2]); var bottom = Px(shapes[3]);
        // Centre pixel is 0; a 4 px gap leaves pixels 1..4 empty on each side, then 10 px of line.
        Assert.Equal((-14, -5, 0, 0), left);
        Assert.Equal((5, 14, 0, 0), right);
        Assert.Equal((0, 0, -14, -5), top);
        Assert.Equal((0, 0, 5, 14), bottom);
        foreach (var s in shapes) Assert.True(s.X == Math.Floor(s.X) && s.Y == Math.Floor(s.Y) && s.W == Math.Floor(s.W) && s.H == Math.Floor(s.H));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(3, -1, 1)]
    public void Thickness_covers_whole_pixels(double t, int from, int to)
    {
        var right = CrosshairGeometry.Build(Cross(t: t))[0].Shapes[1];
        Assert.Equal((from, to), (Px(right).y0, Px(right).y1));
    }

    [Fact]
    public void Outline_surrounds_each_shape_and_rotation_disables_snapping()
    {
        var p = Cross(outline: true);
        p.Layers[0].OutlineThickness = 1;
        var shapes = CrosshairGeometry.Build(p)[0].Shapes;
        Assert.Equal(8, shapes.Count); // outline + fill per arm
        Assert.Equal(shapes[1].X - 1, shapes[0].X);
        Assert.Equal(shapes[1].W + 2, shapes[0].W);
        p.Layers[0].Rotation = 45;
        var d = CrosshairGeometry.Build(p)[0];
        Assert.Equal(45, d.Rotation);
        Assert.Contains(d.Shapes, s => s.X != Math.Floor(s.X)); // unsnapped geometry, rotated around the centre
        Assert.Equal(CrosshairGeometry.Centre, d.PivotX);
    }

    [Fact]
    public void Hidden_layers_are_not_drawn_and_offsets_scale()
    {
        var p = Cross();
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Dot, Size = 2, OffsetX = 10, Outline = false });
        p.Layers[0].Visible = false;
        var d = Assert.Single(CrosshairGeometry.Build(p, 0, scale: 2));
        var dot = Assert.Single(d.Shapes);
        Assert.Equal(4, dot.W); // size × scale
        Assert.Equal(20.5, d.PivotX); // centre + offset × scale
    }

    [Fact]
    public void Bloom_spreads_reacting_layers_fades_and_flashes()
    {
        var p = Cross(gap: 4);
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Dot, Size = 3, ReactsToFiring = false, Outline = false });
        p.Firing = new FiringAnimation { Enabled = true, Spread = 6, FiringOpacity = 0.5, Flash = true, FlashColor = "#FFFF0000" };
        var rest = CrosshairGeometry.Build(p, 0);
        var full = CrosshairGeometry.Build(p, 1);
        Assert.Equal(Px(rest[0].Shapes[1]).x0 + 6, Px(full[0].Shapes[1]).x0); // right arm moved out by the spread
        Assert.Equal(rest[1].Shapes[0].W, full[1].Shapes[0].W); // the dot doesn't react
        Assert.Equal(0.5, full[0].Shapes[0].Opacity, 3);
        Assert.Equal(new Argb(0xFF, 0xFF, 0, 0), full[0].Shapes[0].Color);
        Assert.Equal(Argb.Parse("#FF2BE38B", default), full[1].Shapes[0].Color); // flash only on reacting layers
        p.Firing.Enabled = false;
        Assert.Equal(Px(rest[0].Shapes[1]), Px(CrosshairGeometry.Build(p, 1)[0].Shapes[1])); // disabled = no spread
        Assert.True(CrosshairGeometry.Extent(Cross(len: 10, gap: 4)) >= 14);
    }

    [Fact]
    public void Bloom_state_rises_while_held_and_recovers()
    {
        var a = new FiringAnimation { Enabled = true, RiseMs = 100, RecoverMs = 200 };
        var b = new BloomState();
        Assert.True(b.Update(true, 50, a));
        Assert.Equal(0.5, b.Value, 3);
        b.Update(true, 80, a);
        Assert.Equal(1, b.Value, 3);
        b.Update(false, 100, a);
        Assert.Equal(0.5, b.Value, 3);
        Assert.False(new BloomState().Update(false, 16, a)); // nothing to redraw at rest
        Assert.True(BloomState.IsFiring(FireTrigger.EitherMouse, false, true));
        Assert.False(BloomState.IsFiring(FireTrigger.LeftMouse, false, true));
    }

    [Theory]
    [InlineData("Cross", 1, false)]
    [InlineData("CrossDot", 2, false)]
    [InlineData("T", 1, true)]
    [InlineData("Dot", 1, false)]
    [InlineData("Circle", 2, false)]
    public void Old_crosshair_becomes_the_first_profile(string style, int layers, bool noTop)
    {
        var legacy = new CrosshairSettings { Style = style, Color = "#FF00FFFF", Size = 12, Thickness = 3, Gap = 5, Opacity = 0.8, OffsetX = 7, OffsetY = -3, MonitorIndex = 1 };
        var d = CrosshairStore.Load(legacy, out var notice);
        Assert.Null(notice);
        var p = Assert.Single(d.Profiles);
        Assert.Equal("My crosshair", p.Name);
        Assert.Equal(layers, p.Layers.Count);
        Assert.All(p.Layers, l => Assert.Equal("#FF00FFFF", l.Color));
        Assert.All(p.Layers, l => Assert.Equal(0.8, l.Opacity, 3));
        Assert.Equal((7.0, -3.0, 1), (d.Overlay.PositionX, d.Overlay.PositionY, d.Overlay.MonitorIndex));
        if (style is "Cross" or "T" or "CrossDot") Assert.Equal((12.0, 3.0, 5.0), (p.Layers[0].Length, p.Layers[0].Thickness, p.Layers[0].Gap));
        Assert.Equal(noTop, !p.Layers[0].Top);
        if (style == "Circle") Assert.Equal(17, p.Layers[0].Size); // radius = size + gap, as before
    }

    [Fact]
    public void Saved_profiles_survive_a_restart_and_a_damaged_file_is_kept()
    {
        var d = CrosshairStore.Load(new CrosshairSettings(), out _);
        var p = CrosshairTemplates.Create("Circle + dot");
        p.Name = "Second";
        p.Firing.Enabled = true;
        d.Profiles.Add(p);
        d.ActiveId = p.Id;
        d.Overlay.ToggleHotkey = "Ctrl+Shift+F9";
        d.Games.Add(new CrosshairGameRule { Exe = "Game.exe", ProfileId = p.Id });
        CrosshairStore.Save(d);

        var again = CrosshairStore.Load(new CrosshairSettings { Style = "Dot" }, out var notice);
        Assert.Null(notice);
        Assert.Equal(2, again.Profiles.Count);
        Assert.Equal("Second", again.Active.Name);
        Assert.True(again.Active.Firing.Enabled);
        Assert.Equal(LayerKind.Circle, again.Active.Layers[0].Kind);
        Assert.Equal("Ctrl+Shift+F9", again.Overlay.ToggleHotkey);
        Assert.Equal(p.Id, again.RuleFor("game")!.ProfileId);

        File.WriteAllText(CrosshairStore.FilePath, "{ not json");
        var recovered = CrosshairStore.Load(new CrosshairSettings { Style = "Dot" }, out notice);
        Assert.NotNull(notice);
        Assert.Equal(LayerKind.Dot, Assert.Single(recovered.Profiles).Layers[0].Kind);
        Assert.Single(Directory.GetFiles(CrosshairStore.Folder, "crosshair.json.damaged-*"));
    }

    [Fact]
    public void Share_code_round_trips_and_bad_input_is_rejected()
    {
        var p = CrosshairTemplates.Create("Cross + dot");
        p.Name = "Pro";
        p.Layers[0].Rotation = 45;
        p.Firing = new FiringAnimation { Enabled = true, Spread = 9, Trigger = FireTrigger.EitherMouse };
        var code = CrosshairSharing.ToCode(p);
        Assert.StartsWith("MTX1:", code);
        Assert.True(code.Length < 1000, code.Length.ToString());
        var back = CrosshairSharing.Import(code).Profile;
        Assert.Equal("Pro", back.Name);
        Assert.Equal(45, back.Layers[0].Rotation);
        Assert.Equal(9, back.Firing.Spread);
        Assert.Equal(FireTrigger.EitherMouse, back.Firing.Trigger);

        Assert.Throws<InvalidDataException>(() => CrosshairSharing.Import("hello"));
        Assert.Throws<InvalidDataException>(() => CrosshairSharing.Import("MTX1:@@@@"));
        Assert.Throws<InvalidDataException>(() => CrosshairSharing.Import(code[..(code.Length / 2)]));
        Assert.Throws<InvalidDataException>(() => CrosshairSharing.Import("{\"format\":\"something-else\"}"));
    }

    [Fact]
    public void Imported_values_are_clamped()
    {
        var p = Cross();
        p.Name = new string('x', 500) + "\u0007";
        p.Layers[0].Thickness = 1e9;
        p.Layers[0].Gap = double.NaN;
        p.Layers[0].Color = "not a colour";
        p.Layers[0].ImageFile = "..\\..\\evil.png";
        for (int i = 0; i < 100; i++) p.Layers.Add(new CrosshairLayer());
        var back = CrosshairSharing.Import(CrosshairSharing.ToFile(p)).Profile;
        Assert.Equal(CrosshairSanitizer.MaxName, back.Name.Length);
        Assert.Equal(CrosshairSanitizer.MaxLayers, back.Layers.Count);
        Assert.Equal(50, back.Layers[0].Thickness);
        Assert.Equal(4, back.Layers[0].Gap);
        Assert.Equal("#FF2BE38B", back.Layers[0].Color);
        Assert.Null(back.Layers[0].ImageFile);
    }

    private static byte[] Png(int w, int h, byte colorType, bool trns = false)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            ms.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, colorType, 0, 0, 0]);
        if (trns) Chunk("tRNS", [0, 0, 0, 0, 0, 0]);
        Chunk("IDAT", [0]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    [Fact]
    public void Png_images_are_checked_stored_and_travel_with_exports()
    {
        Assert.True(CrosshairImages.Inspect(Png(64, 32, 6), out _)!.HasAlpha);
        Assert.False(CrosshairImages.Inspect(Png(64, 32, 2), out _)!.HasAlpha);
        Assert.True(CrosshairImages.Inspect(Png(64, 32, 2, trns: true), out _)!.HasAlpha);
        Assert.Null(CrosshairImages.Inspect(Encoding.ASCII.GetBytes("GIF89a not a png at all......................"), out var why));
        Assert.Contains("PNG", why);
        Assert.Null(CrosshairImages.Inspect(Png(5000, 10, 6), out why));

        var name = CrosshairImages.Store(Png(40, 20, 6), out var info);
        Assert.True(CrosshairImages.IsValidName(name) && CrosshairImages.Exists(name));
        Assert.Equal((40, 20), (info.Width, info.Height));
        var p = new CrosshairProfile { Name = "Img" };
        p.Layers.Add(new CrosshairLayer { Kind = LayerKind.Image, ImageFile = name, Size = 40, ImageAspect = 0.5 });
        var shape = CrosshairGeometry.Build(p)[0].Shapes[0];
        Assert.Equal((ShapeKind.Image, 40.0, 20.0), (shape.Kind, shape.W, shape.H));

        var file = CrosshairSharing.ToFile(p);
        var code = CrosshairSharing.ToCode(p);
        File.Delete(CrosshairImages.PathOf(name)); // as if on another PC
        var imported = CrosshairSharing.Import(file);
        Assert.Empty(imported.Notes);
        Assert.True(CrosshairImages.Exists(imported.Profile.Layers[0].ImageFile!));
        File.Delete(CrosshairImages.PathOf(name));
        Assert.True(CrosshairImages.Exists(CrosshairSharing.Import(code).Profile.Layers[0].ImageFile!)); // small images fit in codes
    }

    [Fact]
    public void Undo_and_redo_step_through_edits_per_profile()
    {
        var h = new CrosshairHistory();
        var p = Cross(gap: 4);
        h.Track(p);
        Assert.False(h.CanUndo(p));
        p.Layers[0].Gap = 6; h.Commit(p);
        p.Layers[0].Gap = 8; h.Commit(p);
        Assert.False(h.Commit(p)); // nothing new
        var u1 = h.Undo(p)!;
        Assert.Equal(6, u1.Layers[0].Gap);
        var u2 = h.Undo(u1)!;
        Assert.Equal(4, u2.Layers[0].Gap);
        Assert.Null(h.Undo(u2));
        var r = h.Redo(u2)!;
        Assert.Equal(6, r.Layers[0].Gap);
        r.Layers.Add(new CrosshairLayer()); // a new edit clears redo
        h.Commit(r);
        Assert.False(h.CanRedo(r));
        // An uncommitted edit is committed first, then undone.
        r.Layers[0].Gap = 20;
        var u3 = h.Undo(r)!;
        Assert.Equal((6.0, 2), (u3.Layers[0].Gap, u3.Layers.Count));
        Assert.Equal(20, h.Redo(u3)!.Layers[0].Gap);
    }

    [Fact]
    public void Profiles_get_unique_names_and_copies_get_new_ids()
    {
        var d = new CrosshairData();
        d.Profiles.Add(new CrosshairProfile { Name = "Main" });
        Assert.Equal("Main 2", d.UniqueName("Main"));
        d.Profiles.Add(new CrosshairProfile { Name = "Main 2" });
        Assert.Equal("Main 3", d.UniqueName("Main 2"));
        var copy = d.Profiles[0].Copy("Copy");
        Assert.NotEqual(d.Profiles[0].Id, copy.Id);
        Assert.Equal("Main", d.Active.Name); // falls back to the first profile
        Assert.Equal("game.exe", CrosshairData.NormalizeExe(@"C:\x\game"));
    }
}
