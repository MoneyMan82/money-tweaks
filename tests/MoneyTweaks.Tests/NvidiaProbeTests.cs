using MoneyTweaks.Core.Features;
using Xunit.Abstractions;

namespace MoneyTweaks.Tests;

public class NvidiaRuleTests
{
    private static readonly List<uint> Lod = [0xFFFFFF80, 0x00000080];

    [Fact]
    public void Undocumented_and_unlisted_values_are_refused()
    {
        var lod = NvidiaFortnite.LodBiasDx;
        Assert.Contains("0x00000020", NvidiaFortnite.Refusal(lod, NvidiaFortnite.UndocumentedLodValue, lod.DriverName, Lod));
        Assert.Null(NvidiaFortnite.Refusal(lod, 3, lod.DriverName, Lod));   // +0.375
        Assert.Null(NvidiaFortnite.Refusal(lod, 4, lod.DriverName, Lod));   // +0.5
        Assert.NotNull(NvidiaFortnite.Refusal(lod, 4, lod.DriverName, [0xFFFFFFF8, 0x00000002])); // outside a narrower driver range
        var tri = NvidiaFortnite.TrilinearOpt;
        Assert.NotNull(NvidiaFortnite.Refusal(tri, 0xFFFFFFFF, tri.DriverName, [0, 1]));
        Assert.Null(NvidiaFortnite.Refusal(tri, 1, tri.DriverName, [0, 1]));
        Assert.NotNull(NvidiaFortnite.Refusal(tri, 1, tri.DriverName, [0, 2]));       // driver doesn't list it
        Assert.NotNull(NvidiaFortnite.Refusal(tri, 1, "Something else", [0, 1]));    // ID means something else on this driver
        Assert.NotNull(NvidiaFortnite.Refusal(NvidiaFortnite.LodBiasOgl, 0, "", []));  // OGL is never written
        Assert.Equal("+0.3750", NvidiaFortnite.Describe(lod, 3));
        Assert.Contains("not a Money Tweaks value", NvidiaFortnite.Describe(lod, 0x20));
    }

    [Fact]
    public void Presets_and_derived_driver_lod_setting()
    {
        var mild = NvidiaFortnite.Targets(NvidiaFortnite.Preset("Mild Potato"));
        Assert.Equal(0x14u, mild[NvidiaFortnite.QualityId]);
        Assert.Equal(3u, mild[NvidiaFortnite.LodBiasDxId]);
        Assert.Equal(0u, mild[NvidiaFortnite.DriverLodBiasId]);  // driver must stop controlling LOD bias or the value is ignored
        Assert.Equal(4u, NvidiaFortnite.Targets(NvidiaFortnite.Preset("Extreme Potato"))[NvidiaFortnite.LodBiasDxId]);
        var def = NvidiaFortnite.Targets(NvidiaFortnite.Preset("Default"));
        Assert.All(def.Values, v => Assert.Null(v));
        Assert.DoesNotContain(NvidiaFortnite.LodBiasOglId, def.Keys);
        // Every preset value is in the fixed list.
        foreach (var p in NvidiaFortnite.Presets)
            foreach (var (key, v) in NvidiaFortnite.Preset(p))
                if (v is { } x) Assert.Contains(NvidiaFortnite.Controls.Single(c => c.Key == key).Choices, c => c.Value == x);
    }

    [Fact]
    public void Plan_refuses_whole_apply_when_driver_reports_another_name()
    {
        var st = new NvidiaFortnite.Status(true, "", "Fortnite", false, [],
            NvidiaFortnite.Managed.ToDictionary(m => m.Id, m => ((string?)(m.Id == NvidiaFortnite.LodBiasDxId ? "Other" : m.DriverName), m.Id == NvidiaFortnite.LodBiasDxId ? Lod : new List<uint> { 0, 1, 0xA, 0x14, 0xFFFFFFF6 })));
        var plan = NvidiaFortnite.Plan(NvidiaFortnite.Preset("Mild Potato"), st);
        Assert.NotNull(plan.Single(p => p.Setting.Id == NvidiaFortnite.LodBiasDxId).Refused);
        Assert.Null(plan.Single(p => p.Setting.Id == NvidiaFortnite.QualityId).Refused);
        Assert.True(plan.Single(p => p.Setting.Id == NvidiaFortnite.QualityId).Changes);
    }
}

