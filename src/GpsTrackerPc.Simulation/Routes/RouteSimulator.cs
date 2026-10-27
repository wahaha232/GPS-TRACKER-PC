using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>模擬器輸出的目前位置。</summary>
public sealed record RoutePosition(
    Coordinates Coordinates,
    float SpeedMetersPerSecond,
    float BearingDegrees,
    double Progress,
    int CurrentWaypointIndex,
    int Lap,
    bool IsStopped,
    bool IsComplete,
    double ElapsedSeconds);

/// <summary>
/// 路線播放器。和 Kotlin 版相同的速度公式與邊界條件，
/// 依 GPX 時間戳記播放或依固定速度前進。
/// 非循環路線走到終點就停在終點；循環路線到終點後沿原路折返回起點，
/// 一趟「起點 → 終點 → 起點」算一圈，不會從終點直線連回起點。
/// </summary>
public sealed class RouteSimulator
{
    private const int MaxAdvanceIterations = 10_000;
    private const double StoppedSpeedMps = 0.05;

    private readonly SimulationRoute _route;
    private readonly double _speedMetersPerSecond;

    /// <summary>依時間戳記播放：每一段走多久由 GPX 決定，速度因此隨段落變化（含原地停留）。</summary>
    private readonly bool _timed;

    /// <summary>
    /// 實際走的路線點順序（路線點索引）。非循環：0..n-1；
    /// 循環（折返）：0..n-1..0，最後一個回到起點時算完成一圈。
    /// </summary>
    private readonly List<int> _path = new();

    /// <summary>第 i 段是 _path[i] → _path[i + 1]。</summary>
    private readonly List<double> _legDistances = new();
    private readonly double _totalDistance;
    private readonly List<double> _timedLegSeconds = new();

    private double _timeIntoLeg;

    /// <summary>目前這一步實際使用的速度（等速模式下，自然起伏會讓它隨時間變化）。</summary>
    private double _effectiveSpeed;

    private int _pathIndex;
    private double _distanceIntoLeg;
    private double _distanceCompletedInLap;
    private double _stopRemainingSeconds;
    private double _elapsedSeconds;
    private int _lap;
    private bool _complete;

    public RouteSimulator(SimulationRoute route)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _speedMetersPerSecond = route.SpeedKph / 3.6;

        _timed = route.UseGpxTiming && SimulationRoute.HasTiming(route.Waypoints);

        var count = route.Waypoints.Count;
        for (var index = 0; index < count; index++)
        {
            _path.Add(index);
        }

        if (route.Loop)
        {
            for (var index = count - 2; index >= 0; index--)
            {
                _path.Add(index);
            }
        }

        for (var index = 0; index < _path.Count - 1; index++)
        {
            _legDistances.Add(
                GeoMath.DistanceMeters(
                    route.Waypoints[_path[index]].Coordinates,
                    route.Waypoints[_path[index + 1]].Coordinates));
        }

        _totalDistance = _legDistances.Sum();

        if (_timed)
        {
            // 折返段沿用去程同一段的 GPX 時間差，所以回程速度和去程一樣。
            for (var index = 0; index < _path.Count - 1; index++)
            {
                _timedLegSeconds.Add(Math.Abs(
                    route.Waypoints[_path[index + 1]].TimeOffsetSeconds!.Value -
                    route.Waypoints[_path[index]].TimeOffsetSeconds!.Value));
            }
        }

        _effectiveSpeed = _speedMetersPerSecond * SpeedFactor(0.0);

        EstimatedTotalSeconds = route.Loop
            ? (double?)null
            : _timed
                ? _timedLegSeconds.Sum()
                : _totalDistance / _speedMetersPerSecond +
                  route.Waypoints.Sum(waypoint => (double)waypoint.StopDurationSeconds);

