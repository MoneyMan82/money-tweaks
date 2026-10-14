using System.Globalization;
using System.Runtime.InteropServices;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Core.Macros;

public enum ColorMode { Appears, Disappears, Changes }
public enum TriggerLogic { All, Any }

/// <summary>A screen-colour condition on a pixel or small region, in physical virtual-screen pixels (all monitors, any scaling).</summary>
public sealed class ColorTrigger
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public string Color { get; set; } = "#FF0000";
    /// <summary>Max per-channel difference (0 = exact, 255 = anything).</summary>
    public int Tolerance { get; set; } = 20;
    public ColorMode Mode { get; set; } = ColorMode.Appears;

    public const int MaxSide = 200; // keeps capture cheap

    public static bool TryParseHex(string? hex, out (byte r, byte g, byte b) c)
    {
        c = default;
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        c = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>Evaluates this condition on BGRA pixels. For Changes, <paramref name="baseline"/> is the average colour when watching began.</summary>
    public bool Evaluate(byte[] bgra, ref (int r, int g, int b)? baseline)
    {
        if (Mode == ColorMode.Changes)
        {
            var avg = Average(bgra);
            baseline ??= avg;
            var b0 = baseline.Value;
            return Math.Max(Math.Abs(avg.r - b0.r), Math.Max(Math.Abs(avg.g - b0.g), Math.Abs(avg.b - b0.b))) > Tolerance;
        }
        TryParseHex(Color, out var t);
        bool any = false;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            if (Math.Abs(bgra[i + 2] - t.r) <= Tolerance && Math.Abs(bgra[i + 1] - t.g) <= Tolerance && Math.Abs(bgra[i] - t.b) <= Tolerance) { any = true; break; }
        return Mode == ColorMode.Appears ? any : !any;
    }

    public static (int r, int g, int b) Average(byte[] bgra)
    {
        long r = 0, g = 0, b = 0;
        int n = bgra.Length / 4;
        if (n == 0) return (0, 0, 0);
        for (int i = 0; i + 3 < bgra.Length; i += 4) { b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; }
        return ((int)(r / n), (int)(g / n), (int)(b / n));
    }

    public string Describe() => Mode switch
    {
        ColorMode.Appears => $"{Color} appears",
        ColorMode.Disappears => $"{Color} disappears",
        _ => "colour changes",
    } + $" at {X},{Y}" + (Width > 1 || Height > 1 ? $" ({Width}×{Height})" : "") + $" ±{Tolerance}";

    public ColorTrigger Clone() => (ColorTrigger)MemberwiseClone();
}

/// <summary>GDI capture of small screen regions. Coordinates are physical pixels (the app is per-monitor DPI aware).</summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    private static readonly Lock CaptureGate = new();
    public const int MaxCapture = 512;
    private static nint _memDc, _dib, _bits;

    /// <summary>
    /// Returns BGRA bytes for the region, or null if Windows refused the capture. One cached 200×200 DIB is reused for
    /// every call (creating a DC + bitmap per capture was the main CPU cost).
    /// </summary>
    public static byte[]? Capture(int x, int y, int w, int h)
    {
        w = Math.Clamp(w, 1, MaxCapture);
        h = Math.Clamp(h, 1, MaxCapture);
        lock (CaptureGate)
        {
            nint screen = GetDC(0);
            try
            {
                if (_memDc == 0)
                {
                    _memDc = CreateCompatibleDC(screen);
                    var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = MaxCapture, biHeight = -MaxCapture, biPlanes = 1, biBitCount = 32 };
                    _dib = CreateDIBSection(screen, ref bi, 0, out _bits, 0, 0);
                    if (_dib == 0) { DeleteDC(_memDc); _memDc = 0; return null; }
                    SelectObject(_memDc, _dib);
                }
                if (!BitBlt(_memDc, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */)) return null;
                var data = new byte[w * h * 4];
                for (int row = 0; row < h; row++)
                    Marshal.Copy(_bits + row * MaxCapture * 4, data, row * w * 4, w * 4);
                return data;
            }
            finally { ReleaseDC(0, screen); }
        }
    }

    public static (int x, int y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public static (byte r, byte g, byte b)? PixelAt(int x, int y)
    {
        var d = Capture(x, y, 1, 1);
        return d is null ? null : (d[2], d[1], d[0]);
    }
}

public sealed record ColorStatus(Macro Macro, bool[] Conditions, bool Combined, (int r, int g, int b)[] Seen, bool PausedForGame);

/// <summary>
/// One background loop for all armed colour triggers. Each macro is checked at its own interval (50–5000 ms) and only
/// small regions are captured, so CPU stays low. Paused entirely while a blocked competitive game is focused.
/// </summary>
public sealed class ColorWatcher : IDisposable
{
    private readonly Func<int, int, int, int, byte[]?> _capture;
    private readonly Func<string?> _foreground;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, (bool combined, DateTime due, DateTime lastFire, (int, int, int)?[] baselines)> _state = [];
    private readonly Lock _gate = new();
    public event Action<ColorStatus>? Status;
    /// <summary>Raised when a macro's combined colour condition turns true (rising edge).</summary>
    public event Action<Macro>? Rising;
    /// <summary>Raised when a macro's combined colour condition turns false (falling edge).</summary>
    public event Action<Macro>? Falling;