/// <summary>
/// Real driver round trip on a throwaway profile for a dummy executable (never Fortnite's profile): apply, read back,
/// restore, and the profile is deleted again. Needs administrator rights to save; reports that instead of passing silently.
/// </summary>
[Collection("registry")]
public class NvidiaWriteTests : IDisposable
{
    private const string DummyExe = "MoneyTweaksDrsSelfTest.exe";
    // Writes (to throwaway profiles only) run on request: set MT_NVIDIA_WRITE_TESTS=1 on a PC with an NVIDIA driver.
    private static bool Enabled => NvApi.DriverPresent && Environment.GetEnvironmentVariable("MT_NVIDIA_WRITE_TESTS") == "1";
    private const string Skip = "Skipped: needs an NVIDIA driver and MT_NVIDIA_WRITE_TESTS=1.";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "mt-nv-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper output;
    public NvidiaWriteTests(ITestOutputHelper output)
    {
        this.output = output;
        MoneyTweaks.Core.Sys.AppPaths.OverrideRoot(_tmp);
    }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void Apply_verify_restore_on_scratch_profile()
    {
        if (!Enabled) { output.WriteLine(Skip); return; }
        var state = new NvFortniteState();
        (bool ok, string msg, List<string> log) r;
        try { r = NvidiaFortnite.Apply(NvidiaFortnite.Preset("Extreme Potato"), state, DummyExe); }
        catch (NvApiException ex) when (ex.Status == NvApi.InvalidUserPrivilege)
        {
            output.WriteLine("Not elevated: the driver refused to save (" + ex.Message + "). Nothing was written.");
            using var s = new NvApi.Session();
            Assert.Equal(IntPtr.Zero, s.FindProfileForApp(DummyExe)); // unsaved session left nothing behind
            return;
        }
        output.WriteLine($"apply: {r.ok} {r.msg}\n  " + string.Join("\n  ", r.log));
        Assert.True(r.ok, r.msg);
        Assert.True(state.Backup!.CreatedProfile);
        Assert.Empty(NvidiaFortnite.Verify(NvidiaFortnite.Targets(NvidiaFortnite.Preset("Extreme Potato")), DummyExe));
        var rr = NvidiaFortnite.Restore(state, DummyExe);
        output.WriteLine($"restore: {rr.ok} {rr.message}\n  " + string.Join("\n  ", rr.log));
        Assert.True(rr.ok, rr.message);
        using var after = new NvApi.Session();
        Assert.Equal(IntPtr.Zero, after.FindProfileForApp(DummyExe));
    }

    [Fact]
    public void Existing_profile_original_values_come_back_exactly()
    {
        if (!Enabled) { output.WriteLine(Skip); return; }
        const string exe = "MoneyTweaksDrsSelfTest2.exe", name = "Money Tweaks self-test (delete me)";
        try
        {
            // An existing profile like the user's, with an earlier manual edit: LOD bias 0x20 and Quality = Quality.
            using (var s = new NvApi.Session())
            {
                var p = s.CreateProfileWithApp(name, exe, "self-test");
                s.Set(p, NvidiaFortnite.LodBiasDxId, 0x20);
                s.Set(p, NvidiaFortnite.QualityId, 0);
                s.Save();
            }
            var state = new NvFortniteState();
            var r = NvidiaFortnite.Apply(NvidiaFortnite.Preset("Mild Potato"), state, exe);
            output.WriteLine($"apply: {r.ok} {r.message}\n  " + string.Join("\n  ", r.log));
            Assert.True(r.ok, r.message);
            Assert.False(state.Backup!.CreatedProfile);
            Assert.Contains(NvidiaFortnite.UnusualBackupValues(state.Backup), v => v.Contains("0x00000020"));
            var rr = NvidiaFortnite.Restore(state, exe);
            output.WriteLine($"restore: {rr.ok} {rr.message}\n  " + string.Join("\n  ", rr.log));
            Assert.True(rr.ok, rr.message);
            using var check = new NvApi.Session();
            var prof = check.FindProfileForApp(exe);
            Assert.Equal(0x20u, check.Get(prof, NvidiaFortnite.LodBiasDxId)!.Current);
            Assert.Equal(0u, check.Get(prof, NvidiaFortnite.QualityId)!.Current);
            Assert.False(check.Get(prof, NvidiaFortnite.AnisoSampleOptId) is { InProfile: true });
        }
        catch (NvApiException ex) when (ex.Status == NvApi.InvalidUserPrivilege) { output.WriteLine("Not elevated; skipped: " + ex.Message); }
        finally
        {
            using var s = new NvApi.Session();
            var p = s.FindProfile(name);
            if (p != IntPtr.Zero) { s.DeleteProfile(p); s.Save(); }
            Assert.Equal(IntPtr.Zero, s.FindProfileForApp(exe));
        }
    }
}

