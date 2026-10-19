using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Advisor;

namespace MoneyTweaks.Views;

public sealed partial class AdvisorView
{
    private static string LimiterTone(Limiter k) => k switch
    {
        Limiter.Gpu or Limiter.GpuPower or Limiter.GpuThermal => "Violet",
        Limiter.Cpu or Limiter.CpuClocks or Limiter.CpuThermal => "Accent",
        Limiter.FrameCap => "Success",
        Limiter.Memory or Limiter.Vram or Limiter.Storage => "Gold",
        Limiter.Background => "Gold",
        _ => "Subtle",
    };

    private void RenderResults()
    {
        _results.Children.Clear();
        if (_dx is not { } d || _res is not { } r) return;
        var g = _store.Goal;

        // ---- diagnosis dashboard --------------------------------------------------------------------------------------------
        var dash = new StackPanel();
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(new TextBlock { Text = "WHAT'S HOLDING YOU BACK", Style = Ui.Res<Style>("Eyebrow") });
        left.Children.Add(new TextBlock { Text = d.Mixed && d.Factors.Count > 1 ? $"Mixed: {d.Factors[0].Label} + {d.Factors[1].Label.ToLowerInvariant()}" : d.Primary.Label, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Ui.Brush(LimiterTone(d.Primary.Kind)), TextWrapping = TextWrapping.Wrap });
        left.Children.Add(Ui.Muted($"Confidence: {d.Primary.Confidence} · {_capture!.What} · {_capture.Seconds} s · {(d.FpsMeasured ? $"{d.Frames!.Frames:N0} frames measured" : "frame timing not available")}", 12));
        foreach (var e in d.Primary.Evidence) left.Children.Add(new TextBlock { Text = "• " + e, FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        hero.Children.Add(left);
        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        right.Children.Add(new TextBlock { Text = "BOTTLENECK BREAKDOWN", Style = Ui.Res<Style>("Eyebrow") });
        if (d.Factors.Count == 0) right.Children.Add(Ui.Muted("No limiter stood out in the measurements.", 12));
        foreach (var f in d.Factors)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0), ToolTip = string.Join("\n", f.Evidence) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = f.Label, FontSize = 12, Foreground = Ui.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var track = new Grid { Height = 12, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(6) });
            track.Children.Add(new Border { Background = Ui.Brush(LimiterTone(f.Kind)), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Tag = f.Score });
            track.SizeChanged += (_, _) => { if (track.Children[1] is Border b) b.Width = Math.Max(6, track.ActualWidth * f.Score); };
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            right.Children.Add(row);
        }
        right.Children.Add(Ui.Muted("Bar = how strongly the measurements point at it (several signals must agree). Hover for the evidence.", 11));
        hero.Children.Add(right);
        dash.Children.Add(hero);

