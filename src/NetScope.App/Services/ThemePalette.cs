using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using NetScope.Core.Models;

namespace NetScope.App.Services;

public static class ThemePalette
{
    public static void Apply(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark || theme == AppTheme.System && SystemPrefersDark();
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        Set("CanvasBrush", dark ? "#111722" : "#F3F6FA");
        Set("SidebarBrush", dark ? "#151E2B" : "#EDF3F9");
        Set("SurfaceBrush", dark ? "#1B2533" : "#FFFFFF");
        Set("SurfaceAltBrush", dark ? "#202C3C" : "#F7F9FC");
        Set("BorderBrush", dark ? "#344154" : "#DCE3EB");
        Set("TextPrimaryBrush", dark ? "#F2F5F9" : "#172033");
        Set("TextSecondaryBrush", dark ? "#B7C1CF" : "#667085");
        Set("TextMutedBrush", dark ? "#7F8A9A" : "#98A2B3");
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static void Set(string key, string color) => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
}
