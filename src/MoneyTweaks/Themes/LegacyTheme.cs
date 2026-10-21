using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using MoneyTweaks.Core.Sys;

namespace MoneyTweaks;

/// <summary>
/// Legacy UI: the original Money Tweaks look (versions 1.0–1.3, before the "Midnight Circuit" redesign): green accents,
/// flat cards without rim lighting and no ambient glow. It swaps the theme's colour resources before any window or style
/// is created, from a recoloured copy of the theme, so every control picks them up; switching needs a restart.
/// </summary>
public static class LegacyTheme
{
    // The current palette's colours → the original theme's (1.0), keyed by RGB; alpha is kept. Colours added since 1.0
    // get the closest original equivalent.
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["05070B"] = "080C0A", ["0A0D13"] = "0F1512", ["10141C"] = "141C18", ["151B26"] = "19231E", ["1D2432"] = "223029",
        ["4C8DFF"] = "2BE38B", ["2F6BE0"] = "17A865", ["8B6CFF"] = "14B8A6", ["F5B84A"] = "F5C451", ["3DDC97"] = "2BE38B",
        ["FF5C7A"] = "FF5D6C", ["E9EEF7"] = "E8F1EC", ["8A94A8"] = "8A9A92", ["5B6478"] = "5E6E66", ["BFD5FF"] = "BFF5DC",
        ["A8B8D8"] = "A8D8C0", ["2A3550"] = "2E4A3C", ["2B3550"] = "2E4A3C",
    };

    // Flat cards and hero (no rim light or gradient), as in the original look.
    private static readonly Dictionary<string, string> FlatBrushes = new()
    {
        ["CardFill"] = "#FF141C18", ["CardRim"] = "#FF223029", ["CardRimHover"] = "#FF2E4A3C", ["HeroFill"] = "#FF0F1512",
    };

    public static bool Active { get; private set; }

    /// <summary>
    /// Converts the theme's XAML to the legacy look: mapped accent colours, the cool graphite greys tinted green, flat
    /// card/hero brushes and dark text on accent buttons. Pure text transform (no WPF), so it can be checked directly.
    /// </summary>
    public static string Convert(string xaml)
    {
        foreach (var (key, color) in FlatBrushes)
            xaml = Regex.Replace(xaml, $@"(<LinearGradientBrush x:Key=""{key}""[\s\S]*?</LinearGradientBrush>)",
                m => Regex.Replace(m.Value, @"Color=""#[0-9A-Fa-f]{8}""", $@"Color=""{color}"""));
        xaml = Regex.Replace(xaml, @"<SolidColorBrush x:Key=""OnAccent"" Color=""#[0-9A-Fa-f]{8}""/>", @"<SolidColorBrush x:Key=""OnAccent"" Color=""#FF04140C""/>");
        return Regex.Replace(xaml, @"#([0-9A-Fa-f]{2})([0-9A-Fa-f]{6})\b", m => "#" + m.Groups[1].Value + MapRgb(m.Groups[2].Value));
    }

    private static string MapRgb(string rgb)
    {
        if (Map.TryGetValue(rgb, out var to)) return to;
        int r = System.Convert.ToInt32(rgb[..2], 16), g = System.Convert.ToInt32(rgb[2..4], 16), b = System.Convert.ToInt32(rgb[4..], 16);
        // Dark blue-tinted greys (control surfaces, borders) become the original's green-tinted greys.
        if (Math.Max(r, Math.Max(g, b)) > 0x70 || b < r || b < g) return rgb;
        double lum = 0.3 * r + 0.59 * g + 0.11 * b;
        static int Cl(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);
        return $"{Cl(lum * 0.86):X2}{Cl(lum * 1.14):X2}{Cl(lum * 0.98):X2}";
    }

    /// <summary>Call once at startup, before any window or style exists: swaps the theme for its legacy copy.</summary>
    public static void Apply(ResourceDictionary app)
    {
        try
        {
            using var stream = typeof(LegacyTheme).Assembly.GetManifestResourceStream("MoneyTweaks.Themes.Theme.xaml")
                ?? throw new InvalidOperationException("theme source isn't embedded");
            using var reader = new StreamReader(stream);
            var legacy = (ResourceDictionary)XamlReader.Parse(Convert(reader.ReadToEnd()));
            var i = app.MergedDictionaries.ToList().FindIndex(d => d.Contains("Accent"));
            if (i < 0) app.MergedDictionaries.Add(legacy); else app.MergedDictionaries[i] = legacy;
            Active = true;
            Log.Info("Legacy UI is on (original look)");
        }
        catch (Exception ex) { Log.Error("Legacy UI couldn't be applied; using the current look", ex); }
    }

    /// <summary>Starts a new copy of Money Tweaks and closes this one, so a look change takes effect.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            App.Settings.Save();
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex) { Dialog.Show("Couldn't restart Money Tweaks", ex.Message + "\n\nClose and open Money Tweaks to see the change.", DialogKind.Error); }
    }
}