        var metrics = Ui.Grid(5);
        foreach (var (k, v) in d.Summary)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = v.ToString(k.Contains("GB") ? "0.0" : k.Contains("ms") ? "0.0" : "0", CultureInfo.CurrentCulture), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Ui.Brush("Text") });
            sp.Children.Add(Ui.Muted(k, 11));
            metrics.Children.Add(new Border { Background = Ui.Brush("Surface"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 8, 0), Child = sp });
        }
        dash.Children.Add(metrics);
        foreach (var n in d.Notes) dash.Children.Add(new TextBlock { Text = n, FontSize = 11.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        _results.Children.Add(Ui.Card(dash));

        // ---- goal vs current ----------------------------------------------------------------------------------------------------
        var goal = new StackPanel();
        goal.Children.Add(Ui.Section("Your goal vs. now", "Solid = measured. Striped range = projection from your measurements (not a promise)."));
        goal.Children.Add(GoalChart(RecommendationEngine.Simulate(d, g, r), g.TargetFps));
        goal.Children.Add(Ui.Muted(r.FpsNow is { } now ? (now >= g.TargetFps ? $"You're at {now:0} FPS: already at or above your {g.TargetFps:0} FPS target." : $"{g.TargetFps - now:0} FPS short of your {g.TargetFps:0} FPS target.") : "Enter your current FPS or measure with frame timing to compare with the goal.", 12));
        goal.Children.Add(Ui.Muted("Projections scale measured load to 100% (the most the other component could deliver). Game engines, scene changes and settings can make the real result lower; when the scene varied a lot or a frame cap was active, that's stated above.", 11));
        _results.Children.Add(Ui.Card(goal));

        // ---- top three ----------------------------------------------------------------------------------------------------------
        var top = new StackPanel();
        top.Children.Add(Ui.Section("The three smartest moves", $"Ranked by expected benefit per euro for {(g.Game.Length > 0 ? g.Game : "your game")} at {g.Width}×{g.Height}, {g.Settings}{(g.BudgetEur is { } b ? $", budget €{b:0}" : "")}."));
        var cards = Ui.Grid(3);
        for (int i = 0; i < r.Top.Count; i++) cards.Children.Add(Ui.Card(RecCard(r.Top[i], i + 1), new Thickness(0, 0, 10, 10)));
        top.Children.Add(cards);
        if (r.WhyFewer is { } why) top.Children.Add(new TextBlock { Text = why, FontSize = 12.5, Foreground = Ui.Brush("Gold"), TextWrapping = TextWrapping.Wrap });
        _results.Children.Add(Ui.Card(top));

        // ---- upgrade planner -----------------------------------------------------------------------------------------------------
        var plan = new StackPanel();
        plan.Children.Add(Ui.Section("Upgrade planner", "Every option the evidence supports, including ones that didn't make the top three or are over budget. Enter prices to compare performance per euro."));
        foreach (var x in r.All.OrderBy(x => x.Tier).ThenByDescending(x => x.Value))
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            var info = new StackPanel();
            var head = new WrapPanel();
            head.Children.Add(Ui.Tag(x.Tier switch { RecTier.Free => "FREE", RecTier.LowCost => "LOW COST", _ => "HARDWARE" }, x.Tier == RecTier.Free ? "Success" : x.Tier == RecTier.LowCost ? "Accent" : "Violet"));
            if (r.Top.Contains(x)) head.Children.Add(Ui.Tag($"#{r.Top.ToList().IndexOf(x) + 1}", "Gold"));
            if (r.OverBudget.Contains(x)) head.Children.Add(Ui.Tag("OVER BUDGET", "Danger"));
            head.Children.Add(Ui.Text(x.Title, 12.5, FontWeights.SemiBold));
            info.Children.Add(head);
            if (x.Requirements.Count > 0) info.Children.Add(Ui.Muted("Needs: " + string.Join(" · ", x.Requirements), 11));
            row.Children.Add(info);
            var proj = Ui.Muted(x.Projected is { } p ? $"{p.Low:0}–{p.High:0} FPS (projection)" : "no FPS projection", 11.5);
            Grid.SetColumn(proj, 1); row.Children.Add(proj);
            string perEuro = x.CostEur is { } ce && ce > 0 && x.Projected is { } p2 && r.FpsNow is { } n2 ? $"{(p2.Mid - n2) / ce * 100:0.#} FPS per €100" : x.Tier == RecTier.Free ? "free" : x.CostKnown ? "–" : "enter a price";
            var cost = Ui.Muted($"{x.CostText}\n{perEuro}", 11.5);
            Grid.SetColumn(cost, 2); row.Children.Add(cost);
            plan.Children.Add(row);
        }
        if (r.All.Count == 0) plan.Children.Add(Ui.Muted("Nothing to plan: no option is supported by the measurements.", 12));
        _results.Children.Add(Ui.Card(plan));

        // ---- history ---------------------------------------------------------------------------------------------------------------
        var hist = new StackPanel();
        hist.Children.Add(Ui.Section("Earlier diagnoses", null));
        foreach (var h in _store.History.Take(8)) hist.Children.Add(Ui.Muted(h, 11.5));
        _results.Children.Add(Ui.Card(hist));
    }

    private UIElement RecCard(Recommendation x, int rank)
    {
        var sp = new StackPanel();
        var head = new WrapPanel();
        head.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Ui.Brush(rank == 1 ? "Accent" : "Surface"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = rank.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Ui.Brush(rank == 1 ? "OnAccent" : "Text"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        head.Children.Add(Ui.Tag(x.Tier switch { RecTier.Free => "FREE", RecTier.LowCost => "LOW COST", _ => "HARDWARE" }, x.Tier == RecTier.Free ? "Success" : x.Tier == RecTier.LowCost ? "Accent" : "Violet"));
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = x.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        sp.Children.Add(Ui.Muted($"{x.CostText} · {x.Difficulty} · confidence {x.Confidence}", 11.5));
        sp.Children.Add(new TextBlock { Text = x.Benefit, FontSize = 12, Foreground = Ui.Brush("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (x.Projected is { } p)
            sp.Children.Add(new TextBlock { Text = $"Projection: {p.Low:0}–{p.High:0} FPS. {p.Basis}", FontSize = 11.5, Foreground = Ui.Brush("Violet"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        else sp.Children.Add(Ui.Muted("No FPS number: there isn't credible data to estimate it for your game, so only the direction is stated.", 11.5));
        var details = new Expander { Header = Ui.Text("Evidence, requirements and trade-offs", 12, FontWeights.SemiBold), Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Brush("Text") };
        var dp = new StackPanel();
        dp.Children.Add(Ui.Text("Evidence", 11.5, FontWeights.SemiBold));
        foreach (var e in x.Evidence) dp.Children.Add(Ui.Muted("• " + e, 11.5));
        if (x.Requirements.Count > 0) { dp.Children.Add(Ui.Text("Compatibility / requirements", 11.5, FontWeights.SemiBold)); foreach (var q in x.Requirements) dp.Children.Add(Ui.Muted("• " + q, 11.5)); }
        if (x.TradeOffs.Count > 0) { dp.Children.Add(Ui.Text("Trade-offs", 11.5, FontWeights.SemiBold)); foreach (var t in x.TradeOffs) dp.Children.Add(Ui.Muted("• " + t, 11.5)); }
        dp.Children.Add(Ui.Text("Why this rank", 11.5, FontWeights.SemiBold));
        dp.Children.Add(Ui.Muted(x.WhyRanked, 11.5));
        details.Content = dp;
        sp.Children.Add(details);
        var buttons = new List<UIElement>();
        if (x.LinkTag is { } tag) buttons.Add(Ui.Btn(x.LinkLabel ?? "Open", (_, _) => MainWindow.Current?.Navigate(tag), "PrimaryButton", new Thickness(0, 10, 8, 0)));
        if (x.PriceQuery is { } q2) buttons.Add(Ui.Btn("Find prices / enter price", (_, _) => PriceDialog(x, q2), "BaseButton", new Thickness(0, 10, 8, 0)));
        if (buttons.Count > 0) sp.Children.Add(Ui.Row(buttons.ToArray()));
        return sp;
    }

    /// <summary>Opens reputable local stores for the item; the user enters the price they found (kept with source and date).</summary>
    private void PriceDialog(Recommendation x, string query)
    {
        var stores = RecommendationEngine.RetailerSearch(query, _country);
        var win = new Window { Title = "Prices", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current.MainWindow, Background = Ui.Brush("Card"), ResizeMode = ResizeMode.NoResize };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(Ui.Text(x.Title, 15, FontWeights.SemiBold));
        sp.Children.Add(Ui.Muted($"Search “{query}” at stores for {new RegionInfo(_country).EnglishName}, then enter the price of the item you'd buy. Money Tweaks doesn't fetch prices or claim discounts.", 12));
        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (name, url) in stores)
            links.Children.Add(Ui.Btn(name + " ↗", (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }, "GhostButton", new Thickness(0, 0, 6, 6)));
        sp.Children.Add(links);
        _store.Prices.TryGetValue(x.Id, out var old);
        var product = new TextBox { Text = old?.Product ?? "", Tag = "Product (e.g. the exact model)", Margin = new Thickness(0, 4, 0, 4) };
        var price = new TextBox { Text = old?.Eur.ToString("0", CultureInfo.CurrentCulture) ?? "", Tag = "Price in €", Margin = new Thickness(0, 4, 0, 4) };
        var source = new ComboBox { IsEditable = true, Margin = new Thickness(0, 4, 0, 4), Text = old?.Source ?? stores[0].name };
        foreach (var (name, _) in stores) source.Items.Add(name);
        sp.Children.Add(product); sp.Children.Add(price); sp.Children.Add(source);
        var save = Ui.Btn("Save price", (_, _) =>
        {
            if (!double.TryParse(price.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var eur) || eur <= 0) { price.Focus(); return; }
            _store.Prices[x.Id] = new PriceEntry { Eur = eur, Product = product.Text.Trim(), Source = source.Text.Trim(), Date = DateTime.Now };
            _store.Save();
            win.Close();
            Analyze();
        }, "PrimaryButton", new Thickness(0, 8, 8, 0));
        var clear = Ui.Btn("Remove price", (_, _) => { _store.Prices.Remove(x.Id); _store.Save(); win.Close(); Analyze(); }, "GhostButton", new Thickness(0, 8, 0, 0));
        sp.Children.Add(Ui.Row(save, clear));
        win.Content = sp;
        win.ShowDialog();
    }

    /// <summary>Horizontal bars: measured (solid) and projected ranges (striped) against the target line.</summary>
    private static UIElement GoalChart(IReadOnlyList<Scenario> sc, double target)
    {
        var canvas = new Canvas { Height = sc.Count * 40 + 20, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 8) };
        void Draw()
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, left = 280, plotW = Math.Max(50, w - left - 20);
            double max = Math.Max(target * 1.15, sc.Max(s => s.High ?? 0) * 1.05);
            double X(double v) => left + v / max * plotW;
            for (int i = 0; i < sc.Count; i++)
            {
                var s = sc[i]; double y = 10 + i * 40;
                var label = new TextBlock { Text = s.Label, FontSize = 12, Foreground = Ui.Brush("Text"), Width = left - 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = s.Note };
                Canvas.SetLeft(label, 0); Canvas.SetTop(label, y + 2); canvas.Children.Add(label);
                if (s.Low is not { } lo || s.High is not { } hi)
                {
                    var na = new TextBlock { Text = s.Note, FontSize = 11, Foreground = Ui.Brush("Subtle"), Width = plotW, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = s.Note };
                    Canvas.SetLeft(na, left); Canvas.SetTop(na, y + 3); canvas.Children.Add(na);
                    continue;
                }
                if (s.Measured)
                {
                    var bar = new Rectangle { Width = Math.Max(2, X(hi) - left), Height = 20, RadiusX = 4, RadiusY = 4, Fill = Ui.Brush("Accent") };
                    Canvas.SetLeft(bar, left); Canvas.SetTop(bar, y); canvas.Children.Add(bar);
                }
                else
                {
                    var stripes = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(8, 8), MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat };
                    var violet = ((SolidColorBrush)Ui.Brush("Violet")).Color;
                    stripes.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, violet.R, violet.G, violet.B), 0)); stripes.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, violet.R, violet.G, violet.B), 0.5));
                    stripes.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, violet.R, violet.G, violet.B), 0.5)); stripes.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, violet.R, violet.G, violet.B), 1));
                    var range = new Rectangle { Width = Math.Max(4, X(hi) - X(lo)), Height = 20, RadiusX = 4, RadiusY = 4, Fill = stripes, Stroke = Ui.Brush("Violet"), StrokeThickness = 1 };
                    Canvas.SetLeft(range, X(lo)); Canvas.SetTop(range, y); canvas.Children.Add(range);
                }
                var val = new TextBlock { Text = s.Measured ? $"{hi:0} FPS" : $"{lo:0}–{hi:0}", FontSize = 11, Foreground = Ui.Brush("Muted") };
                Canvas.SetLeft(val, X(hi) + 6); Canvas.SetTop(val, y + 3); canvas.Children.Add(val);
            }
            var line = new Line { X1 = X(target), X2 = X(target), Y1 = 0, Y2 = canvas.Height, Stroke = Ui.Brush("Gold"), StrokeThickness = 2, StrokeDashArray = [4, 3] };
            canvas.Children.Add(line);
            var tl = new TextBlock { Text = $"target {target:0}", FontSize = 11, Foreground = Ui.Brush("Gold"), FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(tl, Math.Min(w - 70, X(target) + 4)); Canvas.SetTop(tl, canvas.Height - 16); canvas.Children.Add(tl);
        }
        canvas.SizeChanged += (_, _) => Draw();
        return canvas;
    }
}