/// <summary>Read-only: prints what this PC's NVIDIA driver reports. Never writes. Skips when there's no NVIDIA driver.</summary>
public class NvidiaProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void Probe_legacy_guide_settings_read_only()
    {
        if (!NvApi.DriverPresent) { output.WriteLine("No NVIDIA driver; skipped."); return; }
        foreach (var name in new[] { "Antialiasing - Transparency Supersampling", "Texture filtering - LOD Bias", "Texture filtering - LOD Bias (DX)", "Texture filtering - Driver Controlled LOD Bias",
                     "Texture filtering - Quality", "Texture filtering - Anisotropic filter optimization", "Texture filtering - Anisotropic sample optimization",
                     "Anisotropic filtering setting", "Anisotropic filtering mode", "Threaded optimization", "Power management mode", "Multi-display/mixed-GPU acceleration" })
        {
            var id = NvApi.SettingIdFromName(name);
            var (def, vals) = id is { } i ? NvApi.SettingValues(i) : (null, []);
            output.WriteLine($"'{name}' -> {(id is { } x ? "0x" + x.ToString("X8") : "UNKNOWN")} default={(def is { } d ? "0x" + d.ToString("X8") : "-")} values=[{string.Join(", ", vals.Select(v => "0x" + v.ToString("X8")))}]");
        }
        using var s = new NvApi.Session();
        var p = s.FindProfileForApp("FortniteClient-Win64-Shipping.exe");
        if (p == IntPtr.Zero) { output.WriteLine("no Fortnite profile"); return; }
        output.WriteLine($"Fortnite profile '{s.ProfileName(p)}' globalOrBase={s.IsGlobalOrBase(p)}");
        foreach (var x in s.EnumSettings(p))
            output.WriteLine($"  0x{x.Id:X8} type={x.Type} loc={x.Location} predef={x.IsCurrentPredefined} value=0x{x.Value:X8}{(x.Text is null ? "" : " '" + x.Text + "'")} {x.Name}");
    }

    [Fact]
    public void Probe_driver_settings_read_only()
    {
        if (!NvApi.DriverPresent) { output.WriteLine("No NVIDIA driver; skipped."); return; }
        foreach (var id in new uint[] { 0x00CE2691, 0x00E73211, 0x002ECAF2, 0x0019BB68, 0x00638E8F, 0x00738E8F, 0x20403F79, 0x0084CD70 })
        {
            var (def, vals) = NvApi.SettingValues(id);
            output.WriteLine($"0x{id:X8} name='{NvApi.SettingName(id)}' default={(def is { } d ? "0x" + d.ToString("X8") : "-")} values=[{string.Join(", ", vals.Select(v => "0x" + v.ToString("X8")))}]");
        }
        using var s = new NvApi.Session();
        var p = s.FindProfileForApp("FortniteClient-Win64-Shipping.exe");
        output.WriteLine(p == IntPtr.Zero ? "No profile lists FortniteClient-Win64-Shipping.exe" : $"Fortnite profile: '{s.ProfileName(p)}'");
        if (p == IntPtr.Zero) return;
        foreach (var id in new uint[] { 0x00CE2691, 0x00E73211, 0x002ECAF2, 0x0019BB68, 0x00638E8F, 0x00738E8F, 0x20403F79 })
        {
            var st = s.Get(p, id);
            output.WriteLine(st is null ? $"0x{id:X8}: not set anywhere" : $"0x{id:X8}: current=0x{st.Current:X8} location={st.Location} isPredef={st.IsCurrentPredefined} predefValid={st.IsPredefinedValid} predef=0x{st.Predefined:X8}");
        }
    }
}
