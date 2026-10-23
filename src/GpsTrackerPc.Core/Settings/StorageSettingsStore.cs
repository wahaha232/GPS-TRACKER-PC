using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Settings;

public sealed record StorageSettings(string? DataDirectory);

/// <summary>
/// 本機資料夾位置設定。設定檔固定在 %LOCALAPPDATA%\GpsTrackerPc，
/// 避免資料夾改到別處後找不到自己的設定。
/// </summary>
public sealed class StorageSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public StorageSettingsStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public StorageSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new StorageSettings(DataDirectory: null);
            }

            var stored = JsonSerializer.Deserialize<StoredStorageSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            var path = stored?.DataDirectory;
            return new StorageSettings(string.IsNullOrWhiteSpace(path) ? null : path.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StorageSettings(DataDirectory: null);
        }
    }

    public void Save(StorageSettings settings)
    {
        var json = JsonSerializer.Serialize(
            new StoredStorageSettings { DataDirectory = settings.DataDirectory },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public void SetDataDirectory(string? path) =>
        Save(new StorageSettings(string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path)));

    private sealed class StoredStorageSettings
    {
        public string? DataDirectory { get; set; }
    }
}
