using MoneyTweaks.Core.Features;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class NvidiaPresetRuleTests
{
    [Fact]
    public void Legacy_preset_has_exact_guide_values()
    {
        var lod = NvidiaProfilePresets.Legacy.Items.Single(i => i.ExpectedId == 0x00738E8F);
        Assert.Equal(0x00000020u, lod.Value);
        Assert.True(lod.Mandatory);
        Assert.Equal("0x00000020", NvidiaProfilePresets.Hex(lod.Value));
        Assert.DoesNotContain(NvidiaProfilePresets.Legacy.Items, i => i.ExpectedId == 0x00738E8F && i.Value is 0x45 or 0x78);
        uint V(uint id) => NvidiaProfilePresets.Legacy.Items.Single(i => i.ExpectedId == id).Value;
        Assert.Equal(0x8u, V(0x10D48A85));   // AA_MODE_REPLAY_MODE_ALL
        Assert.Equal(1u, V(0x00638E8F));     // driver controlled LOD bias On (guide)
        Assert.Equal(0x14u, V(0x00CE2691));  // high performance
        Assert.Equal(1u, V(0x0084CD70));
        Assert.Equal(1u, V(0x00E73211));
        Assert.Equal(0u, V(0x101E61A9));     // Off [Point]
        Assert.Equal(1u, V(0x20C1221E));
        Assert.Equal(1u, V(0x1057EB71));     // prefer maximum performance
    }

    [Fact]
    public void Nip_round_trip_keeps_0x20_exact()
    {
        var nip = NvidiaProfilePresets.BuildNip("Fortnite", "FortniteClient-Win64-Shipping.exe",
            [new(0x00738E8F, "Texture filtering - LOD Bias", 0, false, 0x20, null), new(0x00A879CF, "Vertical Sync", 0, false, 0x08416747, null), new(0x12345678, "Some string", 3, false, 0, "abc")]);
        Assert.Contains("<SettingID>7573135</SettingID>", nip);
        Assert.Contains("<SettingValue>32</SettingValue>", nip);           // .nip stores DWORDs in decimal; 32 == 0x00000020
        Assert.Contains("fortniteclient-win64-shipping.exe", nip);
        var (profile, exes, settings) = NvidiaProfilePresets.ParseNip(nip);
        Assert.Equal("Fortnite", profile);
        Assert.Single(exes);
        Assert.Equal(0x20u, settings.Single(s => s.Id == 0x00738E8F).Value);
        Assert.Equal(0x08416747u, settings.Single(s => s.Id == 0x00A879CF).Value);
        Assert.Equal("abc", settings.Single(s => s.Id == 0x12345678).Text);
    }

    [Fact]
    public void Resolution_uses_driver_metadata_and_rejects_mismatches()
    {
        var lod = NvidiaProfilePresets.Legacy.Items.Single(i => i.ExpectedId == 0x00738E8F);
        IReadOnlyList<uint> Range(uint _) => [0xFFFFFF80, 0x00000080];
        Assert.Null(NvidiaProfilePresets.Resolve(lod, _ => 0x00738E8F, _ => "Texture filtering - LOD Bias", Range).problem);
        Assert.Equal(NvItemStatus.Unsupported, NvidiaProfilePresets.Resolve(lod, _ => null, _ => null, Range).problem);
        Assert.Equal(NvItemStatus.Rejected, NvidiaProfilePresets.Resolve(lod, _ => 0x11111111, _ => "Texture filtering - LOD Bias", Range).problem);
        Assert.Equal(NvItemStatus.Rejected, NvidiaProfilePresets.Resolve(lod, _ => 0x00738E8F, _ => "LOD", Range).problem);
        Assert.Equal(NvItemStatus.Rejected, NvidiaProfilePresets.Resolve(lod, _ => 0x00738E8F, _ => "Texture filtering - LOD Bias", _ => [0xFFFFFFF0, 0x00000010]).problem);
        var ts = NvidiaProfilePresets.Legacy.Items.Single(i => i.ExpectedId == 0x10D48A85);
        Assert.Equal(NvItemStatus.Unsupported, NvidiaProfilePresets.Resolve(ts, _ => 0x10D48A85, _ => ts.DriverName, _ => [0, 1, 2]).problem);
    }

    [Fact]
    public void Rendering_mode_decides_whether_lod_bias_can_apply()
    {
        Assert.Equal(RenderSupport.Supported, NvidiaProfilePresets.RenderingMode("[x]\nPreferredRHI=dx11\nPreferredFeatureLevel=es31\n").support);
        Assert.Equal(RenderSupport.Supported, NvidiaProfilePresets.RenderingMode("PreferredRHI=dx11\nPreferredFeatureLevel=sm5\n").support);
        Assert.Equal(RenderSupport.LikelyIgnored, NvidiaProfilePresets.RenderingMode("PreferredRHI=dx12\n").support);
        Assert.Equal(RenderSupport.Unknown, NvidiaProfilePresets.RenderingMode("Foo=1\n").support);
        Assert.Equal(RenderSupport.Unknown, NvidiaProfilePresets.RenderingMode(null).support);
    }
}

