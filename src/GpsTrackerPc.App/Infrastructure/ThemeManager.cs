using System.Windows;
using System.Windows.Media;
using GpsTrackerPc.Core.Appearance;
using Microsoft.Win32;

namespace GpsTrackerPc.App.Infrastructure;

internal sealed record ThemeOption(AppThemePreference Value, string Label);

internal static class ThemeCatalog
{
    public static IReadOnlyList<ThemeOption> Options { get; } = new[]
    {
        new ThemeOption(AppThemePreference.System, "系統預設"),
        new ThemeOption(AppThemePreference.Light, "淺色"),
        new ThemeOption(AppThemePreference.Dark, "深色"),
    };

    public static ThemeOption Get(string? value) =>
        Options.FirstOrDefault(option =>
            string.Equals(AppThemePreferenceValue(option.Value), value, StringComparison.OrdinalIgnoreCase))
        ?? Options[0];

    public static string AppThemePreferenceValue(AppThemePreference value) => value switch
    {
        AppThemePreference.Light => "light",
        AppThemePreference.Dark => "dark",
        _ => "system",
    };
}

/// <summary>把主題色套到 Application.Resources；XAML 使用 DynamicResource，切換時立即生效。</summary>
internal static class ThemeManager
{
    public static void Apply(AppThemePreference preference)
    {
        if (Application.Current == null)
        {
            return;
        }

        var effective = preference == AppThemePreference.System
            ? (SystemUsesLightTheme() ? AppThemePreference.Light : AppThemePreference.Dark)
            : preference;
        var colors = effective == AppThemePreference.Light ? LightColors : DarkColors;
        foreach (var pair in colors)
        {
            Application.Current.Resources[pair.Key] = new SolidColorBrush(ParseColor(pair.Value));
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int intValue && intValue != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Color ParseColor(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static readonly IReadOnlyDictionary<string, string> DarkColors =
        new Dictionary<string, string>
        {
            ["AppBackground"] = "#0B1220",
            ["AppSurface"] = "#111A2E",
            ["AppSurfaceAlt"] = "#0F172A",
            ["AppBorder"] = "#1E293B",
            ["AppBorderStrong"] = "#334155",
            ["AppText"] = "#E2E8F0",
            ["AppTextSoft"] = "#CBD5E1",
            ["AppTextMuted"] = "#94A3B8",
            ["AppAccent"] = "#38BDF8",
            ["AppSuccess"] = "#22C55E",
            ["AppSuccessText"] = "#86EFAC",
            ["AppDangerText"] = "#FCA5A5",
            ["AppDangerSurface"] = "#3B1D1D",
            ["AppDangerBorder"] = "#7F1D1D",
            ["AppWarningText"] = "#FCD34D",
            ["AppOverlayBackground"] = "#CC0B1220",
        };

    private static readonly IReadOnlyDictionary<string, string> LightColors =
        new Dictionary<string, string>
        {
            ["AppBackground"] = "#F8FAFC",
            ["AppSurface"] = "#FFFFFF",
            ["AppSurfaceAlt"] = "#F1F5F9",
            ["AppBorder"] = "#E2E8F0",
            ["AppBorderStrong"] = "#CBD5E1",
            ["AppText"] = "#0F172A",
            ["AppTextSoft"] = "#334155",
            ["AppTextMuted"] = "#64748B",
            ["AppAccent"] = "#0284C7",
            ["AppSuccess"] = "#16A34A",
            ["AppSuccessText"] = "#15803D",
            ["AppDangerText"] = "#B91C1C",
            ["AppDangerSurface"] = "#FEE2E2",
            ["AppDangerBorder"] = "#FCA5A5",
            ["AppWarningText"] = "#B45309",
            ["AppOverlayBackground"] = "#E6FFFFFF",
        };
}
