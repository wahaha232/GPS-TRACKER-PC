using System.Net;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Social;

public enum SocialTargetKind
{
    Friend,
    Group,
}

/// <summary>好友或群組的分享連線。Topic／Key 就是邀請碼裡的秘密，不可寫進紀錄或輸出。</summary>
public sealed record SocialTarget(
    SocialTargetKind Kind,
    string Id,
    string Name,
    string Server,
    string Topic,
    string KeyBase64,
    long CreatedAtMs);

/// <summary>透過中繼服務交換的位置快照；Sharing=false 表示對方已停止分享。</summary>
public sealed record SocialSnapshot(
    string DeviceId,
    string DeviceName,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    long SentAtMs,
    bool Sharing)
{
    public Coordinates ToCoordinates() => new(Latitude, Longitude);
}

/// <summary>合併後要顯示在好友／群組列表與地圖上的一筆成員位置。</summary>
public sealed record SocialPeer(
    SocialTargetKind Kind,
    string TargetId,
    string TargetName,
    string DeviceId,
    string DisplayName,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    DateTimeOffset UpdatedAt,
    bool Sharing,
    bool Online,
    double? DistanceMeters);

public sealed class SocialException : Exception
{
    public SocialException(HttpStatusCode? statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>好友分享的固定參數；調整時要一起考慮舊版邀請碼。</summary>
public static class SocialLimits
{
    public const string InvitePrefix = "GTF1-";
    public const string DefaultServer = "https://ntfy.sh";
    public const int MaxTargets = 50;
    public const int MaxNameLength = 60;
    public const int MaxInviteLength = 4096;
    public const int MaxSnapshotsPerTarget = 400;
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
}
