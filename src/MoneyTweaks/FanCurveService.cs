using System.Windows.Threading;
using MoneyTweaks.Core.Features;
using MoneyTweaks.Core.Sys;
using MoneyTweaks.Core.Tuning;

namespace MoneyTweaks;

/// <summary>
/// Drives the GPU fan from Money Tweaks' fan curve through MSI Afterburner while Money Tweaks is open. Any problem (no
/// temperature, Afterburner closed, a write refused) hands the fan back to automatic control. Closing Money Tweaks does
/// the same; after a crash, the next start puts the fan back to automatic before anything else.
/// </summary>
public static class FanCurveService
{
    private static DispatcherTimer? _timer;
    private static int _last = -1;
    private static TuningState? _st;
    public static bool Running => _timer is not null;
    public static string Status { get; private set; } = "Off";
    public static event Action? Changed;

    public static (bool ok, string message) Start(TuningState st, IReadOnlyList<int[]> points)
    {
        var g = Afterburner.Read();
        if (g is null) return (false, "MSI Afterburner isn't running (or Money Tweaks isn't running as administrator).");
        if (g.Fan is null) return (false, "Afterburner reports no controllable fan on this GPU.");
        var errors = FanCurve.Validate(points, g.Fan);
        if (errors.Count > 0) return (false, string.Join(" ", errors));
        if (st.AfterburnerBackup is null && Afterburner.Control.Read() is { } snap) st.AfterburnerBackup = snap; // rollback point
        st.FanCurve = points.Select(p => p.ToArray()).ToList();
        st.FanCurveActive = true; // crash marker, saved before the first fan write
        st.Save();
        _st = st;
        _last = -1;
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= Tick;
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, EventArgs.Empty);
        Log.Info("Fan curve started: " + string.Join(" ", points.Select(p => $"{p[0]}°C={p[1]}%")));
        return (true, "Fan curve running.");
    }

    private static async void Tick(object? s, EventArgs e)
    {
        if (_st is null) return;
        var temp = await Task.Run(() => NvApi.GpuTemperature() ?? Afterburner.MonitoringValue(Afterburner.SrcGpuTemperature, cpu: false));
        if (temp is not { } t) { Stop("GPU temperature couldn't be read"); return; }
        int pct = FanCurve.Evaluate(_st.FanCurve, t);
        Status = $"{t:0} °C → {pct}%";
        if (Math.Abs(pct - _last) >= 2 || _last < 0)
        {
            var r = await Task.Run(() => Afterburner.Apply(new Afterburner.Change { FanPercent = pct }));
            if (!r.ok) { Stop("Afterburner refused the fan speed: " + r.message); return; }
            _last = pct;
        }
        Changed?.Invoke();
    }

    /// <summary>Stops the curve and returns the fan to automatic control.</summary>
    public static void Stop(string? reason = null)
    {
        _timer?.Stop();
        _timer = null;
        var (ok, msg, _, _) = Afterburner.Apply(new Afterburner.Change { FanAuto = true });
        if (_st is not null) { _st.FanCurveActive = false; _st.Save(); }
        Status = reason is null ? "Off (fan on automatic)" : $"Stopped: {reason}. Fan {(ok ? "back on automatic" : "couldn't be switched back: " + msg)}.";
        Log.Info("Fan curve stopped" + (reason is null ? "" : ": " + reason) + $"; auto fan: {ok}");
        Changed?.Invoke();
    }

    /// <summary>At startup: a curve was running when Money Tweaks last closed without stopping it (crash). Fan → automatic.</summary>
    public static string? RecoverAfterCrash()
    {
        var st = TuningState.Load();
        if (!st.FanCurveActive) return null;
        var (ok, msg, _, _) = Afterburner.Apply(new Afterburner.Change { FanAuto = true });
        st.FanCurveActive = false;
        st.Save();
        Log.Warn($"Fan curve was active at last exit; fan set back to automatic: {ok} {msg}");
        return ok ? "Money Tweaks closed while its fan curve was running; the GPU fan is back on automatic." : "Money Tweaks closed while its fan curve was running. Start MSI Afterburner and set the fan to Auto.";
    }
}
