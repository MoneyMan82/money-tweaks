using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;
using MoneyTweaks.Core.Model;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks.Views;

/// <summary>
/// Tweak X-Ray in a tweak card: every system location the tweak changes, the value now versus after applying, what Undo
/// restores, plus purpose, risk, reversibility and the backup. Read on a background thread when opened (power-plan and
/// command checks start processes); nothing is changed.
/// </summary>
public static class XRayPanel
{
    public static async void Fill(ContentControl host, Tweak t)
    {
        host.Content = Ui.Loading("Reading the current values from Windows…");
        XRayReport r;
        try { r = await Task.Run(() => TweakXRay.Inspect(t)); }
        catch (Exception ex)
        {
            Log.Error("Tweak X-Ray failed", ex);
            host.Content = Ui.Muted("Couldn't inspect this tweak: " + ex.Message, 12);
            return;
        }
        host.Content = Build(r, () => Fill(host, t));
    }

    private static UIElement Build(XRayReport r, Action refresh)
    {
        var root = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var again = Ui.Btn("Read again", (_, _) => refresh(), "GhostButton");
        again.Padding = new Thickness(10, 3, 10, 3); again.FontSize = 11.5;
        DockPanel.SetDock(again, Dock.Right);
        head.Children.Add(again);
        head.Children.Add(new TextBlock { Text = "TWEAK X-RAY", Style = Ui.Res<Style>("Eyebrow"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(head);

        int unknown = r.Changes.Count(c => c.Reach != XRayReach.Exact), set = r.Changes.Count(c => c.AlreadySet);
        root.Children.Add(Ui.Muted($"{r.Changes.Count} change{(r.Changes.Count == 1 ? "" : "s")}" + (set > 0 ? $" · {set} already set" : "")
            + (unknown > 0 ? $" · {unknown} not fully inspectable" : "") + $" · read from this PC at {r.At:T}" + (r.Applied ? " · applied" : ""), 11.5));
        root.Children.Add(Facts(r));
        foreach (var c in r.Changes) root.Children.Add(Change(c, r.Applied));
        return root;
    }

    private static UIElement Facts(XRayReport r)
    {
        var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Add(string k, string v, string? tone = null)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = k, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 2, 8, 2) };
            var vt = new TextBlock { Text = v, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(tone ?? "Muted"), Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(kt, row); Grid.SetRow(vt, row); Grid.SetColumn(vt, 1);
            g.Children.Add(kt); g.Children.Add(vt);
            row++;
        }
        Add("Purpose", r.Purpose);
        Add("Risk", r.Risk, r.Tweak.Risk == Risk.Caution ? "Gold" : null);
        Add("Reversibility", r.Reversibility, r.Reversibility.StartsWith("full", StringComparison.Ordinal) ? null : "Gold");
        Add("Backup", r.Backup);
        return g;
    }

    private static UIElement Change(XRayChange c, bool applied)
    {
        var sp = new StackPanel();
        var tags = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        tags.Children.Add(Ui.Tag(c.Kind.ToUpperInvariant(), "Accent"));
        tags.Children.Add(Spaced(c.Reach switch
        {
            XRayReach.Exact => Ui.Tag("READ FROM WINDOWS", "Success"),
            XRayReach.Partial => Ui.Tag("PARTLY INSPECTABLE", "Gold"),
            _ => Ui.Tag("EFFECT NOT INSPECTABLE", "Danger"),
        }));
        if (c.AlreadySet && !applied) tags.Children.Add(Spaced(Ui.Tag(c.Proposed.StartsWith("nothing", StringComparison.Ordinal) ? "NOT ON THIS PC" : "ALREADY SET", "Violet")));
        sp.Children.Add(tags);
        sp.Children.Add(new TextBox
        {
            Text = c.Location, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
            Foreground = Ui.Brush("Text"), FontFamily = Ui.Res<FontFamily>("MonoFont"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            ToolTip = "Select to copy",
        });
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        void Add(string k, string v, string tone)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = k, FontSize = 11.5, Foreground = Ui.Brush("Subtle"), Margin = new Thickness(0, 1, 8, 1) };
            var vt = new TextBlock { Text = v, FontSize = 11.5, FontFamily = Ui.Res<FontFamily>("MonoFont"), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush(tone), Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(kt, row); Grid.SetRow(vt, row); Grid.SetColumn(vt, 1);
            g.Children.Add(kt); g.Children.Add(vt);
            row++;
        }
        Add("Now", c.Current, "Muted");
        Add(applied ? "Set by tweak" : "After applying", c.Proposed, "Text");
        Add("Undo", c.Undo, "Muted");
        sp.Children.Add(g);
        if (c.Note is { } n) sp.Children.Add(new TextBlock { Text = n, FontSize = 11, Foreground = Ui.Brush("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return new Border
        {
            Child = sp, BorderBrush = Ui.Brush(c.Reach == XRayReach.Unknown ? "Danger" : "BorderBrush"), BorderThickness = new Thickness(c.Reach == XRayReach.Unknown ? 2 : 0, 0, 0, 0),
            Background = Ui.Brush("CardHover"), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private static UIElement Spaced(Border tag) { tag.Margin = new Thickness(6, 0, 0, 0); return tag; }
}
