using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MoneyTweaks.Linux.App;

/// <summary>Money Tweaks for Linux: live monitor, reversible kernel tweaks with Tweak X-Ray, and feedback.</summary>
internal sealed class MainWindow : Window
{
    private readonly LinuxFs _fs = new();
    private readonly LinuxTweakEngine _engine;
    private readonly ContentControl _host = new();
    private readonly StackPanel _nav = new() { Margin = new Thickness(12, 18, 12, 12) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Sampler _sampler;
    private Action<Snapshot>? _onSample;
    private bool _sampling;
    public static string Version => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public MainWindow()
    {
        Title = "Money Tweaks for Linux (preview)";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 520;
        Background = Ui.Bg;
        _sampler = new Sampler(_fs);
        _engine = new LinuxTweakEngine(_fs, new PkexecWriter(), LinuxPaths.Backups) { Log = LinuxPaths.Log };
        LinuxPaths.Log($"start {Version} on {LinuxDiagnostics.OsName(_fs)}, kernel {_fs.ReadLine("/proc/sys/kernel/osrelease")}");

        var brand = new StackPanel { Margin = new Thickness(6, 0, 0, 20) };
        brand.Children.Add(new TextBlock { Inlines = [new Avalonia.Controls.Documents.Run("MONEY ") { Foreground = Ui.Text }, new Avalonia.Controls.Documents.Run("TWEAKS") { Foreground = Ui.Accent }], FontSize = 17, FontWeight = FontWeight.Bold });
        brand.Children.Add(Ui.T($"v{Version} · Linux preview", 11, Ui.Subtle));
        _nav.Children.Add(brand);
        AddNav("Monitor", ShowMonitor);
        AddNav("Tweaks", ShowTweaks);
        AddNav("About & feedback", ShowAbout);

        var side = new Border { Child = _nav, Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 1, 0), Width = 220 };
        var banner = new Border
        {
            Background = Ui.Brush("#1A1508"), BorderBrush = Ui.Gold, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 8),
            Child = Ui.T("Preview: this Linux edition was built without testing on Linux hardware. If anything looks wrong or fails, use About & feedback → Report a problem.", 12, Ui.Gold),
        };
        var right = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        right.Children.Add(banner);
        right.Children.Add(_host);
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(right);
        Content = root;

