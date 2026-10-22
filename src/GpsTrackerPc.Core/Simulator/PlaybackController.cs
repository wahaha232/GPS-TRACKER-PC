using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.Core.Simulator;

public enum PlaybackState
{
    Idle,
    Playing,
    Paused,
    Stopped,
    Completed,
}

/// <summary>播放取樣點：時間是模擬經過的秒數；中斷期間不會建立取樣點。</summary>
public sealed record PlaybackSample(
    Coordinates Coordinates,
    double ElapsedSeconds,
    float SpeedMetersPerSecond,
    bool IsInOutage);

/// <summary>即時播放與直接產生共用的結果；第 7 階段再轉成網站紀錄，本階段不上傳。</summary>
public sealed record PlaybackResult(
    SimulationRoute Route,
    IReadOnlyList<PlaybackSample> Samples,
    double ElapsedSeconds,
    double DistanceMeters,
    bool Completed,
    int LapsCompleted);

/// <summary>
/// 與畫面分開的播放控制器。所有時間都由外部用 <see cref="Advance(TimeSpan)"/> 注入，
/// 不直接讀系統時間；以模擬秒為單位，每滿一秒取樣一次。
/// </summary>
public sealed class PlaybackController
{
    private const double SampleIntervalSeconds = 1.0;
    private const double LoopStepSeconds = 0.1;
    private const double Epsilon = 1e-9;
    private const int MaxIterations = 1_000_000;

    private readonly SimulationRoute _route;
    private readonly List<PlaybackSample> _samples = new();

    private RouteSimulator? _simulator;
    private RoutePosition? _current;
    private double _nextSampleAt = SampleIntervalSeconds;
    private double _distanceMeters;

    public PlaybackController(SimulationRoute route, int maxLaps = 1)
    {
        _route = route ?? throw new ArgumentNullException(nameof(route));
        if (maxLaps < 1)
        {
            throw new ArgumentException("圈數至少要是 1", nameof(maxLaps));
        }

        MaxLaps = maxLaps;
    }

    public PlaybackState State { get; private set; } = PlaybackState.Idle;

    /// <summary>只影響即時播放的推進速度；不影響每秒取樣的內容。</summary>
    public double SpeedMultiplier { get; private set; } = 1.0;

    public int MaxLaps { get; }

    public IReadOnlyList<PlaybackSample> Samples => _samples;

    public RoutePosition? Current => _current;

    public double DistanceMeters => _distanceMeters;

    public double ElapsedSeconds => _current?.ElapsedSeconds ?? 0.0;

    public bool IsInOutage =>
        _current != null && GpsOutagePolicy.IsActive(_route.Outages, _current.ElapsedSeconds);

    public double Progress
    {
        get
        {
            if (_current == null)
            {
                return 0.0;
            }

            if (!_route.Loop)
            {
                return Math.Clamp(_current.Progress, 0.0, 1.0);
            }

            return Math.Clamp((_current.Lap + _current.Progress) / MaxLaps, 0.0, 1.0);
        }
    }

    public void SetSpeedMultiplier(double multiplier)
    {
        if (multiplier is not (1.0 or 2.0 or 4.0 or 8.0))
        {
            throw new ArgumentException("倍速只能是 1、2、4、8", nameof(multiplier));
        }

        SpeedMultiplier = multiplier;
    }

    public void Start()
    {
        if (State == PlaybackState.Playing)
        {
            throw new InvalidOperationException("播放進行中。");
        }

        _simulator = new RouteSimulator(_route);
        _current = _simulator.Current();
        _samples.Clear();
        _distanceMeters = 0.0;
        _nextSampleAt = SampleIntervalSeconds;
        State = PlaybackState.Playing;
        RecordSample(_current);
    }

    public void Pause()
    {
        if (State == PlaybackState.Playing)
        {
            State = PlaybackState.Paused;
        }
    }

    public void Resume()
    {
        if (State == PlaybackState.Paused)
        {
            State = PlaybackState.Playing;
        }
    }

    public void Stop()
    {
        if (State is PlaybackState.Playing or PlaybackState.Paused)
        {
            State = PlaybackState.Stopped;
        }
    }

    /// <summary>推進真實經過時間；內部會換算成「真實秒數 × 倍速」的模擬秒數。</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (State != PlaybackState.Playing || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var remaining = elapsed.TotalSeconds * SpeedMultiplier;
        if (!(remaining > 0.0) || !double.IsFinite(remaining))
        {
            return;
        }

        var iterations = 0;
        while (remaining > Epsilon && State == PlaybackState.Playing)
        {
            if (iterations++ >= MaxIterations)
            {
                throw new InvalidOperationException("播放推進次數超過上限。");
            }

            var current = _current!;
            var toNext = _nextSampleAt - current.ElapsedSeconds;
            // 循環路線用更小的步長，才能在跑到指定圈數時立刻停下來，不會多跑將近一秒。
            var allowedStep = _route.Loop ? Math.Min(toNext, LoopStepSeconds) : toNext;
            var step = toNext > Epsilon ? Math.Min(remaining, allowedStep) : 0.0;
            RoutePosition next;

            if (step > Epsilon)
            {
                next = _simulator!.Advance(step);
                _distanceMeters += GeoMath.DistanceMeters(current.Coordinates, next.Coordinates);
                _current = next;
                remaining -= step;
            }
            else
            {
                next = current;
            }

            if (next.ElapsedSeconds + Epsilon >= _nextSampleAt)
            {
                RecordSample(next);
                _nextSampleAt += SampleIntervalSeconds;
            }

            if (IsFinished(next))
            {
                RecordFinalSample(next);
                State = PlaybackState.Completed;
                break;
            }
        }
    }

    public PlaybackResult BuildResult() =>
        new(
            _route,
            _samples.ToList(),
            ElapsedSeconds,
            _distanceMeters,
            State == PlaybackState.Completed,
            _current?.Lap ?? 0);

    /// <summary>
    /// 不用等，直接以每秒一步算完整條路線。循環路線由 <paramref name="maxLaps"/> 指定圈數。
    /// </summary>
    public static PlaybackResult GenerateDirect(SimulationRoute route, int maxLaps = 1)
    {
        var controller = new PlaybackController(route, maxLaps);
        controller.Start();

        var iterations = 0;
        while (controller.State == PlaybackState.Playing)
        {
            if (iterations++ >= MaxIterations)
            {
                throw new InvalidOperationException("直接產生超過模擬步數上限。");
            }

            controller.Advance(TimeSpan.FromSeconds(SampleIntervalSeconds));
        }

        return controller.BuildResult();
    }

    private void RecordSample(RoutePosition position)
    {
        if (GpsOutagePolicy.IsActive(_route.Outages, position.ElapsedSeconds))
        {
            return;
        }

        if (_samples.Count > 0 &&
            Math.Abs(_samples[^1].ElapsedSeconds - position.ElapsedSeconds) < 1e-6)
        {
            return;
        }

        _samples.Add(new PlaybackSample(
            position.Coordinates,
            position.ElapsedSeconds,
            position.SpeedMetersPerSecond,
            IsInOutage: false));
    }

    private void RecordFinalSample(RoutePosition position)
    {
        if (_samples.Count == 0 ||
            Math.Abs(_samples[^1].ElapsedSeconds - position.ElapsedSeconds) > 1e-6)
        {
            RecordSample(position);
        }
    }

    private bool IsFinished(RoutePosition position) =>
        _route.Loop
            ? position.Lap >= MaxLaps
            : position.IsComplete;
}