    public ColorWatcher(Func<int, int, int, int, byte[]?>? capture = null, Func<string?>? foreground = null)
    {
        _capture = capture ?? ScreenCapture.Capture;
        _foreground = foreground ?? MacroManager.ForegroundProcessName;
    }

    public bool IsRunning => _cts is not null;

    public void Start(IReadOnlyList<Macro> macros)
    {
        Stop();
        var watched = macros.Where(m => m.ColorTriggers.Count > 0).ToList();
        if (watched.Count == 0) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        lock (_gate) _state.Clear();
        _ = Task.Run(() => Loop(watched, ct), ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Last combined result for a macro (used to AND colour conditions with key triggers).</summary>
    public bool IsTrue(Macro m) { lock (_gate) return _state.TryGetValue(m.Id, out var s) && s.combined; }

    private async Task Loop(List<Macro> macros, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var blocked = MacroPolicy.BlockedGame(_foreground()) is not null;
            var nextDue = now.AddSeconds(1);
            foreach (var m in macros)
            {
                (bool combined, DateTime due, DateTime lastFire, (int, int, int)?[] baselines) st;
                lock (_gate) st = _state.TryGetValue(m.Id, out var s) ? s : (false, now, DateTime.MinValue, new (int, int, int)?[m.ColorTriggers.Count]);
                if (st.due > now) { if (st.due < nextDue) nextDue = st.due; continue; }
                var interval = TimeSpan.FromMilliseconds(Math.Clamp(m.ColorPollMs, MacroPolicy.MinColorPollMs, 5000));
                if (blocked)
                {
                    if (st.combined) Falling?.Invoke(m);
                    lock (_gate) _state[m.Id] = (false, now + interval, st.lastFire, st.baselines);
                    Status?.Invoke(new ColorStatus(m, new bool[m.ColorTriggers.Count], false, [], true));
                    if (now + interval < nextDue) nextDue = now + interval;
                    continue;
                }
                var results = new bool[m.ColorTriggers.Count];
                var seen = new (int r, int g, int b)[m.ColorTriggers.Count];
                // One screen read per check when all regions fit in one box (each screen read costs ~1–2 ms).
                int ux = m.ColorTriggers.Min(t => t.X), uy = m.ColorTriggers.Min(t => t.Y);
                int uw = m.ColorTriggers.Max(t => t.X + t.Width) - ux, uh = m.ColorTriggers.Max(t => t.Y + t.Height) - uy;
                byte[]? union = m.ColorTriggers.Count > 1 && uw <= ScreenCapture.MaxCapture && uh <= ScreenCapture.MaxCapture ? _capture(ux, uy, uw, uh) : null;
                for (int i = 0; i < m.ColorTriggers.Count; i++)
                {
                    var t = m.ColorTriggers[i];
                    var px = union is not null ? Slice(union, uw, t.X - ux, t.Y - uy, t.Width, t.Height) : _capture(t.X, t.Y, t.Width, t.Height);
                    if (px is null) { results[i] = false; continue; }
                    var bl = st.baselines[i];
                    results[i] = t.Evaluate(px, ref bl);
                    st.baselines[i] = bl;
                    seen[i] = ColorTrigger.Average(px);
                }
                bool combined = m.ColorLogic == TriggerLogic.All ? results.All(r => r) : results.Any(r => r);
                var lastFire = st.lastFire;
                if (combined && !st.combined && now - st.lastFire >= TimeSpan.FromMilliseconds(500))
                {
                    lastFire = now;
                    Rising?.Invoke(m);
                    // "Changes" conditions re-arm against the new colour after firing.
                    for (int i = 0; i < m.ColorTriggers.Count; i++) if (m.ColorTriggers[i].Mode == ColorMode.Changes) st.baselines[i] = seen[i];
                }
                else if (!combined && st.combined) Falling?.Invoke(m);
                lock (_gate) _state[m.Id] = (combined, now + interval, lastFire, st.baselines);
                Status?.Invoke(new ColorStatus(m, results, combined, seen, false));
                if (now + interval < nextDue) nextDue = now + interval;
            }
            var wait = nextDue - DateTime.UtcNow;
            try { await Task.Delay(wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static byte[] Slice(byte[] src, int srcW, int x, int y, int w, int h)
    {
        var d = new byte[w * h * 4];
        for (int row = 0; row < h; row++) Buffer.BlockCopy(src, ((y + row) * srcW + x) * 4, d, row * w * 4, w * 4);
        return d;
    }

    public void Dispose() => Stop();
}
