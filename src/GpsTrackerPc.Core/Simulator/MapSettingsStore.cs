using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Simulator;

public enum BasemapKind
{
    Simple,
    Satellite,
    Nlsc,
    None,
}

public sealed record BasemapOption(
    BasemapKind Kind,
    string Value,
    string Label,
    string Attribution);

public static class BasemapCatalog
{
    public static BasemapOption Simple { get; } = new(
        BasemapKind.Simple,
        "simple",
        "簡易地圖（OpenFreeMap）",
        "© OpenFreeMap · © OpenMapTiles · © OpenStreetMap contributors");

    public static BasemapOption Satellite { get; } = new(
        BasemapKind.Satellite,
        "satellite",
        "衛星（Esri）",
        "Esri, Maxar, Earthstar Geographics, GIS User Community");

    public static BasemapOption Nlsc { get; } = new(
        BasemapKind.Nlsc,
        "nlsc",
        "台灣電子地圖（NLSC）",
        "內政部國土測繪中心（NLSC）通用版電子地圖");

    public static BasemapOption None { get; } = new(
        BasemapKind.None,
        "none",
        "無底圖",
        "無底圖");

    public static IReadOnlyList<BasemapOption> Options { get; } = new[]
    {
        Simple,
        Satellite,
        Nlsc,
        None,
    };

    public static BasemapOption Default => Simple;

    public static BasemapOption Get(string? value) =>
        Options.FirstOrDefault(option =>
            string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? Simple;
}

public sealed record SimulatorMapSettings(BasemapKind Basemap, bool Follow);

/// <summary>記住模擬視窗的底圖與跟隨模式；存在 %LOCALAPPDATA%\GpsTrackerPc\map-settings.json。</summary>
public sealed class MapSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public MapSettingsStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public SimulatorMapSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new SimulatorMapSettings(BasemapCatalog.Default.Kind, Follow: true);
            }

            var settings = JsonSerializer.Deserialize<StoredMapSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return new SimulatorMapSettings(
                BasemapCatalog.Get(settings?.Basemap).Kind,
                settings?.Follow ?? true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SimulatorMapSettings(BasemapCatalog.Default.Kind, Follow: true);
        }
    }

    public void Save(SimulatorMapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var option = BasemapCatalog.Options.First(candidate => candidate.Kind == settings.Basemap);
        var json = JsonSerializer.Serialize(
            new StoredMapSettings
            {
                Basemap = option.Value,
                Follow = settings.Follow,
            },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    private sealed class StoredMapSettings
    {
        public string? Basemap { get; set; }

        public bool? Follow { get; set; }
    }
}
