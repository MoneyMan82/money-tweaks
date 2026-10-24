using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Monitoring;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Reality Lab: this PC's graphics card in 3D. The adapter Windows reports is matched to Money Tweaks' card library
/// (<see cref="GpuModels"/>); the matched model is built procedurally to the reference / Founders Edition dimensions or as
/// a typical partner card of that chip, and an unknown card gets a placeholder rather than a wrong model. Live readings
/// come from the shared sensor hub while the page is shown.
/// </summary>
public sealed partial class RealityLabView
{
    private readonly Task<(DxgiAdapter? adapter, GpuMatch match)> _gpuDetect = Task.Run(() =>
    {
        var adapters = Dxgi.Adapters();
        var a = GpuModels.Primary(adapters);
        var m = a is null ? GpuModels.Match("No graphics adapter found") : GpuModels.Match(a.Name, a.VendorId, a.SubSysId);
        Log.Info($"Reality Lab GPU: {a?.Name ?? "none"} → {m.Design}{(m.Spec is { } s ? " " + s.Id : "")}{(m.Maker is { } mk ? ", made by " + mk : "")}");
        return (a, m);
    });
    private readonly Border _gpuHost = new() { Height = 330, ClipToBounds = true };
    private readonly StackPanel _gpuInfo = new();
    private readonly StackPanel _gpuLive = new();
    private GpuShowcase? _showcase;
    private readonly object _hubToken = new();
    private bool _hubHeld;

