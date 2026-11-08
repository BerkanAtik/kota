using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Kota.App;

static class Theme
{
    public static readonly Color Green = Color.FromRgb(0x2E, 0xA0, 0x43);
    public static readonly Color Amber = Color.FromRgb(0xD2, 0x99, 0x22);
    public static readonly Color Red = Color.FromRgb(0xDA, 0x36, 0x33);

    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void Apply(ResourceDictionary r)
    {
        bool dark = IsDark();
        Set(r, "Bg", dark ? "#202020" : "#F3F3F3");
        Set(r, "Card", dark ? "#2B2B2B" : "#FFFFFF");
        Set(r, "Fg", dark ? "#F3F3F3" : "#1B1B1B");
        Set(r, "Sub", dark ? "#A6A6A6" : "#616161");
        Set(r, "Line", dark ? "#383838" : "#E0E0E0");
        Set(r, "Track", dark ? "#3E3E3E" : "#E8E8E8");
        Set(r, "Hover", dark ? "#3A3A3A" : "#E9E9E9");
        Set(r, "ClaudeAccent", "#D97757");
        Set(r, "CodexAccent", dark ? "#8C96F7" : "#5B67E8");
    }

    public static Color SeverityColor(double percent) => percent >= 90 ? Red : percent >= 70 ? Amber : Green;

    public static SolidColorBrush SeverityBrush(double percent)
    {
        var brush = new SolidColorBrush(SeverityColor(percent));
        brush.Freeze();
        return brush;
    }

    static void Set(ResourceDictionary r, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        r[key] = brush;
    }
}
