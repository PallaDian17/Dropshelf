using System.Windows;
using System.Windows.Media;
using Dropshelf.Core;
using Microsoft.Win32;

namespace Dropshelf.UI;

/// <summary>Follows the Windows light/dark app mode and accent color, like Dropover follows macOS appearance.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ResourceDictionary? _palette;

    public static bool IsDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static bool SystemUsesLightTaskbar() => ReadDword("SystemUsesLightTheme", 0) == 1;

    private static void Apply()
    {
        var app = Application.Current;
        if (app is null) return;

        IsDark = ReadDword("AppsUseLightTheme", 1) == 0;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Dropshelf;component/UI/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        // Later merged dictionaries win, so the palette must replace the one App.xaml loaded
        // (at the same position) rather than be inserted in front of it.
        var merged = app.Resources.MergedDictionaries;
        var index = _palette is not null
            ? merged.IndexOf(_palette)
            : merged.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Themes/Dark.xaml") == true
                                             || d.Source?.OriginalString.Contains("Themes/Light.xaml") == true);
        if (index >= 0) merged[index] = palette;
        else merged.Insert(0, palette);
        _palette = palette;

        var accent = ReadAccent();
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = Frozen(accent);
        app.Resources["SelectionBrush"] = Frozen(Color.FromArgb(IsDark ? (byte)0x55 : (byte)0x40, accent.R, accent.G, accent.B));
        app.Resources["AccentTextBrush"] = Frozen(Luminance(accent) > 0.6 ? Colors.Black : Colors.White);

        ThemeChanged?.Invoke();
    }

    private static Color ReadAccent()
    {
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            var c = settings.GetColorValue(IsDark ? Windows.UI.ViewManagement.UIColorType.AccentLight1 : Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }
        catch (Exception ex)
        {
            Log.Warn($"Accent color unavailable: {ex.Message}");
            return Color.FromRgb(0x0A, 0x84, 0xFF);
        }
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
