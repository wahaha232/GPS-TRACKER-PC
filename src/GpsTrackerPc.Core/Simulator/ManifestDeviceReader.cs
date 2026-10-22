using System.Text.Json;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>從本機 manifest.json 取出裝置名稱與 source，供設定畫面做重名檢查。</summary>
public static class ManifestDeviceReader
{
    public static IReadOnlyList<ManifestDevice> Read(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return Array.Empty<ManifestDevice>();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ManifestDevice>();
            }

            var devices = new List<ManifestDevice>();
            foreach (var track in tracks.EnumerateArray())
            {
                var deviceName = GetString(track, "deviceName");
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    continue;
                }

                devices.Add(new ManifestDevice(deviceName.Trim(), GetString(track, "source")));
            }

            return devices;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<ManifestDevice>();
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
}
