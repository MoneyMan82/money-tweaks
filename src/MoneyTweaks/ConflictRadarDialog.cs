using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoneyTweaks.Controls;
using MoneyTweaks.Core.Engine;

namespace MoneyTweaks;

/// <summary>
/// Tweak Conflict Radar's pre-apply dialog: each conflict with its evidence and the ways to resolve it. The first choice of
/// every conflict never changes the system; nothing happens until the user confirms.
/// </summary>
public static class ConflictRadarDialog
{
    /// <summary>The chosen fix per conflict, or null when cancelled.</summary>
    public static Dictionary<RadarConflict, RadarFix>? Show(IReadOnlyList<RadarConflict> conflicts, string continueText)
    {
        var choice = conflicts.ToDictionary(c => c, c => c.Fixes[0]);
        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Foreground = (Brush)Application.Current.Resources["Text"],
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 660, ShowInTaskbar = false, Title = "Conflict Radar",
            WindowStartupLocation = Application.Current.MainWindow?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null,
        };
        bool ok = false;
        win.Content = Build(conflicts, continueText, choice, () => { ok = true; win.Close(); }, win.Close);
        win.Loaded += (_, _) => Motion.PopIn((FrameworkElement)win.Content);
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
        win.ShowDialog();
        return ok ? choice : null;
    }

    /// <summary>The dialog's content; <paramref name="choice"/> is updated as options are picked.</summary>
    public static Border Build(IReadOnlyList<RadarConflict> conflicts, string continueText, Dictionary<RadarConflict, RadarFix> choice, Action onContinue, Action onCancel)
    {
        Brush Res(string k) => (Brush)Application.Current.Resources[k];
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "CONFLICT RADAR", Style = Ui.Res<Style>("Eyebrow") });
        int contra = conflicts.Count(c => c.Kind == RadarKind.Contradiction);
        body.Children.Add(new TextBlock
        {
            Text = conflicts.Count == 1 ? "1 conflict found before applying" : $"{conflicts.Count} conflicts found before applying",
            Style = Ui.Res<Style>("H2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
        });
        body.Children.Add(Ui.Muted("Found by comparing the settings each tweak actually writes. Nothing has been changed. Pick how to handle each one; the first option never changes your system."
            + (contra > 0 ? $" {contra} contradiction(s) can't be applied together as they are." : ""), 12.5));

        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        int n = 0;
        foreach (var c in conflicts)
        {
            var card = new StackPanel();
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(Ui.Tag(c.KindLabel.ToUpperInvariant(), c.Kind switch { RadarKind.Contradiction => "Danger", RadarKind.Duplicate => "Violet", _ => "Gold" }));
            var who = new TextBlock { Text = $"{c.Selected.Name}  ↔  {c.Other.Name}{(c.OtherApplied ? " (applied)" : "")}", FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            head.Children.Add(who);
            card.Children.Add(head);
            card.Children.Add(new TextBlock { Text = c.Explanation, FontSize = 12.5, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
            foreach (var e in c.Evidence)
                card.Children.Add(new TextBlock { Text = "• " + e, FontSize = 11.5, FontFamily = Ui.Res<FontFamily>("MonoFont"), Foreground = Res("Subtle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            var group = "radar" + n++;
            var fixes = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var f in c.Fixes)
            {
                var rb = new RadioButton
                {
                    GroupName = group, IsChecked = f == c.Fixes[0], Margin = new Thickness(0, 2, 0, 2), Foreground = Res("Text"),
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 540, Inlines = { new System.Windows.Documents.Run(f.Label) { FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run((f.ChangesSystem ? " · changes your system after a second confirmation. " : " · ") + f.Detail) { Foreground = Res(f.ChangesSystem ? "Gold" : "Muted") } } },
                };
                rb.Checked += (_, _) => choice[c] = f;
                fixes.Children.Add(rb);
            }
            card.Children.Add(fixes);
            list.Children.Add(new Border { Child = card, BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8) });
        }
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true };
        cancel.Click += (_, _) => onCancel();
        var go = new Button { Content = continueText, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Style = Ui.Res<Style>("PrimaryButton") };
        go.Click += (_, _) => onContinue();
        buttons.Children.Add(cancel); buttons.Children.Add(go);
        body.Children.Add(buttons);

        return new Border
        {
            Background = Res("CardFill"), BorderBrush = Res("CardRim"), BorderThickness = new Thickness(1), Padding = new Thickness(24, 22, 24, 22), Margin = new Thickness(14),
            Child = body, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 32, ShadowDepth = 0, Opacity = 0.65 },
        };
    }
}
