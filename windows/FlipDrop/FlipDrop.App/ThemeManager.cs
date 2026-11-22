using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FlipDrop.App;

/// <summary>
/// Swaps the themeable brush resources (declared in App.xaml, consumed via DynamicResource)
/// between a light and a dark palette at runtime. "System" follows the Windows apps theme
/// read from HKCU\...\Themes\Personalize\AppsUseLightTheme.
/// </summary>
internal static class ThemeManager
{
    public static bool IsDark { get; private set; }

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event Action? ThemeChanged;

    private static readonly (string Key, string Light, string Dark)[] Palette =
    {
        ("WindowBg",     "#FFEFF1F5", "#FF16171B"),
        ("SidebarBg",    "#FFF7F8FA", "#FF1C1D22"),
        ("CardBg",       "#FFFFFFFF", "#FF232429"),
        ("CardBorder",   "#FFE3E6EC", "#FF34363D"),
        ("TextPrimary",  "#FF1C1C1E", "#FFF2F3F5"),
        ("TextSecondary","#FF8A8A8E", "#FF9A9AA2"),
        ("HoverBg",      "#FFECEEF3", "#FF2B2D34"),
        ("SelectBg",     "#FFEAF0FF", "#FF2A3350"),
        ("ControlBg",    "#FFEDEFF4", "#FF202127"),
        ("GhostBg",      "#FFFFFFFF", "#FF2A2B31"),
        ("TrackBg",      "#FFD4D6DC", "#FF3A3C44"),
        ("AmberBg",      "#FFFFF4E0", "#FF3A2E12"),
        ("AmberBorder",  "#FFF2C879", "#FF6E5418"),
        ("AmberText",    "#FF8A5A00", "#FFF2C879"),
    };

    public static bool ResolveDark(string? themeSetting) => themeSetting switch
    {
        "Light" => false,
        "Dark" => true,
        _ => SystemUsesDark(),
    };

    public static void Apply(string? themeSetting)
    {
        IsDark = ResolveDark(themeSetting);
        var res = System.Windows.Application.Current.Resources;
        foreach (var (key, light, dark) in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(IsDark ? dark : light)!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }
        ThemeChanged?.Invoke();
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* registry unavailable — default to light */ }
        return false;
    }
}
