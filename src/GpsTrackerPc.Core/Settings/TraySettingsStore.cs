using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Settings;

public sealed record TraySettings(bool MinimizeToTray);

/// <summary>視窗縮小後是否收到工具列圖示區（系統匣）的設定。</summary>
public sealed class TraySettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public TraySettingsStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public TraySettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new TraySettings(MinimizeToTray: false);
            }

            var stored = JsonSerializer.Deserialize<StoredTraySettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return new TraySettings(stored?.MinimizeToTray ?? false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TraySettings(MinimizeToTray: false);
        }
    }

    public void Save(TraySettings settings)
    {
        var json = JsonSerializer.Serialize(
            new StoredTraySettings { MinimizeToTray = settings.MinimizeToTray },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public void SetMinimizeToTray(bool enabled) => Save(new TraySettings(enabled));

    private sealed class StoredTraySettings
    {
        public bool? MinimizeToTray { get; set; }
    }
}
