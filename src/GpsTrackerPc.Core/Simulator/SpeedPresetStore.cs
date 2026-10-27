using System.Globalization;
using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>模擬頁的移動方式速度按鈕（例如「走路 5」）。Key 固定，速度可由使用者自訂。</summary>
public sealed record SpeedPreset(string Key, string Label, double SpeedKph)
{
    public string ButtonText => $"{Label} {SpeedKph.ToString("0.##", CultureInfo.InvariantCulture)}";
}

/// <summary>記住自訂的移動方式速度；存在 %LOCALAPPDATA%\GpsTrackerPc\speed-presets.json。</summary>
public sealed class SpeedPresetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;

    public SpeedPresetStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public static IReadOnlyList<SpeedPreset> Defaults { get; } = new[]
    {
        new SpeedPreset("walk", "走路", 5),
        new SpeedPreset("run", "跑步", 10),
        new SpeedPreset("bike", "騎車", 18),
        new SpeedPreset("car", "開車", 40),
    };

    public static bool IsValidSpeed(double speedKph) =>
        double.IsFinite(speedKph) &&
        speedKph >= SimulationRoute.MinSpeedKph &&
        speedKph <= SimulationRoute.MaxSpeedKph;

    /// <summary>讀取自訂速度；檔案不存在、損毀或某一項不合法時，該項用預設值。</summary>
    public IReadOnlyList<SpeedPreset> Load()
    {
        Dictionary<string, double>? stored = null;
        try
        {
            if (File.Exists(_filePath))
            {
                stored = JsonSerializer.Deserialize<Dictionary<string, double>>(
                    File.ReadAllText(_filePath),
                    SerializerOptions);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            stored = null;
        }

        return Defaults
            .Select(preset =>
                stored != null &&
                stored.TryGetValue(preset.Key, out var speed) &&
                IsValidSpeed(speed)
                    ? preset with { SpeedKph = speed }
                    : preset)
            .ToList();
    }

    /// <summary>只存速度；Key 必須是預設清單裡的項目，速度要在 0.5–500 km/h。</summary>
    public void Save(IReadOnlyList<SpeedPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var values = new Dictionary<string, double>();
        foreach (var preset in presets)
        {
            if (Defaults.All(candidate => candidate.Key != preset.Key))
            {
                throw new ArgumentException("未知的移動方式：" + preset.Key, nameof(presets));
            }

            if (!IsValidSpeed(preset.SpeedKph))
            {
                throw new ArgumentException(
                    $"{preset.Label}的速度必須介於 {SimulationRoute.MinSpeedKph} 到 {SimulationRoute.MaxSpeedKph} km/h",
                    nameof(presets));
            }

            values[preset.Key] = preset.SpeedKph;
        }

        var json = JsonSerializer.Serialize(values, SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>恢復預設：刪掉設定檔。</summary>
    public void Reset()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