        _timer.Tick += async (_, _) => await SampleNow();
        _timer.Start();
        ShowMonitor();
    }

    private async Task SampleNow()
    {
        if (_sampling || _onSample is null) return;
        _sampling = true;
        try { var s = await Task.Run(_sampler.Sample); _onSample?.Invoke(s); }
        catch (Exception ex) { LinuxPaths.Log("sample: " + ex.Message); }
        finally { _sampling = false; }
    }

    private void AddNav(string text, Action show)
    {
        var b = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent, Foreground = Ui.Text, CornerRadius = new CornerRadius(0), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 2),
        };
        b.Click += (_, _) => show();
        _nav.Children.Add(b);
    }

    // ---- Monitor ------------------------------------------------------------------------------------------------------

    private void ShowMonitor()
    {
        var page = Ui.Page("Monitoring", "Monitor", "Live readings from the kernel (/proc and /sys) and, for NVIDIA cards, nvidia-smi. Updated every second. A reading this PC doesn't provide says so.", out var body);
        var grid = new WrapPanel();
        body.Children.Add(grid);
        var cards = new Dictionary<string, (TextBlock value, TextBlock sub)>();
        foreach (var (key, title) in new[] { ("cpu", "CPU usage"), ("temp", "CPU temperature"), ("clock", "CPU clock"), ("ram", "Memory"), ("gpu", "GPU"), ("net", "Network"), ("up", "Uptime") })
        {
            var v = Ui.T("…", 24, Ui.Text, FontWeight.SemiBold, wrap: false);
            var s = Ui.T("", 11.5, Ui.Muted);
            var sp = new StackPanel();
            sp.Children.Add(Ui.T(title.ToUpperInvariant(), 11, Ui.Subtle, FontWeight.SemiBold));
            sp.Children.Add(v); sp.Children.Add(s);
            grid.Children.Add(new Border { Child = Ui.CardOf(sp, new Thickness(0)), Width = 260, Margin = new Thickness(0, 0, 12, 12) });
            cards[key] = (v, s);
        }
        body.Children.Add(Ui.Eyebrow("Cores"));
        var cores = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(cores);
        var bars = new List<(ProgressBar bar, TextBlock pct)>();

        _onSample = s =>
        {
            void Set(string k, string v, string sub) { cards[k].value.Text = v; cards[k].sub.Text = sub; }
            Set("cpu", s.CpuPercent is { } c ? $"{c:0}%" : s.CpuReadable ? "measuring…" : "not available", s.CpuReadable ? $"{s.CorePercent.Count} logical cores · /proc/stat" : "/proc/stat can't be read");
            Set("temp", s.CpuTempC is { } t ? $"{t:0.0} °C" : "not available", s.CpuTempSource ?? "no k10temp, zenpower or coretemp sensor and no x86_pkg_temp thermal zone");
            Set("clock", s.CpuMhz is { } m ? $"{m:0} MHz" : "not available", s.CpuMhz is null ? "no cpufreq scaling_cur_freq files" : "average of cpufreq scaling_cur_freq");
            Set("ram", s.MemReadable ? $"{(s.MemTotalKb - s.MemAvailableKb) / 1048576.0:0.0} / {s.MemTotalKb / 1048576.0:0.0} GB" : "not available", s.MemReadable ? $"{s.MemUsedPercent:0}% used · /proc/meminfo" : "/proc/meminfo can't be read");
            Set("gpu", s.GpuPercent is { } g ? $"{g:0}%" + (s.GpuTempC is { } gt ? $" · {gt:0} °C" : "") : "not available",
                s.GpuSource ?? "no amdgpu gpu_busy_percent and no nvidia-smi");
            Set("net", s.NetReadable ? $"↓ {Rate(s.NetDownKBps)}" : "not available", s.NetReadable ? $"↑ {Rate(s.NetUpKBps)} · /proc/net/dev (all but loopback)" : "/proc/net/dev can't be read");
            Set("up", s.Uptime is { } u ? $"{(int)u.TotalDays}d {u.Hours}h {u.Minutes}m" : "not available", "/proc/uptime");
            while (bars.Count < s.CorePercent.Count)
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Width = 120, Foreground = Ui.Accent, Background = Ui.Border, CornerRadius = new CornerRadius(0) };
                var pct = Ui.T("", 11, Ui.Muted, wrap: false);
                var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 10) };
                sp.Children.Add(Ui.T($"CPU {bars.Count}", 11, Ui.Subtle, wrap: false)); sp.Children.Add(bar); sp.Children.Add(pct);
                cores.Children.Add(sp);
                bars.Add((bar, pct));
            }
            for (int i = 0; i < s.CorePercent.Count; i++) { bars[i].bar.Value = s.CorePercent[i]; bars[i].pct.Text = $"{s.CorePercent[i]:0}%"; }
        };
        _host.Content = page;
        _ = SampleNow();   // fill the cards right away instead of after the first tick
    }

    private static string Rate(double kbps) => kbps >= 1024 ? $"{kbps / 1024:0.0} MB/s" : $"{kbps:0} KB/s";

    // ---- Tweaks -------------------------------------------------------------------------------------------------------

    private void ShowTweaks()
    {
        _onSample = null;
        var page = Ui.Page("Optimization", "Tweaks", $"Kernel settings Money Tweaks can change and undo. Each change asks for your password through polkit, is backed up first, read back afterwards and rolled back if the kernel doesn't keep it. Changes last {LinuxTweak.Persistence}.", out var body);
        foreach (var t in LinuxCatalog.All) body.Children.Add(TweakCard(t));
        if (Sampler.Which("gamemoded") is not null || Sampler.Which("gamemoderun") is not null)
            body.Children.Add(Ui.CardOf(Ui.T("Feral GameMode is installed. To use it for a Steam game, set its launch options to: gamemoderun %command%", 12.5, Ui.Muted)));
        _host.Content = page;
    }

    private Control TweakCard(LinuxTweak t)
    {
        var sp = new StackPanel();
        var status = Ui.T("", 12, Ui.Muted);
        var message = Ui.T("", 12, Ui.Gold);
        var xray = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var head = new WrapPanel();
        head.Children.Add(Ui.T(t.Name, 15, Ui.Text, FontWeight.SemiBold, wrap: false));
        sp.Children.Add(head);
        var purpose = Ui.T(t.Purpose, 12.5, Ui.Muted); purpose.Margin = new Thickness(0, 4, 0, 6);
        sp.Children.Add(purpose);
        sp.Children.Add(status);
        sp.Children.Add(message);
        sp.Children.Add(actions);
        var expander = new Expander { Header = "Inspect changes", Content = xray, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(0) };
        expander.Expanded += (_, _) => FillXRay(t, xray);
        sp.Children.Add(expander);

        void Refresh()
        {
            var (state, reason) = _engine.State(t);
            status.Text = state switch
            {
                LinuxTweakState.Applied => "● Applied" + (_engine.Backup(t) is null ? " (already set before, or by another tool)" : ""),
                LinuxTweakState.Partial => "◐ Partly applied",
                LinuxTweakState.NotApplied => "○ Not applied",
                _ => "Not available on this PC: " + reason,
            };
            status.Foreground = state == LinuxTweakState.Applied ? Ui.Success : state == LinuxTweakState.NotAvailable ? Ui.Subtle : Ui.Muted;
            actions.Children.Clear();
            if (state is LinuxTweakState.NotApplied or LinuxTweakState.Partial)
                actions.Children.Add(Ui.Btn("Apply", async (_, _) => await Run(() => _engine.Apply(t)), primary: true));
            if (_engine.Backup(t) is not null)
                actions.Children.Add(Ui.Btn("Undo", async (_, _) => await Run(() => _engine.Undo(t))));
            if (expander.IsExpanded) FillXRay(t, xray);
        }

        async Task Run(Func<LinuxResult> op)
        {
            foreach (var b in actions.Children.OfType<Button>()) b.IsEnabled = false;
            message.Text = "Waiting for authorisation…";
            LinuxResult r;
            try { r = await Task.Run(op); }
            catch (Exception ex) { r = new(false, ex.Message, LinuxTweakState.NotApplied); LinuxPaths.Log($"{t.Id}: {ex}"); }
            message.Text = (r.Success ? "" : "⚠ ") + r.Message;
            message.Foreground = r.Success ? Ui.Success : Ui.Gold;
            Refresh();
        }

        Refresh();
        return Ui.CardOf(sp);
    }

    private void FillXRay(LinuxTweak t, StackPanel host)
    {
        host.Children.Clear();
        host.Children.Add(Ui.T($"Risk: {t.Risk} · Reversible: Undo writes back the values captured before applying · Lasts {LinuxTweak.Persistence}", 11.5, Ui.Muted));
        var changes = _engine.Inspect(t);
        if (changes.Count == 0) host.Children.Add(Ui.T("Nothing to change: this PC doesn't have the setting.", 12, Ui.Subtle));
        foreach (var c in changes)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 6, 0, 0) };
            int row = 0;
            void Add(string k, string v, IBrush tone)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var kt = Ui.T(k, 11.5, Ui.Subtle); Grid.SetRow(kt, row);
                var vt = new SelectableTextBlock { Text = v, FontSize = 11.5, Foreground = tone, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(vt, row); Grid.SetColumn(vt, 1);
                g.Children.Add(kt); g.Children.Add(vt);
                row++;
            }
            Add("File", c.Path, Ui.Text);
            Add("Now", c.Current, Ui.Muted);
            Add("After applying", c.Proposed + (c.AlreadySet ? "  (already set, unchanged)" : ""), Ui.Text);
            Add("Undo", c.Undo, Ui.Muted);
            host.Children.Add(new Border { Child = g, Background = Ui.Surface, Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 6, 0, 0) });
        }
    }

    // ---- About & feedback -----------------------------------------------------------------------------------------------

    private void ShowAbout()
    {
        _onSample = null;
        var page = Ui.Page("Money Tweaks for Linux", "About & feedback", "A preview of Money Tweaks for Linux. It was built without being run on Linux hardware, so your reports decide what gets fixed first.", out var body);
        string report = LinuxDiagnostics.Report(_fs, Version, _engine);
        var reportBox = new SelectableTextBlock { Text = report, FontFamily = new FontFamily("monospace"), FontSize = 11.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        var title = new TextBox { Watermark = "Short description of the problem", CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 6, 0, 8) };
        var link = new SelectableTextBlock { FontSize = 11, Foreground = Ui.Subtle, TextWrapping = TextWrapping.Wrap };
        var status = Ui.T("", 12, Ui.Success);
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("Report a problem", 15, Ui.Text, FontWeight.SemiBold));
        sp.Children.Add(Ui.T("Opens a new GitHub issue in your browser with the system details below filled in. Nothing is sent until you submit it there, and you can edit or remove anything first.", 12.5, Ui.Muted));
        sp.Children.Add(title);
        var row = new WrapPanel();
        row.Children.Add(Ui.Btn("Report a problem", (_, _) =>
        {
            string url = LinuxDiagnostics.IssueUrl(string.IsNullOrWhiteSpace(title.Text) ? "Problem report" : title.Text.Trim(), report);
            link.Text = "If no browser opened, copy this link: " + url;
            try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); status.Text = "Opened in your browser."; }
            catch (Exception ex) { status.Text = "Couldn't open a browser (" + ex.Message + "); use the link below."; }
        }, primary: true));
        row.Children.Add(Ui.Btn("Copy details", async (_, _) =>
        {
            if (GetTopLevel(this)?.Clipboard is { } cb) { await cb.SetTextAsync(report); status.Text = "Details copied."; }
        }));
        sp.Children.Add(row);
        sp.Children.Add(status);
        sp.Children.Add(link);
        body.Children.Add(Ui.CardOf(sp));
        var d = new StackPanel();
        d.Children.Add(Ui.T("System details included in reports", 13, Ui.Text, FontWeight.SemiBold));
        d.Children.Add(Ui.T("No user names, host names, home-folder paths or network addresses.", 11.5, Ui.Subtle));
        reportBox.Margin = new Thickness(0, 8, 0, 0);
        d.Children.Add(reportBox);
        body.Children.Add(Ui.CardOf(d));
        body.Children.Add(Ui.T($"Log: {LinuxPaths.LogFile} · Backups: {LinuxPaths.Backups} · Free and open source (MIT). Interface: Avalonia (MIT); notices in THIRD-PARTY-NOTICES-LINUX.txt next to the program.", 11.5, Ui.Subtle));
        _host.Content = page;
    }
}
