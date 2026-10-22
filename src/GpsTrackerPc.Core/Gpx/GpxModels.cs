using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpsTrackerPc.Core.Gpx;

/// <summary>GPX 裡的單一軌跡點；TimeMs 用 NaN 表示沒有可用時間（和網頁版的 isFinite 判斷一致）。</summary>
public sealed record GpxPoint(double Lat, double Lon, double? Elevation, double TimeMs);

/// <summary>從 GPX 解析出來的原始內容。</summary>
public sealed record GpxDocument(string? Name, IReadOnlyList<GpxPoint> Points);

/// <summary>建立網站紀錄時的選項。</summary>
public sealed record GpxBuildOptions
{
    public string? Id { get; init; }

    public string? Mode { get; init; } = "UNKNOWN";
}

/// <summary>轉換結果；Error 不為 null 時代表不能匯入。</summary>
public sealed record GpxBuildResult(
    GpxTrack? Track,
    GpxTrackEntry? Entry,
    GpxImportSummary? Summary,
    string? Error)
{
    public bool IsSuccess => Error == null;

    public static GpxBuildResult Fail(string error) => new(null, null, null, error);
}

/// <summary>匯入前的摘要，顯示在畫面上讓使用者確認。</summary>
public sealed record GpxImportSummary(
    string? Name,
    int Points,
    int SkippedNoTime,
    long StartTimeMs,
    double DistanceMeters,
    long DurationMs,
    bool HasAltitude);

/// <summary>防重複比對只需要開始時間與距離。</summary>
public interface ITrackIdentity
{
    long StartTimeMs { get; }

    double DistanceMeters { get; }
}

public sealed record TrackIdentity(long StartTimeMs, double DistanceMeters) : ITrackIdentity;

/// <summary>寫入 data/&lt;trackId&gt;.json 的網站紀錄格式（和 gt-gpx.js build() 相同）。</summary>
public sealed class GpxTrack
{
    public string TrackId { get; init; } = string.Empty;

    public long StartTimeMs { get; set; }

    public long EndTimeMs { get; init; }

    public long DurationMs { get; init; }

    public double DistanceMeters { get; init; }

    public double AvgSpeedMps { get; init; }

    public double MaxSpeedMps { get; init; }

    public long StepCount { get; init; }

    public string StepSource { get; init; } = "NONE";

    public string DominantMode { get; init; } = "UNKNOWN";

    public int GpsPointCount { get; init; }

    public long PhotoCount { get; init; }

    public double TotalAscentMeters { get; init; }

    public double TotalDescentMeters { get; init; }

    public double? MaxAltitudeMeters { get; init; }

    public double? MinAltitudeMeters { get; init; }

    /// <summary>每點 [lon, lat, ele, km/h, 從起點算起的秒數]。</summary>
    public List<double[]> Points { get; init; } = new();

    public List<object[]> Photos { get; init; } = new();

    /// <summary>[[模式, 起秒, 迄秒]]；匯入時整條路線只有一段。</summary>
    public List<object[]> Segments { get; init; } = new();

    public string Source { get; set; } = "gpx-import";

    /// <summary>模擬紀錄標記為 PC；一般 GPX 匯入沒有這個欄位。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }
}

/// <summary>manifest.json 裡的一筆紀錄摘要。</summary>
public sealed class GpxTrackEntry : ITrackIdentity
{
    public string TrackId { get; init; } = string.Empty;

    public long StartTimeMs { get; set; }

    public double DistanceMeters { get; init; }

    public long DurationMs { get; init; }

    public double AvgSpeedMps { get; init; }

    public double MaxSpeedMps { get; init; }

    public long StepCount { get; init; }

    public string DominantMode { get; init; } = "UNKNOWN";

    public double TotalAscentMeters { get; init; }

    public double TotalDescentMeters { get; init; }

    public long PhotoCount { get; init; }

    public string File { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}

/// <summary>匯出時只需要從紀錄 JSON 讀出的欄位。</summary>
public sealed class GpxTrackDocument
{
    public long StartTimeMs { get; set; }

    public List<double[]>? Points { get; set; }
}

public static class GpxErrors
{
    public static string Get(string code) => code switch
    {
        "no-points" => "檔案裡找不到軌跡點（需要 GPX 的 <trkpt>）。",
        "no-time" => "這個 GPX 沒有時間資訊，無法計算活動時間與速度，所以不能匯入。",
        "too-few" => "有時間資訊的軌跡點少於 2 個，無法匯入。",
        _ => "這個 GPX 無法匯入。",
    };
}

public static class GpxJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string SerializeTrack(GpxTrack track) => JsonSerializer.Serialize(track, Options);

    public static string SerializeEntry(GpxTrackEntry entry) => JsonSerializer.Serialize(entry, Options);
}
