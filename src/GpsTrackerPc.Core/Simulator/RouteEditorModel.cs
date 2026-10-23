using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>
/// 路線編輯的 C# 狀態。地圖只負責顯示與送出操作，實際的點位、順序、停留與設定都以這裡為準。
/// </summary>
public sealed class RouteEditorModel
{
    private readonly List<RouteWaypoint> _waypoints = new();
    private readonly List<RouteOutage> _outages = new();

    public string Id { get; private set; } = Guid.NewGuid().ToString();

    public long CreatedAt { get; private set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string Name { get; set; } = "新路線";

    public double SpeedKph { get; private set; } = 5.0;

    public bool Loop { get; set; }

    public bool NaturalSpeed { get; set; }

    public bool UseGpxTiming { get; private set; }

    public IReadOnlyList<RouteWaypoint> Waypoints => _waypoints;

    public IReadOnlyList<RouteOutage> Outages => _outages;

    public bool HasTiming => SimulationRoute.HasTiming(_waypoints);

    public bool CanUseGpxTiming => HasTiming;

    public string AddWaypoint(
        Coordinates coordinates,
        string? name = null,
        long stopDurationSeconds = 0,
        long? timeOffsetSeconds = null)
    {
        var waypoint = new RouteWaypoint(
            name: string.IsNullOrWhiteSpace(name) ? $"路線點 {_waypoints.Count + 1}" : name.Trim(),
            coordinates: coordinates,
            stopDurationSeconds: stopDurationSeconds,
            timeOffsetSeconds: timeOffsetSeconds);
        _waypoints.Add(waypoint);
        NormalizeTiming();
        return waypoint.Id;
    }

    public bool MoveWaypoint(string id, Coordinates coordinates)
    {
        var index = FindIndex(id);
        if (index < 0)
        {
            return false;
        }

        var current = _waypoints[index];
        _waypoints[index] = current with { Coordinates = coordinates };
        return true;
    }

    public bool SetWaypointStop(string id, long stopDurationSeconds)
    {
        if (stopDurationSeconds is < 0 or > 86_400)
        {
            throw new ArgumentException("停留秒數必須介於 0 到 86400 秒", nameof(stopDurationSeconds));
        }

        var index = FindIndex(id);
        if (index < 0)
        {
            return false;
        }

        var current = _waypoints[index];
        _waypoints[index] = current with { StopDurationSeconds = stopDurationSeconds };
        return true;
    }

    public bool RenameWaypoint(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名稱不能是空白", nameof(name));
        }

        var index = FindIndex(id);
        if (index < 0)
        {
            return false;
        }

        var current = _waypoints[index];
        _waypoints[index] = current with { Name = name.Trim() };
        return true;
    }

    public bool DeleteWaypoint(string id)
    {
        var index = FindIndex(id);
        if (index < 0)
        {
            return false;
        }

        _waypoints.RemoveAt(index);
        NormalizeTiming();
        return true;
    }

    /// <summary>清除全部路線點；GPS 中斷設定與其他路線設定保留。</summary>
    public void ClearWaypoints()
    {
        _waypoints.Clear();
        UseGpxTiming = false;
    }

    /// <summary>把指定點移到新位置；超出範圍會被夾在 0～最後一個索引。</summary>
    public bool ReorderWaypoint(string id, int newIndex)
    {
        var index = FindIndex(id);
        if (index < 0)
        {
            return false;
        }

        var target = Math.Clamp(newIndex, 0, _waypoints.Count - 1);
        if (target == index)
        {
            return true;
        }

        var waypoint = _waypoints[index];
        _waypoints.RemoveAt(index);
        _waypoints.Insert(target, waypoint);
        NormalizeTiming();
        return true;
    }

    public void SetSpeed(double speedKph)
    {
        if (!double.IsFinite(speedKph) || speedKph < SimulationRoute.MinSpeedKph || speedKph > SimulationRoute.MaxSpeedKph)
        {
            throw new ArgumentException("速度必須介於 0.5 到 500 km/h", nameof(speedKph));
        }

        SpeedKph = speedKph;
    }

    public void SetUseGpxTiming(bool value)
    {
        if (value && !CanUseGpxTiming)
        {
            throw new InvalidOperationException("這條路線沒有可用的時間資料，不能依 GPX 時間播放。");
        }

        UseGpxTiming = value;
    }

    public RouteOutage AddOutage(long startSeconds, long durationSeconds)
    {
        var outage = new RouteOutage(startSeconds, durationSeconds);
        _outages.Add(outage);
        _outages.Sort((first, second) => first.StartSeconds.CompareTo(second.StartSeconds));
        return outage;
    }

    public bool RemoveOutage(RouteOutage outage) => _outages.Remove(outage);

    public void Reset()
    {
        _waypoints.Clear();
        _outages.Clear();
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Name = "新路線";
        SpeedKph = 5.0;
        Loop = false;
        NaturalSpeed = false;
        UseGpxTiming = false;
    }

    public void ReplaceRoute(SimulationRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        _waypoints.Clear();
        _waypoints.AddRange(route.Waypoints);
        _outages.Clear();
        _outages.AddRange(route.Outages);
        Id = route.Id;
        CreatedAt = route.CreatedAt;
        Name = route.Name;
        SpeedKph = route.SpeedKph;
        Loop = route.Loop;
        NaturalSpeed = route.NaturalSpeed;
        UseGpxTiming = route.UseGpxTiming && HasTiming;
    }

    public void GenerateRandom(
        Coordinates center,
        int pointCount,
        double radiusMeters,
        RandomRouteShape shape,
        Random? random = null,
        string? name = null)
    {
        var generated = RandomRouteGenerator.Generate(center, pointCount, radiusMeters, shape, random);
        _waypoints.Clear();
        for (var index = 0; index < generated.Count; index++)
        {
            _waypoints.Add(new RouteWaypoint(
                name: $"路線點 {index + 1}",
                coordinates: generated[index]));
        }

        _outages.Clear();
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(name))
        {
            Name = name.Trim();
        }

        Loop = shape == RandomRouteShape.Loop;
        UseGpxTiming = false;
    }

    public SimulationRoute ToRoute()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("路線名稱不能是空白", nameof(Name));
        }

        return new SimulationRoute(
            name: name,
            waypoints: _waypoints.ToList(),
            speedKph: SpeedKph,
            loop: Loop,
            useGpxTiming: UseGpxTiming && HasTiming,
            outages: _outages.ToList(),
            naturalSpeed: NaturalSpeed,
            createdAt: CreatedAt,
            id: Id);
    }

    private int FindIndex(string id) => _waypoints.FindIndex(waypoint => waypoint.Id == id);

    private void NormalizeTiming()
    {
        if (UseGpxTiming && !HasTiming)
        {
            UseGpxTiming = false;
        }
    }
}
