using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Settings;

/// <summary>低電量暫停後，接上電源時要自動繼續還是等使用者按「繼續」。</summary>
public enum BatteryResumeMode
{
    Manual,
    Automatic,
}

public sealed record PowerSettings(
    bool PauseOnLowBattery,
    int ThresholdPercent,
    BatteryResumeMode ResumeMode)
{
    public const int DefaultThresholdPercent = 10;
    public const int MinThresholdPercent = 5;
    public const int MaxThresholdPercent = 50;

    public static PowerSettings Default { get; } =
        new(PauseOnLowBattery: true, DefaultThresholdPercent, BatteryResumeMode.Manual);
}

/// <summary>筆電電量不足時自動暫停模擬的設定；存在 %LOCALAPPDATA%\GpsTrackerPc\power.json。</summary>
public sealed class PowerSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public PowerSettingsStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public PowerSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return PowerSettings.Default;
            }

            var stored = JsonSerializer.Deserialize<StoredPowerSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return new PowerSettings(
                stored?.PauseOnLowBattery ?? PowerSettings.Default.PauseOnLowBattery,
                ClampThreshold(stored?.ThresholdPercent ?? PowerSettings.DefaultThresholdPercent),
                string.Equals(stored?.ResumeMode, "automatic", StringComparison.OrdinalIgnoreCase)
                    ? BatteryResumeMode.Automatic
                    : BatteryResumeMode.Manual);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PowerSettings.Default;
        }
    }

    public void Save(PowerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(
            new StoredPowerSettings
            {
                PauseOnLowBattery = settings.PauseOnLowBattery,
                ThresholdPercent = ClampThreshold(settings.ThresholdPercent),
                ResumeMode = settings.ResumeMode == BatteryResumeMode.Automatic ? "automatic" : "manual",
            },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public static int ClampThreshold(int percent) =>
        Math.Clamp(percent, PowerSettings.MinThresholdPercent, PowerSettings.MaxThresholdPercent);

    private sealed class StoredPowerSettings
    {
        public bool? PauseOnLowBattery { get; set; }

        public int? ThresholdPercent { get; set; }

        public string? ResumeMode { get; set; }
    }
}