/// <summary>
/// Real NVIDIA driver import and rollback on a throwaway profile shaped like the user's Fortnite profile (unrelated V-Sync
/// and pre-rendered-frame settings plus an earlier +0.5 LOD bias). Opt-in: MT_NVIDIA_WRITE_TESTS=1. Never touches Fortnite.
/// </summary>
[Collection("registry")]
public class NvidiaPresetWriteTests : IDisposable
{
    private const string Exe = "MoneyTweaksDrsSelfTest3.exe", Name = "Money Tweaks self-test 3 (delete me)";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-nvp-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;
    public NvidiaPresetWriteTests(ITestOutputHelper output)
    {
        this.output = output;
        MoneyTweaks.Core.Sys.AppPaths.OverrideRoot(_tmp);
    }
    public void Dispose()
    {
        try
        {
            if (NvApi.DriverPresent)
            {
                using var s = new NvApi.Session();
                var p = s.FindProfile(Name);
                if (p != IntPtr.Zero) { s.DeleteProfile(p); s.Save(); }
            }
        }
        catch { }
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact]
    public void Legacy_import_verify_restore_reset()
    {
        if (!(NvApi.DriverPresent && Environment.GetEnvironmentVariable("MT_NVIDIA_WRITE_TESTS") == "1")) { output.WriteLine("Skipped: needs an NVIDIA driver and MT_NVIDIA_WRITE_TESTS=1."); return; }
        using (var s = new NvApi.Session())
        {
            var p = s.CreateProfileWithApp(Name, Exe, "self-test");
            s.Set(p, 0x00A879CF, 0x08416747); // Vertical Sync (unrelated, like the user's profile)
            s.Set(p, 0x007BA09E, 1);          // Maximum pre-rendered frames (unrelated)
            s.Set(p, 0x00738E8F, 4);          // earlier +0.5 LOD bias
            s.Set(p, 0x00638E8F, 0);          // earlier driver-controlled LOD bias Off
            s.Save();
        }
        var state = new NvProfileState();
        var r = NvidiaProfilePresets.Apply(NvidiaProfilePresets.Legacy, state, Exe);
        output.WriteLine($"apply ok={r.Ok}: {r.Summary}\n method: {r.Method}\n render: {r.RenderText}\n display: {r.Display}");
        foreach (var i in r.Items) output.WriteLine($"  {i.Item.Field}: {i.Status} · {i.Detail} · before {(i.Before is { } b ? NvidiaProfilePresets.Hex(b) : "-")} after {(i.After is { } a ? NvidiaProfilePresets.Hex(a) : "-")}");
        foreach (var u in r.UnrelatedChanges) output.WriteLine("  UNRELATED: " + u);
        Assert.True(r.Ok, r.Summary);
        Assert.Empty(r.UnrelatedChanges);
        Assert.Equal(NvItemStatus.Applied, r.Items.Single(i => i.Item.ExpectedId == 0x00738E8F).Status);
        Assert.Equal(0x20u, r.Items.Single(i => i.Item.ExpectedId == 0x00738E8F).After);
        Assert.True(File.Exists(r.BackupNip));
        Assert.Equal(4u, NvidiaProfilePresets.ParseNip(File.ReadAllText(r.BackupNip!)).settings.Single(x => x.Id == 0x00738E8F).Value);

        var v = NvidiaProfilePresets.Verify(NvidiaProfilePresets.Legacy, Exe);
        output.WriteLine("verify: " + v.Summary);
        Assert.True(v.Ok, v.Summary);

        var snap = NvidiaProfilePresets.LoadSnapshot(state.LastBackupJson)!;
        var rl = NvidiaProfilePresets.RestoreSnapshot(snap, Exe);
        output.WriteLine("restore:\n  " + string.Join("\n  ", rl));
        using (var s = new NvApi.Session())
        {
            var p = s.FindProfileForApp(Exe);
            Assert.Equal(4u, s.Get(p, 0x00738E8F)!.Current);
            Assert.Equal(0u, s.Get(p, 0x00638E8F)!.Current);
            Assert.Equal(0x08416747u, s.Get(p, 0x00A879CF)!.Current);
            Assert.False(s.Get(p, 0x10D48A85) is { InProfile: true });
        }

        var reset = NvidiaProfilePresets.Reset(Exe);
        output.WriteLine("reset:\n  " + string.Join("\n  ", reset));
        using (var s = new NvApi.Session())
        {
            var p = s.FindProfileForApp(Exe);
            Assert.False(s.Get(p, 0x00738E8F) is { InProfile: true });
            Assert.Equal(0x08416747u, s.Get(p, 0x00A879CF)!.Current); // unrelated setting survives reset
        }
    }
}

/// <summary>Read-only: NVIDIA Profile Inspector's own metadata agrees with every Legacy field (skips if Inspector isn't on this PC).</summary>
public class InspectorMetadataTests(ITestOutputHelper output)
{
    [Fact]
    public void Legacy_ids_match_inspector_metadata()
    {
        var exe = NvidiaProfilePresets.FindInspector(null);
        if (exe is null) { output.WriteLine("NVIDIA Profile Inspector not found; skipped."); return; }
        var meta = InspectorMetadata.Load(exe);
        output.WriteLine($"{exe}: {meta.Count} settings");
        foreach (var i in NvidiaProfilePresets.Legacy.Items)
        {
            output.WriteLine($"  {NvidiaProfilePresets.Hex(i.ExpectedId)}: {InspectorMetadata.Describe(i, meta)}");
            Assert.True(meta.ContainsKey(i.ExpectedId), i.Field);
        }
        Assert.Contains("LOD Bias (DX)", meta[0x00738E8F].Name);
        Assert.Equal("High performance", meta[0x00CE2691].Values[0x14]);
        Assert.Equal("Off [Point]", meta[0x101E61A9].Values[0]);
        Assert.False(meta[0x00738E8F].Values.ContainsKey(0x20)); // +4.0 is entered as hex, not a named Inspector value
    }
}
