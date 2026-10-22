using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>
/// 本機裝置名稱設定。預設是 PC，但上傳時一律讀這裡的值，不在程式各處寫死。
/// </summary>
public sealed class DeviceNameStore
{
    public const string DefaultName = "PC";
    private const int MaxLength = 80;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public DeviceNameStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public string Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return DefaultName;
            }

            var settings = JsonSerializer.Deserialize<DeviceNameSettings>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return Normalize(settings?.DeviceName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return DefaultName;
        }
    }

    public void Save(string name)
    {
        var normalized = Validate(name);
        var json = JsonSerializer.Serialize(
            new DeviceNameSettings { DeviceName = normalized },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public static string Validate(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("裝置名稱不能是空白", nameof(name));
        }

        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException($"裝置名稱不能超過 {MaxLength} 個字", nameof(name));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("裝置名稱不能包含控制字元", nameof(name));
        }

        return normalized;
    }

    private static string Normalize(string? name) => (name ?? string.Empty).Trim();

    private sealed class DeviceNameSettings
    {
        public string? DeviceName { get; set; }
    }
}
