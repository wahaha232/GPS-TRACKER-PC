using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>路線上的停靠點。</summary>
public sealed record RouteWaypoint
{
    private const long MaxStopDurationSeconds = 86_400L;

    public RouteWaypoint(
        string name,
        Coordinates coordinates,
        long stopDurationSeconds = 0,
        long? timeOffsetSeconds = null,
        string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名稱不能是空白", nameof(name));
        }

        if (stopDurationSeconds is < 0 or > MaxStopDurationSeconds)
        {
            throw new ArgumentException("停留秒數必須介於 0 到 86400 秒", nameof(stopDurationSeconds));
        }

        if (timeOffsetSeconds is < 0)
        {
            throw new ArgumentException("時間偏移必須不小於 0", nameof(timeOffsetSeconds));
        }

        Id = id ?? Guid.NewGuid().ToString();
        Name = name;
        Coordinates = coordinates ?? throw new ArgumentNullException(nameof(coordinates));
        StopDurationSeconds = stopDurationSeconds;
        TimeOffsetSeconds = timeOffsetSeconds;
    }

    public string Id { get; init; }

    public string Name { get; init; }

    public Coordinates Coordinates { get; init; }

    public long StopDurationSeconds { get; init; }

    /// <summary>從路線起點算起的時間（秒）；來自 GPX 的時間戳記，沒有就是 null。</summary>
    public long? TimeOffsetSeconds { get; init; }
}

/// <summary>
/// 模擬 GPS 中斷（例如搭捷運、進隧道）：播放到第 <see cref="StartSeconds"/> 秒起，
/// 連續 <see cref="DurationSeconds"/> 秒不送出任何位置，但模擬的人仍然照常往前走。
/// </summary>
public sealed record RouteOutage
{
    public const long MaxDurationSeconds = 86_400L;

    public RouteOutage(long startSeconds, long durationSeconds)
    {
        if (startSeconds < 0)
        {
            throw new ArgumentException("中斷開始時間必須不小於 0", nameof(startSeconds));
        }

        if (durationSeconds is < 1 or > MaxDurationSeconds)
        {
            throw new ArgumentException("中斷秒數必須介於 1 到 86400 秒", nameof(durationSeconds));
        }

        StartSeconds = startSeconds;
        DurationSeconds = durationSeconds;
    }

    public long StartSeconds { get; init; }

    public long DurationSeconds { get; init; }

    public long EndSeconds => StartSeconds + DurationSeconds;
}

/// <summary>判斷某個時間點是否落在任何一段 GPS 中斷內。</summary>
public static class GpsOutagePolicy
{
    public static bool IsActive(IReadOnlyList<RouteOutage> outages, double elapsedSeconds) =>
        outages.Any(outage => elapsedSeconds >= outage.StartSeconds && elapsedSeconds < outage.EndSeconds);
}

/// <summary>模擬用的路線設定。</summary>
public sealed record SimulationRoute
{
    public const double MinSpeedKph = 0.5;
    public const double MaxSpeedKph = 500.0;

    public SimulationRoute(
        string name,
        IReadOnlyList<RouteWaypoint> waypoints,
        double speedKph,
        bool loop,
        bool useGpxTiming = false,
        IReadOnlyList<RouteOutage>? outages = null,
        bool naturalSpeed = false,
        long? createdAt = null,
        string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名稱不能是空白", nameof(name));
        }

        if (waypoints is null || waypoints.Count < 2)
        {
            throw new ArgumentException("路線至少需要兩個點", nameof(waypoints));
        }

        if (!double.IsFinite(speedKph) || speedKph < MinSpeedKph || speedKph > MaxSpeedKph)
        {
            throw new ArgumentException("速度必須介於 0.5 到 500 km/h", nameof(speedKph));
        }

        if (waypoints.Select(waypoint => waypoint.Coordinates).Distinct().Count() < 2)
        {
            throw new ArgumentException("路線至少需要兩個不同的座標", nameof(waypoints));
        }

        if (useGpxTiming && !HasTiming(waypoints))
        {
            throw new ArgumentException("依時間播放的路線，每個點都必須有遞增的時間戳記", nameof(waypoints));
        }

        Id = id ?? Guid.NewGuid().ToString();
        Name = name;
        Waypoints = waypoints;
        SpeedKph = speedKph;
        Loop = loop;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        UseGpxTiming = useGpxTiming;
        Outages = outages ?? Array.Empty<RouteOutage>();
        NaturalSpeed = naturalSpeed;
    }

    public string Id { get; init; }

    public string Name { get; init; }

    public IReadOnlyList<RouteWaypoint> Waypoints { get; init; }

    public double SpeedKph { get; init; }

    public bool Loop { get; init; }

    public long CreatedAt { get; init; }

    /// <summary>依各點的時間戳記播放；false 就用 <see cref="SpeedKph"/> 等速。</summary>
    public bool UseGpxTiming { get; init; }

    public IReadOnlyList<RouteOutage> Outages { get; init; }

    /// <summary>等速模式下讓速度自然起伏；依時間戳記播放時不套用。</summary>
    public bool NaturalSpeed { get; init; }

    /// <summary>每個點都有時間、時間不倒退，而且總時間大於 0，才能依時間戳記播放。</summary>
    public static bool HasTiming(IReadOnlyList<RouteWaypoint> waypoints)
    {
        if (waypoints.Count < 2)
        {
            return false;
        }

        var times = new List<long>(waypoints.Count);
        foreach (var waypoint in waypoints)
        {
            if (waypoint.TimeOffsetSeconds is not { } time)
            {
                return false;
            }

            times.Add(time);
        }

        for (var index = 0; index < times.Count - 1; index++)
        {
            if (times[index + 1] < times[index])
            {
                return false;
            }
        }

        return times[^1] > times[0];
    }

    // Kotlin 的 data class 會逐項比較清單內容，這裡補上相同的語意。
    public bool Equals(SimulationRoute? other) =>
        other is not null &&
        Id == other.Id &&
        Name == other.Name &&
        SpeedKph.Equals(other.SpeedKph) &&
        Loop == other.Loop &&
        CreatedAt == other.CreatedAt &&
        UseGpxTiming == other.UseGpxTiming &&
        NaturalSpeed == other.NaturalSpeed &&
        Waypoints.SequenceEqual(other.Waypoints) &&
        Outages.SequenceEqual(other.Outages);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(SpeedKph);
        hash.Add(Loop);
        hash.Add(CreatedAt);
        hash.Add(UseGpxTiming);
        hash.Add(NaturalSpeed);
        foreach (var waypoint in Waypoints)
        {
            hash.Add(waypoint);
        }

        foreach (var outage in Outages)
        {
            hash.Add(outage);
        }

        return hash.ToHashCode();
    }
}
