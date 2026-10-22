using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Appearance;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>記住使用者的主題選擇（淺色／深色／系統）。</summary>
public sealed class ThemeSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public ThemeSettingsStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public AppThemePreference Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppThemePreference.System;
            }

            var stored = JsonSerializer.Deserialize<StoredThemeSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return Parse(stored?.Theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppThemePreference.System;
        }
    }

    public void Save(AppThemePreference preference)
    {
        var json = JsonSerializer.Serialize(
            new StoredThemeSettings { Theme = ToValue(preference) },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public static AppThemePreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppThemePreference.Light,
        "dark" => AppThemePreference.Dark,
        "system" => AppThemePreference.System,
        _ => AppThemePreference.System,
    };

    public static string ToValue(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => "light",
        AppThemePreference.Dark => "dark",
        _ => "system",
    };

    private sealed class StoredThemeSettings
    {
        public string? Theme { get; set; }
    }
}
