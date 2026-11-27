using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tabrilo.Windows;

internal static class Theme
{
    internal static double TextScale { get; private set; } = 1;

    public static void Apply(string appearance, double? textScale = null)
    {
        TextScale = Math.Clamp(textScale ?? new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor, 1, 2.25);
        Application.Current.Resources["BodyFontSize"] = 14 * TextScale;
        Application.Current.Resources["TitleFontSize"] = 20 * TextScale;
        using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = appearance == "Dark" || appearance == "System" && Equals(personalize?.GetValue("AppsUseLightTheme"), 0);
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("Tabrilo.Tokens.json")!;
        using var json = JsonDocument.Parse(stream);
        var palette = json.RootElement.GetProperty(dark ? "dark" : "light");
        foreach (var entry in json.RootElement.GetProperty("wheel").EnumerateObject())
            Application.Current.Resources["Wheel" + entry.Name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Value.GetString()!));
        foreach (var entry in palette.EnumerateObject())
        {
            var color = (Color)ColorConverter.ConvertFromString(entry.Value.GetString()!);
            Application.Current.Resources[entry.Name] = new SolidColorBrush(color);
        }
        if (SystemParameters.HighContrast)
        {
            var colors = new Dictionary<string, Color> {
                ["Background"] = SystemColors.WindowColor, ["Surface"] = SystemColors.WindowColor,
                ["Text"] = SystemColors.WindowTextColor, ["Secondary"] = SystemColors.WindowTextColor,
                ["Accent"] = SystemColors.HighlightColor, ["OnAccent"] = SystemColors.HighlightTextColor,
                ["Hover"] = SystemColors.WindowColor, ["Border"] = SystemColors.WindowTextColor,
                ["Error"] = SystemColors.WindowTextColor
            };
            foreach (var (key, color) in colors) Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }
}
