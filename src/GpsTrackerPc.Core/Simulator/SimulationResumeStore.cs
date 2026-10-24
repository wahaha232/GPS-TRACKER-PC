using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>
/// 重啟程式前暫存的模擬狀態：路線、播放位置與已產生的取樣點。
/// 使用者下次啟動時可以選擇繼續、存檔後結束或放棄。
/// </summary>
public sealed record SimulationResumeSnapshot(
    string RouteJson,
    double ElapsedSeconds,
    double DistanceMeters,
    int Laps,
    double SpeedMultiplier,
    bool WasPlaying,
    IReadOnlyList<PlaybackSample> Samples,
    long SavedAtMs);

/// <summary>把重啟前的模擬狀態存在 %LOCALAPPDATA%\GpsTrackerPc\restart-session.json。</summary>
public sealed class SimulationResumeStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly string _filePath;

    public SimulationResumeStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public SimulationResumeSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<SimulationResumeSnapshot>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return snapshot?.Samples is { Count: >= 2 } && !string.IsNullOrWhiteSpace(snapshot.RouteJson)
                ? snapshot
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public void Save(SimulationResumeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉不影響下次啟動；舊資料由 Load 驗證。
        }
    }
}
