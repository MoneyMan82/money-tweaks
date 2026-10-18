using System.Windows;
using System.Windows.Controls;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks.Views;

/// <summary>Controls that go through MSI Afterburner's documented control memory: offsets, undervolt curve, temperature
/// limit, fan and fan curve. Shown only with live values from Afterburner; otherwise an honest "start Afterburner" note.</summary>
public sealed partial class TuningViewAfterburner
{
    public static async Task RenderAsync(StackPanel host, TuningState st, TuningInventory? inv, bool nvidiaPowerHere, Func<Task> refresh)
    {
        host.Children.Clear();
        var g = await Task.Run(Afterburner.Read);
        if (g is null)
        {
            var tool = inv?.Tool("MSI Afterburner");
            host.Children.Add(Ui.Muted("MSI Afterburner isn't running, or Money Tweaks isn't running as administrator (Afterburner runs elevated, so its control memory is only open to elevated apps). " +
                "With it running, this section offers GPU core/memory clock offsets, a voltage/frequency undervolt, the temperature limit, fan control and a fan curve, and the CPU temperature appears everywhere in Money Tweaks.", 12));
            var row = Ui.Row();
            if (tool?.Path is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                row.Children.Add(Ui.Btn("Start MSI Afterburner", (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Dialog.Show("Couldn't start it", ex.Message, DialogKind.Error); } }, "BaseButton", new Thickness(0, 8, 8, 0)));
            else if (tool is not null)
                row.Children.Add(Ui.Btn("Get MSI Afterburner (official site) ↗", (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tool.Url) { UseShellExecute = true }); } catch { } }, "GhostButton", new Thickness(0, 8, 8, 0)));
            row.Children.Add(Ui.Btn("Check again", async (_, _) => await RenderAsync(host, st, inv, nvidiaPowerHere, refresh), "GhostButton", new Thickness(0, 8, 8, 0)));
            host.Children.Add(row);
            return;
        }

        host.Children.Add(Ui.Muted($"Connected to MSI Afterburner ({(g.GpuId.Length > 0 ? g.GpuId : "master GPU")}). Values below are what Afterburner reports right now; ranges are its own limits.", 11.5));
        var change = new Dictionary<string, Func<Afterburner.Change, Afterburner.Change>>();
        var resets = new List<Action>();

        void Slider(string label, string tip, AbRange r, string unit, Action<int> set)
        {
            if (!r.Adjustable) return;
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 6), ToolTip = tip };
            var head = new DockPanel();
            var val = Ui.Text($"{r.Cur:+0;-0;0}{unit}", 12.5, FontWeights.SemiBold); DockPanel.SetDock(val, Dock.Right); head.Children.Add(val);
            head.Children.Add(Ui.Text(label, 12.5));
            sp.Children.Add(head);
            var s = new Slider { Minimum = r.Min, Maximum = r.Max, Value = r.Cur, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true };
            s.ValueChanged += (_, e) => { int v = (int)e.NewValue; val.Text = $"{v:+0;-0;0}{unit}" + (v != r.Cur ? $"  (now {r.Cur:+0;-0;0}{unit})" : ""); set(v); };
            sp.Children.Add(s);
            sp.Children.Add(Ui.Muted($"Range {r.Min}–{r.Max}{unit}, step 1, default {r.Def}{unit}.", 11));
            resets.Add(() => s.Value = r.Cur);
            host.Children.Add(sp);
        }

        if (g.CoreOffset is { } core)
            Slider("Core clock offset", "Adds this many MHz to every point of the GPU's boost curve. Raise in small steps (+15…+30) and test; too high = crashes or artifacts.", core, " MHz",
                v => change["core"] = c => c with { CoreOffsetMhz = v == core.Cur ? null : v });
        if (g.MemOffset is { } mem)
            Slider("Memory clock offset", "Adds this many MHz to the video memory clock. GDDR6X can get slower (error correction) before it crashes: benchmark each step.", mem, " MHz",
                v => change["mem"] = c => c with { MemOffsetMhz = v == mem.Cur ? null : v });
        if (!nvidiaPowerHere && g.PowerLimit is { } pl)
            Slider("Power limit", "Percent of the card's default power limit.", pl, "%", v => change["pl"] = c => c with { PowerLimitPercent = v == pl.Cur ? null : v });
        if (g.ThermalLimit is { } tl)
            Slider("Temperature limit", "The GPU slows down to stay under this temperature.", tl, " °C", v => change["tl"] = c => c with { ThermalLimitC = v == tl.Cur ? null : v });

        // Fan: automatic or a fixed speed.
        if (g.Fan is { } fan)
        {
            var auto = new CheckBox { Content = $"Automatic fan control (now {(g.FanAuto ? "automatic" : $"fixed at {fan.Cur}%")})", IsChecked = g.FanAuto, Margin = new Thickness(0, 8, 0, 0) };
            host.Children.Add(auto);
            Slider("Fixed fan speed", "Used when automatic is off. Fan control stays in effect after Money Tweaks closes (Afterburner keeps it).", fan, "%", v => { if (auto.IsChecked != true) change["fan"] = c => c with { FanPercent = v, FanAuto = null }; });
            auto.Click += (_, _) => { if (auto.IsChecked == true) change["fan"] = c => c with { FanAuto = true, FanPercent = null }; else change.Remove("fan"); };
            resets.Add(() => auto.IsChecked = g.FanAuto);
        }

        host.Children.Add(Ui.Row(
            Ui.Btn("Apply…", async (_, _) =>
            {
                var c = change.Values.Aggregate(new Afterburner.Change(), (acc, f) => f(acc));
                if (c == new Afterburner.Change()) { MainWindow.Current?.ShowToast("Nothing changed."); return; }
                await ApplyAsync(c, st, refresh);
            }, "PrimaryButton", new Thickness(0, 10, 8, 0)),
            Ui.Btn("Cancel", (_, _) => { foreach (var r in resets) r(); change.Clear(); }, "BaseButton", new Thickness(0, 10, 8, 0))));

        // Undervolt (voltage/frequency curve).
        host.Children.Add(Ui.Text("Undervolt (voltage/frequency curve)", 13, FontWeights.SemiBold));
        if (!g.HasCurve) host.Children.Add(Ui.Muted("Afterburner doesn't expose this GPU's voltage/frequency curve.", 12));
        else
        {
            var points = g.Curve.Where(p => p.MilliVolts is >= 750 and <= 1100).GroupBy(p => p.MilliVolts).Select(x => x.First()).ToList();
            var vbox = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var p in points) vbox.Items.Add($"{p.MilliVolts} mV");
            vbox.SelectedIndex = Math.Max(0, points.FindIndex(p => p.MilliVolts >= 900));
            var mhz = new Slider { Minimum = 1200, Maximum = 3200, SmallChange = 15, LargeChange = 60, TickFrequency = 15, IsSnapToTickEnabled = true };
            var mhzText = Ui.Text("", 12.5, FontWeights.SemiBold);
            void Sync()
            {
                if (vbox.SelectedIndex < 0) return;
                var p = points[vbox.SelectedIndex];
                mhz.Minimum = Math.Max(300, p.BaseMhz - 300);
                mhz.Maximum = p.BaseMhz + (g.CoreOffset?.Max ?? 0);
                mhz.Value = Math.Min(mhz.Maximum, Math.Max(mhz.Minimum, p.FrequencyMhz));
            }
            mhz.ValueChanged += (_, e) => mhzText.Text = $"{e.NewValue:0} MHz";
            vbox.SelectionChanged += (_, _) => Sync();
            Sync();
            host.Children.Add(Ui.Muted($"Curve {(g.CurveEnabled ? "is customized" : "is at default")}. Pick a voltage and the clock the GPU should reach at it; every higher-voltage point is capped at that clock, so the GPU never asks for more voltage. Typical RTX 40 start: 2700 MHz at 950 mV, then test.", 11.5));
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(vbox);
            row.Children.Add(new Border { Width = 12 });
            row.Children.Add(mhzText);
            host.Children.Add(row);
            host.Children.Add(mhz);
            host.Children.Add(Ui.Row(
                Ui.Btn("Apply undervolt…", async (_, _) =>
                {
                    var p = points[vbox.SelectedIndex];
                    await ApplyAsync(new Afterburner.Change { Undervolt = (p.MilliVolts, (int)mhz.Value) }, st, refresh);
                }, "BaseButton", new Thickness(0, 8, 8, 0)),
                Ui.Btn("Reset curve to default", async (_, _) => await ApplyAsync(new Afterburner.Change { ResetCurve = true }, st, refresh), "GhostButton", new Thickness(0, 8, 8, 0))));
        }

        // Fan curve driven by Money Tweaks.
        host.Children.Add(Ui.Text("Fan curve", 13, FontWeights.SemiBold));
        if (g.Fan is null) { host.Children.Add(Ui.Muted("Afterburner reports no controllable fan.", 12)); return; }
        var curve = (st.FanCurve.Count >= 2 ? st.FanCurve : FanCurve.Default.Select(x => x.ToArray()).ToList()).Select(x => x.ToArray()).ToList();
        var grid = new WrapPanel();
        var boxes = new List<(TextBox t, TextBox f)>();
        foreach (var pt in curve)
        {
            var t = new TextBox { Text = pt[0].ToString(), Width = 52, Tag = "°C" };
            var f = new TextBox { Text = pt[1].ToString(), Width = 52, Tag = "%" };
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 14, 4) };
            cell.Children.Add(t); cell.Children.Add(Ui.Muted(" °C → ", 12)); cell.Children.Add(f); cell.Children.Add(Ui.Muted(" %", 12));
            grid.Children.Add(cell);
            boxes.Add((t, f));
        }
        host.Children.Add(Ui.Muted($"Runs only while Money Tweaks is open; closing it (or any read/write problem) puts the fan back on automatic. Must reach 100% by {FanCurve.MustBeFullAtC} °C; speeds can't go below this fan's {g.Fan.Min}% minimum.", 11.5));
        host.Children.Add(grid);
        var status = Ui.Muted(FanCurveService.Running ? "Running: " + FanCurveService.Status : FanCurveService.Status, 12);
        FanCurveService.Changed += () => host.Dispatcher.BeginInvoke(() => status.Text = (FanCurveService.Running ? "Running: " : "") + FanCurveService.Status);
        host.Children.Add(Ui.Row(
            Ui.Btn(FanCurveService.Running ? "Update fan curve" : "Start fan curve", (_, _) =>
            {
                var pts = new List<int[]>();
                foreach (var (t, f) in boxes)
                {
                    if (!int.TryParse(t.Text, out var tc) || !int.TryParse(f.Text, out var fp)) { Dialog.Show("Fan curve", "Every point needs whole numbers for °C and %.", DialogKind.Warning); return; }
                    pts.Add([tc, fp]);
                }
                if (!Admin.IsElevated) { Dialog.Show("Administrator rights needed", "Money Tweaks must run as administrator to control the fan through MSI Afterburner.", DialogKind.Warning); return; }
                var (ok, msg) = FanCurveService.Start(st, pts);
                if (!ok) Dialog.Show("Fan curve not started", msg, DialogKind.Warning);
            }, "BaseButton", new Thickness(0, 8, 8, 0)),
            Ui.Btn("Stop (fan → automatic)", (_, _) => FanCurveService.Stop(), "GhostButton", new Thickness(0, 8, 8, 0))));
        host.Children.Add(status);
    }

    private static async Task ApplyAsync(Afterburner.Change c, TuningState st, Func<Task> refresh)
    {
        if (!Admin.IsElevated) { Dialog.Show("Administrator rights needed", "MSI Afterburner runs as administrator, so only an elevated Money Tweaks may change its settings. Use Restart as admin.", DialogKind.Warning); return; }
        var lines = new List<string>();
        if (c.CoreOffsetMhz is { } a) lines.Add($"Core clock offset → {a:+0;-0;0} MHz");
        if (c.MemOffsetMhz is { } b) lines.Add($"Memory clock offset → {b:+0;-0;0} MHz");
        if (c.PowerLimitPercent is { } p) lines.Add($"Power limit → {p}%");
        if (c.ThermalLimitC is { } t) lines.Add($"Temperature limit → {t} °C");
        if (c.FanAuto == true) lines.Add("Fan → automatic");
        if (c.FanPercent is { } f) lines.Add($"Fan → fixed {f}%");
        if (c.Undervolt is { } u) lines.Add($"Undervolt → {u.mhz} MHz at {u.milliVolts} mV, nothing higher above that voltage");
        if (c.ResetCurve) lines.Add("Voltage/frequency curve → default");
        if (!Dialog.Confirm("Apply through MSI Afterburner?", string.Join("\n", lines.Select(l => "• " + l)) +
                "\n\nAfterburner's settings from before Money Tweaks' first change are backed up and restored by Undo all. Every value is read back from Afterburner; if one doesn't match, the change is undone. " +
                "Unstable offsets or undervolts crash games and drivers and can corrupt files being written: test before relying on them. Afterburner keeps these settings after Money Tweaks closes (and re-applies them at startup only if you turn that on in Afterburner).",
                "Apply", DialogKind.Warning)) return;
        var (ok, results) = await Task.Run(() => TuningEngine.ApplyAfterburner(c, st));
        Dialog.Show(ok ? "Applied" : "Not applied (undone)", string.Join("\n", results.Select(r => $"{r.Status}: {r.Item} {r.Value} – {r.Detail}")), ok ? DialogKind.Info : DialogKind.Warning);
        await refresh();
    }
}
