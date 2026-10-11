using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MoneyTweaks;

public enum DialogKind { Info, Warning, Error, Confirm }

/// <summary>Themed modal dialog used for confirmations, warnings and errors.</summary>
public static class Dialog
{
    public static bool Show(string title, string message, DialogKind kind = DialogKind.Info, string ok = "OK", string? cancel = null)
    {
        var accent = kind switch
        {
            DialogKind.Error => (Brush)Application.Current.Resources["Danger"],
            DialogKind.Warning => (Brush)Application.Current.Resources["Gold"],
            _ => (Brush)Application.Current.Resources["Accent"],
        };
        var icon = kind switch { DialogKind.Error => "", DialogKind.Warning => "", DialogKind.Confirm => "", _ => "" };

        var win = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Foreground = (Brush)Application.Current.Resources["Text"],
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 460, ShowInTaskbar = false,
            WindowStartupLocation = Application.Current.MainWindow?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = Application.Current.MainWindow?.IsVisible == true ? Application.Current.MainWindow : null,
            Title = title,
        };
        bool result = false;
        var okBtn = new Button { Content = ok, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true,
            Style = (Style)Application.Current.Resources[kind is DialogKind.Warning ? "GoldButton" : kind is DialogKind.Error ? "DangerButton" : "PrimaryButton"] };
        okBtn.Click += (_, _) => { result = true; win.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (cancel is not null)
        {
            var c = new Button { Content = cancel, MinWidth = 96, IsCancel = true };
            c.Click += (_, _) => win.Close();
            buttons.Children.Add(c);
        }
        buttons.Children.Add(okBtn);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = icon, Style = (Style)Application.Current.Resources["Icon"], Foreground = accent, FontSize = 20, Margin = new Thickness(0, 0, 10, 0) });
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H2"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 13, LineHeight = 19 }
        });
        body.Children.Add(buttons);

        win.Content = new Border
        {
            Background = (Brush)Application.Current.Resources["Surface"], BorderBrush = accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(22), Margin = new Thickness(12), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 }
        };
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
        win.ShowDialog();
        return result;
    }

    public static bool Confirm(string title, string message, string ok = "Continue", DialogKind kind = DialogKind.Confirm) =>
        Show(title, message, kind, ok, "Cancel");
}