        _stopRemainingSeconds = _timed
            ? 0.0
            : route.Waypoints[0].StopDurationSeconds;
    }

    /// <summary>預估整趟要多久（秒）；循環路線由呼叫端決定圈數，回傳 null。</summary>
    public double? EstimatedTotalSeconds { get; }

    private int LastPathIndex => _path.Count - 1;

    public RoutePosition Current() => BuildPosition();

    public RoutePosition Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
        {
            throw new ArgumentException("經過時間必須是有限且不小於 0 的數值", nameof(deltaSeconds));
        }

        if (_timed)
        {
            return AdvanceTimed(deltaSeconds);
        }

        _effectiveSpeed = _speedMetersPerSecond * SpeedFactor(_elapsedSeconds);
        var remainingSeconds = deltaSeconds;
        var iterations = 0;
        while (remainingSeconds > 1e-9 && !_complete)
        {
            if (iterations++ >= MaxAdvanceIterations)
            {
                throw new InvalidOperationException("Route contains too many zero-length steps");
            }

            if (_stopRemainingSeconds > 0)
            {
                var consumed = Math.Min(_stopRemainingSeconds, remainingSeconds);
                _stopRemainingSeconds -= consumed;
                remainingSeconds -= consumed;
                _elapsedSeconds += consumed;
                if (_stopRemainingSeconds > 1e-9)
                {
                    break;
                }
            }

            if (!_route.Loop && _pathIndex == LastPathIndex)
            {
                _complete = true;
                break;
            }

            var legDistance = _legDistances[_pathIndex];
            var remainingLegDistance = Math.Max(legDistance - _distanceIntoLeg, 0.0);
            if (remainingLegDistance <= 1e-6)
            {
                ArriveAt(_pathIndex + 1, legDistance);
                continue;
            }

            var travelSeconds = remainingLegDistance / _effectiveSpeed;
            if (remainingSeconds < travelSeconds)
            {
                _distanceIntoLeg += remainingSeconds * _effectiveSpeed;
                _elapsedSeconds += remainingSeconds;
                remainingSeconds = 0.0;
            }
            else
            {
                remainingSeconds -= travelSeconds;
                _elapsedSeconds += travelSeconds;
                ArriveAt(_pathIndex + 1, legDistance);
            }
        }

        return BuildPosition();
    }

    /// <summary>
    /// 自然起伏：起步 6 秒內由 35% 加速到全速，之後在 ±17% 內緩慢變化
    /// （幾個週期不同的正弦疊加，固定不隨機）。只用在等速模式。
    /// </summary>
    private double SpeedFactor(double elapsed)
    {
        if (!_route.NaturalSpeed || _timed)
        {
            return 1.0;
        }

        var wobble = 1.0 + 0.10 * Math.Sin(elapsed / 9.0) +
            0.05 * Math.Sin(elapsed / 3.7 + 1.3) +
            0.02 * Math.Sin(elapsed / 1.9 + 0.4);
        var ramp = Math.Clamp(0.35 + 0.65 * (elapsed / 6.0), 0.35, 1.0);
        return Math.Clamp(wobble, 0.8, 1.2) * ramp;
    }

    private RoutePosition AdvanceTimed(double deltaSeconds)
    {
        var remainingSeconds = deltaSeconds;
        var iterations = 0;
        while (remainingSeconds > 1e-9 && !_complete)
        {
            if (iterations++ >= MaxAdvanceIterations)
            {
                throw new InvalidOperationException("Route contains too many zero-length steps");
            }

            if (!_route.Loop && _pathIndex == LastPathIndex)
            {
                _complete = true;
                break;
            }

            var legIndex = _pathIndex;
            var legSeconds = _timedLegSeconds[legIndex];
            var secondsLeft = Math.Max(legSeconds - _timeIntoLeg, 0.0);
            if (secondsLeft <= 1e-9)
            {
                ArriveTimed(_pathIndex + 1, _legDistances[legIndex]);
                continue;
            }

            if (remainingSeconds < secondsLeft)
            {
                _timeIntoLeg += remainingSeconds;
                _elapsedSeconds += remainingSeconds;
                remainingSeconds = 0.0;
                _distanceIntoLeg = _legDistances[legIndex] * (_timeIntoLeg / legSeconds);
            }
            else
            {
                remainingSeconds -= secondsLeft;
                _elapsedSeconds += secondsLeft;
                ArriveTimed(_pathIndex + 1, _legDistances[legIndex]);
            }
        }

        return BuildPosition();
    }

    private void ArriveTimed(int targetPathIndex, double completedLegDistance)
    {
        ArriveAt(targetPathIndex, completedLegDistance);
        _timeIntoLeg = 0.0;
        _stopRemainingSeconds = 0.0;
        if (!_route.Loop && targetPathIndex == LastPathIndex)
        {
            _complete = true;
        }
    }

    private void ArriveAt(int targetPathIndex, double completedLegDistance)
    {
        _distanceCompletedInLap += completedLegDistance;
        _pathIndex = targetPathIndex;
        _distanceIntoLeg = 0.0;
        _stopRemainingSeconds = _route.Waypoints[_path[targetPathIndex]].StopDurationSeconds;
        if (_route.Loop && targetPathIndex == LastPathIndex)
        {
            // 折返回到起點：完成一圈，從頭再走一次。
            _lap += 1;
            _distanceCompletedInLap = 0.0;
            _pathIndex = 0;
        }
        else if (!_route.Loop &&
                 targetPathIndex == LastPathIndex &&
                 _stopRemainingSeconds == 0.0)
        {
            _complete = true;
        }
    }

    private RoutePosition BuildPosition()
    {
        var currentWaypointIndex = _path[_pathIndex];
        var currentWaypoint = _route.Waypoints[currentWaypointIndex];
        var atTerminal = !_route.Loop && _pathIndex == LastPathIndex;
        var targetWaypoint = atTerminal
            ? currentWaypoint
            : _route.Waypoints[_path[_pathIndex + 1]];
        var legIndex = atTerminal ? -1 : _pathIndex;
        var legDistance = legIndex >= 0 ? _legDistances[legIndex] : 0.0;
        var fraction = legDistance <= 1e-9 ? 0.0 : _distanceIntoLeg / legDistance;
        var coordinates = legIndex < 0 || _stopRemainingSeconds > 0
            ? currentWaypoint.Coordinates
            : GeoMath.Interpolate(currentWaypoint.Coordinates, targetWaypoint.Coordinates, fraction);
        var progress = _complete
            ? 1.0
            : _totalDistance <= 1e-9
                ? 0.0
                : Math.Clamp((_distanceCompletedInLap + _distanceIntoLeg) / _totalDistance, 0.0, 1.0);
        var legSpeed = _timed && legIndex >= 0
            ? _timedLegSeconds[legIndex] > 1e-9
                ? legDistance / _timedLegSeconds[legIndex]
                : 0.0
            : _effectiveSpeed;
        var isStopped = _stopRemainingSeconds > 1e-9 || _complete ||
            (_timed && legSpeed < StoppedSpeedMps);
        return new RoutePosition(
            coordinates,
            isStopped ? 0f : (float)legSpeed,
            isStopped || legIndex < 0
                ? 0f
                : GeoMath.InitialBearingDegrees(currentWaypoint.Coordinates, targetWaypoint.Coordinates),
            progress,
            currentWaypointIndex,
            _lap,
            isStopped,
            _complete,
            _elapsedSeconds);
    }
}
