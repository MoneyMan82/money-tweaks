using MoneyTweaks.Core.Health;

namespace MoneyTweaks.Tests;

/// <summary>Crash Detective's analysis over event records as Windows logs them (several copied from a real PC).</summary>
public class CrashDetectiveTests
{
    private static RawEvent Ev(string time, string log, string provider, int id, int level, string[] data, (string k, string v)[]? named = null, string? msg = null) =>
        new(DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture), log, provider, id, level, data,
            (named ?? []).ToDictionary(n => n.k, n => n.v, StringComparer.OrdinalIgnoreCase), msg);

    private static CrashReport Run(IEnumerable<RawEvent> events, IEnumerable<PcChange>? changes = null, IEnumerable<DumpFile>? dumps = null, int? dumpSetting = 7) =>
        CrashDetective.Analyze(new CrashInputs(events.ToList(), (dumps ?? []).ToList(), (changes ?? []).ToList(), 5.1, 3.7, dumpSetting, ["test"], [], 30),
            new DateTime(2026, 10, 25, 16, 0, 0));

    // The two blue screens on the test PC: WER 1001 + 1019 (related driver) + Kernel-Power 41 + EventLog 6008 per crash.
    private static IEnumerable<RawEvent> NdisCrash(string t, string kp41, string dump) =>
    [
        Ev(kp41, "System", "Microsoft-Windows-Kernel-Power", 41, 1, ["80", "1", "0", "1", "2", "0", "0", "0"], [("BugcheckCode", "80"), ("PowerButtonTimestamp", "0")]),
        Ev(t, "System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 2, ["0x00000050 (0xffff878fbafeb1d0, 0x0000000000000000, 0xfffff8012a9a9200, 0x0000000000000002)", dump, "656ff566-a982-47c4-94e9-21fc50720524"]),
        Ev(t, "System", "Microsoft-Windows-WER-SystemErrorReporting", 1019, 2, ["ndis.sys"]),
        Ev(t, "System", "EventLog", 6008, 2, ["19:22:26", "23/10/2026"]),
    ];

    [Fact]
    public void Repeated_blue_screens_become_one_incident_each_with_facts_and_a_moderate_lead()
    {
        var r = Run(NdisCrash("2026-10-18 20:41:09", "2026-10-18 20:40:59", @"C:\WINDOWS\Minidump\101826-11875-01.dmp")
            .Concat(NdisCrash("2026-10-23 19:23:20", "2026-10-23 19:23:10", @"C:\WINDOWS\Minidump\102326-11828-01.dmp")));
        Assert.Equal(2, r.Incidents.Count);
        var i = r.Incidents[0];
        Assert.Equal(CrashKind.BlueScreen, i.Kind);
        Assert.Equal("ndis.sys", i.App);
        Assert.Contains(i.Facts, f => f.Text.Contains("PAGE_FAULT_IN_NONPAGED_AREA") && f.Source.Contains("WER-SystemErrorReporting 1001"));
        Assert.Contains(i.Facts, f => f.Text == "Windows names a possibly related driver: ndis.sys");
        Assert.Contains(i.Facts, f => f.Text.Contains("Minidump"));
        Assert.Contains(i.Facts, f => f.Source.Contains("Kernel-Power 41"));   // same crash, not a separate incident
        var h = i.Hypotheses[0];
        Assert.Equal(Confidence.Moderate, h.Confidence);
        Assert.Contains("networking stack", h.Cause);
        Assert.Contains(h.Evidence, e => e.Contains("2 times"));
        Assert.Equal("link:devmgmt.msc", h.Fixes[0].Action);
        Assert.Contains(r.Patterns, p => p.Contains("2 blue screens") && p.Contains("ndis.sys"));
    }

    [Fact]
    public void A_single_blue_screen_with_a_named_driver_is_only_a_weak_lead()
    {
        var r = Run(NdisCrash("2026-10-23 19:23:20", "2026-10-23 19:23:10", "x.dmp"));
        Assert.Equal(Confidence.Weak, Assert.Single(r.Incidents).Hypotheses[0].Confidence);
    }

    [Fact]
    public void Restarts_without_a_stop_code_say_what_was_recorded_and_no_more()
    {
        var held = Run([Ev("2026-10-06 20:53:52", "System", "Microsoft-Windows-Kernel-Power", 41, 1, ["0", "0"], [("BugcheckCode", "0"), ("LongPowerButtonPressDetected", "true")])]);
        var i = Assert.Single(held.Incidents);
        Assert.Equal(CrashKind.UnexpectedRestart, i.Kind);
        Assert.Contains(i.Facts, f => f.Text.Contains("long press of the power button"));
        Assert.Contains("power button", i.Hypotheses[0].Cause);

        var silent = Run([Ev("2026-10-05 08:29:28", "System", "Microsoft-Windows-Kernel-Power", 41, 1, ["0", "0"], [("BugcheckCode", "0"), ("PowerButtonTimestamp", "0")])]);
        var h = Assert.Single(silent.Incidents).Hypotheses[0];
        Assert.Equal(Confidence.Weak, h.Confidence);
        Assert.Contains("can't tell these apart", h.Caveat);
    }

    [Fact]
    public void Kernel_Power_41_with_a_stop_code_and_no_report_is_still_a_blue_screen()
    {
        var r = Run([Ev("2026-10-05 09:54:54", "System", "Microsoft-Windows-Kernel-Power", 41, 1, ["159", "4"], [("BugcheckCode", "159")])]);
        var i = Assert.Single(r.Incidents);
        Assert.Equal(CrashKind.BlueScreen, i.Kind);
        Assert.Contains("DRIVER_POWER_STATE_FAILURE", i.Title);
        Assert.Contains("sleep", i.Hypotheses[0].Cause);
    }

    [Fact]
    public void A_game_crash_in_the_gpu_driver_next_to_a_driver_reset_points_at_the_gpu()
    {
        var r = Run(
        [
            Ev("2026-10-20 21:00:10", "System", "Display", 4101, 3, ["nvlddmkm"]),
            Ev("2026-10-20 21:00:30", "Application", "Application Error", 1000, 2, ["FortniteClient-Win64-Shipping.exe", "++Fortnite+Release-32.10", "x", "nvwgf2umx.dll", "32.0.15.6094", "y", "c0000005", "0000000000a1b2c3"]),
        ], dumps: [new(@"C:\Users\x\AppData\Local\CrashDumps\FortniteClient-Win64-Shipping.exe.1234.dmp", DateTime.Parse("2026-10-20 21:00:31"), 50_000_000)]);
        var crash = r.Incidents.First(i => i.Kind == CrashKind.AppCrash);
        Assert.Contains(crash.Facts, f => f.Text == "The GPU driver was reset nearby");
        Assert.Contains(crash.Facts, f => f.Text.StartsWith("A crash dump was saved"));
        var h = crash.Hypotheses[0];
        Assert.Equal(Confidence.Moderate, h.Confidence);
        Assert.Contains("graphics driver", h.Cause);
        Assert.Contains(r.Incidents, i => i.Kind == CrashKind.GpuDriverReset);
    }

    [Fact]
    public void Overlay_modules_and_hardware_errors_are_named_with_their_evidence()
    {
        var overlay = Run([Ev("2026-10-20 21:00:30", "Application", "Application Error", 1000, 2, ["game.exe", "1.0", "x", "GameOverlayRenderer64.dll", "1.0", "y", "c0000005", "1"])]);
        Assert.Contains("Steam overlay", overlay.Incidents[0].Hypotheses[0].Cause, StringComparison.OrdinalIgnoreCase);

        var whea = Run(
        [
            Ev("2026-10-20 21:00:00", "System", "Microsoft-Windows-WHEA-Logger", 18, 2, [], msg: "A fatal hardware error has occurred. Component: Processor Core"),
            Ev("2026-10-20 21:01:00", "System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 2, ["0x00000124 (0x0, 0x1, 0x2, 0x3)", "", ""]),
        ]);
        var bsod = whea.Incidents.First(i => i.Kind == CrashKind.BlueScreen);
        Assert.Contains(bsod.Facts, f => f.Text.Contains("Processor Core"));
        Assert.Equal(Confidence.Strong, bsod.Hypotheses.First(h => h.Cause.StartsWith("Hardware instability")).Confidence);
    }

    [Fact]
    public void Nothing_logged_means_no_incidents_and_an_honest_note()
    {
        var r = Run([]);
        Assert.Empty(r.Incidents);
        Assert.Contains(r.Notes, n => n.StartsWith("No crashes"));
        Assert.Contains(r.Notes, n => n.Contains("Some crashes leave no trace"));
    }

    [Fact]
    public void Changes_are_leads_only_for_repeating_problems_and_only_when_they_can_matter()
    {
        var crashes = new[] { "2026-10-20 21:00:00", "2026-10-21 21:00:00" }.Select(t =>
            Ev(t, "Application", "Application Error", 1000, 2, ["game.exe", "1.0", "x", "game.exe", "1.0", "y", "c0000005", "1"])).ToList();
        PcChange[] changes =
        [
            new(DateTime.Parse("2026-10-20 11:00"), "Money Tweaks tweak applied", "Hardware-accelerated GPU scheduling", "adv.hags"),
            new(DateTime.Parse("2026-10-20 12:00"), "Money Tweaks tweak applied", "Disable telemetry", "dbl.telemetry"),
            new(DateTime.Parse("2026-10-20 13:00"), "Windows update installed", "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602"),
        ];
        var r = Run(crashes, changes);
        var first = r.Incidents.Last();
        var lead = Assert.Single(first.Hypotheses, h => h.Cause.StartsWith("Changed shortly before"));
        Assert.Contains("GPU scheduling", lead.Cause);
        Assert.Equal("undo-tweak:adv.hags", lead.Fixes[0].Action);
        Assert.Equal(Confidence.Weak, lead.Confidence);
        Assert.DoesNotContain(r.Incidents[0].Hypotheses, h => h.Cause.StartsWith("Changed shortly before"));   // only on the first
        Assert.DoesNotContain(Run(crashes.Take(1), changes).Incidents[0].Hypotheses, h => h.Cause.StartsWith("Changed shortly before"));   // one-off: no timing leads
    }

    [Fact]
    public void Driver_installs_and_updates_are_read_from_the_log()
    {
        var c = CrashDetective.ChangesFromEvents(
        [
            Ev("2026-10-21 18:39:10", "System", "Microsoft-Windows-UserPnp", 20003, 4, ["wdm_usb", @"\SystemRoot\system32\DRIVERS\usb2ser.sys", @"USB\VID_0E8D&PID_0003\6&1B2B7D3C&0&1"]),
            Ev("2026-10-04 19:03:00", "System", "Microsoft-Windows-WindowsUpdateClient", 19, 4, ["x"], [("updateTitle", "Advanced Micro Devices, Inc. Display Driver Update (32.0.21042.62)")]),
        ]).ToList();
        Assert.Equal("Driver installed", c[0].Kind);
        Assert.Equal("wdm_usb (usb2ser.sys)", c[0].Text);
        Assert.Equal("Advanced Micro Devices, Inc. Display Driver Update (32.0.21042.62)", c[1].Text);
    }

    [Fact]
    public void Exports_are_redacted_but_keep_versions_and_codes()
    {
        const string text = @"C:\Users\antti\AppData\Local\CrashDumps\game.exe.1.dmp · user antti on DESKTOP-ABC123 · S-1-5-21-1111111111-2222222222-3333333333-1001 · me@example.com · 192.168.1.20 · 00:1A:2B:3C:4D:5E · report 656ff566-a982-47c4-94e9-21fc50720524 · USB\VID_0E8D&PID_0003\6&1B2B7D3C&0&1 · steamwebhelper.exe 10.96.30.42 crashed in KERNELBASE.dll 10.0.26100.9444 · 0x00000050";
        var r = CrashDetective.Redact(text, "antti", "DESKTOP-ABC123", @"C:\Users\antti");
        foreach (var secret in new[] { "antti", "DESKTOP-ABC123", "1111111111", "me@example.com", "192.168.1.20", "00:1A:2B", "656ff566", "1B2B7D3C" })
            Assert.DoesNotContain(secret, r);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\CrashDumps\game.exe.1.dmp", r);
        Assert.Contains("steamwebhelper.exe 10.96.30.42", r);
        Assert.Contains("KERNELBASE.dll 10.0.26100.9444", r);
        Assert.Contains("0x00000050", r);
        Assert.Contains(@"USB\VID_0E8D&PID_0003\[serial]", r);
    }
}