    private UIElement BuildGpuCard()
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var tag = Ui.Tag("3D MODEL · LIVE READINGS", "Violet");
        DockPanel.SetDock(tag, Dock.Right);
        head.Children.Add(tag);
        head.Children.Add(Ui.Text("Your graphics card", 15, FontWeights.SemiBold));
        sp.Children.Add(head);
        _gpuHost.Background = new LinearGradientBrush(Color.FromRgb(0x12, 0x13, 0x1A), Color.FromRgb(0x05, 0x06, 0x08), 90);
        _gpuHost.Child = Ui.Loading("Identifying your graphics card…");
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.Children.Add(_gpuHost);
        var side = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
        side.Children.Add(_gpuInfo);
        side.Children.Add(_gpuLive);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        sp.Children.Add(grid);
        _gpuDetect.ContinueWith(_ => Dispatcher.BeginInvoke(EnsureShowcase, DispatcherPriority.Background));
        return Ui.Card(sp);
    }

    private void EnsureShowcase()
    {
        if (_showcase is not null || !_gpuDetect.IsCompletedSuccessfully) return;
        var (adapter, m) = _gpuDetect.Result;
        using (Perf.Measure("build GPU model", 20))
            _showcase = new GpuShowcase(m, detailed: !SoftwareRendering);
        _gpuHost.Child = _showcase;
        RenderGpuInfo(adapter, m);
        UpdateGpuTelemetry();
        SyncRunning();
        Log.Info($"Reality Lab: GPU model built ({_showcase.Triangles} triangles, {m.Design})");
    }

    private void RenderGpuInfo(DxgiAdapter? adapter, GpuMatch m)
    {
        _gpuInfo.Children.Clear();
        _gpuInfo.Children.Add(new TextBlock { Text = m.Design switch { GpuDesign.Integrated => "INTEGRATED GRAPHICS", GpuDesign.Laptop => "LAPTOP GPU", GpuDesign.Unknown => "NOT IN THE MODEL LIBRARY", _ => (m.Maker ?? m.Spec!.Vendor).ToUpperInvariant() }, Style = Ui.Res<Style>("Eyebrow") });
        _gpuInfo.Children.Add(new TextBlock { Text = adapter?.Name.Replace("(R)", "").Replace("(TM)", "") ?? "No graphics adapter found", FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        var why = Ui.Muted(m.Why, 11.5);
        why.TextWrapping = TextWrapping.Wrap;
        _gpuInfo.Children.Add(why);
        if (m.Spec is { } s)
        {
            var specs = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            void Row(string k, string v) => specs.Children.Add(InfoRow(k, v));
            Row("Chip", s.Chip);
            Row("Memory", $"{s.MemoryGb} GB {s.MemoryType}, {s.BusBits}-bit");
            Row("Rated board power", $"{s.BoardPowerW} W");
            var (len, h, slots, fans) = m.Shape;
            Row(m.Design == GpuDesign.Partner ? "Model size (typical)" : "Size", $"{len} × {h} mm, {slots.ToString("0.#", CultureInfo.CurrentCulture)} slots, {fans} fan{(fans == 1 ? "" : "s")}");
            _gpuInfo.Children.Add(specs);
        }
        var note = Ui.Muted("The model is drawn by Money Tweaks from published dimensions; it is not a scan of your exact card. Drag to turn it, scroll to zoom.", 11);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 8, 0, 6);
        _gpuInfo.Children.Add(note);
    }

    private static FrameworkElement InfoRow(string k, string v)
    {
        var g = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var val = new TextBlock { Text = v, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 165, TextAlignment = TextAlignment.Right, FontSize = 12.5 };
        DockPanel.SetDock(val, Dock.Right);
        g.Children.Add(val);
        g.Children.Add(new TextBlock { Text = k, Foreground = Ui.Brush("Muted"), FontSize = 12.5 });
        return g;
    }

    /// <summary>The shown card's live readings from the sensor hub (metrics of the adapter with the same name).</summary>
    private GpuTelemetry? ReadGpuTelemetry()
    {
        if (!_gpuDetect.IsCompletedSuccessfully || _gpuDetect.Result.adapter is not { } a || SensorHub.Shared.Last is not { } f) return null;
        var m = SensorHub.Shared.Metrics.FirstOrDefault(x => x.Device == a.Name && x.Id.EndsWith(".load", StringComparison.Ordinal) && x.Id.StartsWith("gpu", StringComparison.Ordinal));
        if (m is null) return null;
        var p = m.Id[..m.Id.IndexOf('.')];
        double? V(string id) => f.Values.GetValueOrDefault(id);
        double? Flag(string key) => f.Flags.TryGetValue(key, out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var rpm = f.Values.Where(kv => kv.Key.StartsWith(p + ".fanrpm.", StringComparison.Ordinal) && kv.Value is not null).Select(kv => kv.Value).FirstOrDefault();
        return new GpuTelemetry(V(p + ".load"), V(p + ".temp"), V(p + ".temp.hot-spot"), V(p + ".clock"), V(p + ".power"), Flag(p + ".powerlimit"),
            V(p + ".vram"), Flag(p + ".vramtotal"), V(p + ".fan"), rpm);
    }

    private void UpdateGpuTelemetry()
    {
        var t = ReadGpuTelemetry();
        if (t is not null) _showcase?.Show(t);
        _gpuLive.Children.Clear();
        if (_showcase is null) return;
        _gpuLive.Children.Add(new TextBlock { Text = "LIVE", Style = Ui.Res<Style>("Eyebrow"), Margin = new Thickness(0, 4, 0, 2) });
        if (!_showcase.Match.IsCard && _showcase.Match.Design != GpuDesign.Unknown && t is null)
        {
            _gpuLive.Children.Add(Ui.Muted("Readings for this GPU appear in the Hardware Monitor when Windows reports them.", 11.5));
            return;
        }
        if (t is null) { _gpuLive.Children.Add(Ui.Muted("Waiting for the first reading…", 11.5)); return; }
        string N(double? v, string unit, string fmt = "0") => v is { } x ? x.ToString(fmt, CultureInfo.CurrentCulture) + unit : "not reported";
        _gpuLive.Children.Add(InfoRow("Load", N(t.LoadPct, " %")));
        _gpuLive.Children.Add(InfoRow("Temperature", N(t.TempC, " °C") + (t.HotSpotC is { } hs ? $" (hot spot {hs:0} °C)" : "")));
        _gpuLive.Children.Add(InfoRow("Core clock", N(t.ClockMhz, " MHz")));
        _gpuLive.Children.Add(InfoRow("Power", N(t.PowerW, " W") + (t.PowerLimitW is { } pl && t.PowerW is not null ? $" of {pl:0} W" : "")));
        _gpuLive.Children.Add(InfoRow("VRAM in use", t.VramMb is { } v ? $"{v / 1024:0.0}" + (t.VramTotalMb is { } vt ? $" of {vt / 1024:0.0} GB" : " GB") : "not reported"));
        _gpuLive.Children.Add(InfoRow("Fans", t.FanPct is { } fp ? $"{fp:0} %" + (t.FanRpm is { } r ? $" ({r:0} RPM)" : "") + (fp < 1 ? ", stopped (zero-RPM idle)" : "") : t.FanRpm is { } r2 ? $"{r2:0} RPM" : "not reported"));
    }

    private void HoldHub(bool on)
    {
        if (on && !_hubHeld) { SensorHub.Shared.Acquire(_hubToken, record: false); _hubHeld = true; }
        else if (!on && _hubHeld) { SensorHub.Shared.Release(_hubToken); _hubHeld = false; }
    }
}
