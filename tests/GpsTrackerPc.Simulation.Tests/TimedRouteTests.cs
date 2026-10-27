using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class TimedRouteTests
{
    // 赤道上每 0.001 度經度約 111.2 公尺。
    private static RouteWaypoint Waypoint(double lon, long? timeOffsetSeconds) =>
        new(name: "p", coordinates: new Coordinates(0.0, lon), timeOffsetSeconds: timeOffsetSeconds);

    private static SimulationRoute TimedRoute(
        (double Lon, long Time)[] points,
        bool loop = false,
        bool useTiming = true) =>
        new(
            name: "r",
            waypoints: points.Select(point => Waypoint(point.Lon, point.Time)).ToList(),
            speedKph: 30.0,
            loop: loop,
            useGpxTiming: useTiming);

    private static SimulationRoute FixedRoute(bool natural, bool loop = false) =>
        new(
            name: "r",
            // 約 1.1 公里
            waypoints: new List<RouteWaypoint>
            {
                Waypoint(0.0, null),
                Waypoint(0.01, null),
            },
            speedKph: 5.0,
            loop: loop,
            naturalSpeed: natural);

    [Fact]
    public void SpeedFollowsEachLegsOwnTiming()
    {
        // 第一段 111 公尺花 100 秒（約 1.1 m/s），第二段 111 公尺花 50 秒（約 2.2 m/s）
        var route = TimedRoute(new[] { (0.0, 0L), (0.001, 100L), (0.002, 150L) });
        var simulator = new RouteSimulator(route);
        var first = simulator.Advance(50.0);
        TestAssert.Close(1.112, first.SpeedMetersPerSecond, 0.02);
        var second = simulator.Advance(75.0); // 第 125 秒：在第二段中間
        TestAssert.Close(2.224, second.SpeedMetersPerSecond, 0.04);
        TestAssert.Close(125.0, second.ElapsedSeconds, 1e-6);
        // 第二段走了一半 → 經度約 0.0015
        TestAssert.Close(0.0015, second.Coordinates.Longitude, 0.00005);
    }

    [Fact]
    public void FinishesExactlyWhenTheRecordedTimeIsUsedUp()
    {
        var simulator = new RouteSimulator(TimedRoute(new[] { (0.0, 0L), (0.001, 100L), (0.002, 150L) }));
        Assert.False(simulator.Advance(149.0).IsComplete);
        var done = simulator.Advance(2.0);
        Assert.True(done.IsComplete);
        TestAssert.Close(0.002, done.Coordinates.Longitude, 1e-9);
    }

    [Fact]
    public void TimeSpentStandingStillKeepsThePositionAndReportsStopped()
    {
        // 第二段兩點座標相同、花 60 秒 → 原地停留
        var route = new SimulationRoute(
            name: "r",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint(0.0, 0L),
                Waypoint(0.001, 100L),
                Waypoint(0.001, 160L),
                Waypoint(0.002, 200L),
            },
            speedKph: 30.0,
            loop: false,
            useGpxTiming: true);
        var simulator = new RouteSimulator(route);
        var stopped = simulator.Advance(130.0);
        Assert.True(stopped.IsStopped);
        Assert.Equal(0f, stopped.SpeedMetersPerSecond);
        TestAssert.Close(0.001, stopped.Coordinates.Longitude, 1e-9);
        TestAssert.Close(130.0, stopped.ElapsedSeconds, 1e-6);
        var moving = simulator.Advance(50.0);
        Assert.False(moving.IsStopped);
    }

    [Fact]
    public void FixedSpeedIsUsedWhenTimingIsSwitchedOff()
    {
        var route = TimedRoute(new[] { (0.0, 0L), (0.001, 100L), (0.002, 150L) }, useTiming: false);
        var position = new RouteSimulator(route).Advance(10.0);
        // 30 km/h ≈ 8.33 m/s，不受 GPX 時間影響
        TestAssert.Close(8.333, position.SpeedMetersPerSecond, 0.01);
    }

    [Fact]
    public void HasTimingNeedsEveryPointStampedAndNonDecreasing()
    {
        Assert.True(SimulationRoute.HasTiming(new[] { Waypoint(0.0, 0), Waypoint(0.001, 10) }));
        Assert.False(SimulationRoute.HasTiming(new[] { Waypoint(0.0, 0), Waypoint(0.001, null) }));
        Assert.False(SimulationRoute.HasTiming(new[] { Waypoint(0.0, 10), Waypoint(0.001, 5) }));
        Assert.False(SimulationRoute.HasTiming(new[] { Waypoint(0.0, 5), Waypoint(0.001, 5) }));
    }

    [Fact]
    public void LoopRouteReturnsWithEachLegsOwnTiming()
    {
        // 去程：第一段 100 秒（慢）、第二段 10 秒（快）。折返時沿用同一段的時間差。
        var route = TimedRoute(new[] { (0.0, 0L), (0.001, 100L), (0.002, 110L) }, loop: true);
        var simulator = new RouteSimulator(route);
        simulator.Advance(110.0); // 走完去程

        var fastReturn = simulator.Advance(5.0); // 終點 → 中間點（10 秒那段）
        Assert.False(fastReturn.IsComplete);
        TestAssert.Close(11.12, fastReturn.SpeedMetersPerSecond, 0.2);
        TestAssert.Close(0.0015, fastReturn.Coordinates.Longitude, 1e-5);

        var slowReturn = simulator.Advance(55.0); // 中間點 → 起點（100 秒那段）
        Assert.Equal(0, slowReturn.Lap);
        TestAssert.Close(1.112, slowReturn.SpeedMetersPerSecond, 0.02);
        TestAssert.Close(0.0005, slowReturn.Coordinates.Longitude, 1e-5);

        var lapDone = simulator.Advance(50.5);
        Assert.Equal(1, lapDone.Lap);
    }

    [Fact]
    public void OutagePolicyCoversOnlyTheConfiguredWindow()
    {
        var outages = new[] { new RouteOutage(startSeconds: 60, durationSeconds: 30) };
        Assert.False(GpsOutagePolicy.IsActive(outages, 59.9));
        Assert.True(GpsOutagePolicy.IsActive(outages, 60.0));
        Assert.True(GpsOutagePolicy.IsActive(outages, 89.9));
        Assert.False(GpsOutagePolicy.IsActive(outages, 90.0));
        Assert.False(GpsOutagePolicy.IsActive(Array.Empty<RouteOutage>(), 70.0));
    }

    [Fact]
    public void PositionKeepsAdvancingDuringAnOutageSoItJumpsAfterwards()
    {
        // 模擬器本身不看中斷：中斷只決定「要不要送出」，人照樣前進。
        var route = TimedRoute(new[] { (0.0, 0L), (0.002, 200L) });
        var simulator = new RouteSimulator(route);
        var before = simulator.Advance(50.0).Coordinates.Longitude;
        var after = simulator.Advance(100.0).Coordinates.Longitude; // 假設第 50~150 秒中斷
        TestAssert.Close(0.0005, before, 0.00002);
        TestAssert.Close(0.0015, after, 0.00002);
    }

    [Fact]
    public void OutageRejectsInvalidRanges()
    {
        Assert.NotNull(new RouteOutage(0, 1));
        Assert.Throws<ArgumentException>(() => new RouteOutage(-1, 10));
        Assert.Throws<ArgumentException>(() => new RouteOutage(0, 0));
    }

    // ---- GPX 匯入與儲存 ----
    private const string GpxSample = """
        <?xml version="1.0"?>
        <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1"><trk><name>t</name><trkseg>
        <trkpt lat="25.0000" lon="121.0000"><time>2026-10-15T06:00:00Z</time></trkpt>
        <trkpt lat="25.0000" lon="121.0010"><time>2026-10-15T06:01:40Z</time></trkpt>
        <trkpt lat="25.0000" lon="121.0020"><time>2026-10-15T06:02:30.500+00:00</time></trkpt>
        </trkseg></trk></gpx>
        """;

    [Fact]
    public void GpxWithTimeStampsImportsAsTimedRoute()
    {
        var route = LocationDocumentCodec.Import(GpxSample, "fallback");
        Assert.True(route.UseGpxTiming);
        Assert.Equal(new long?[] { 0L, 100L, 150L }, route.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));
    }

    [Fact]
    public void GpxWithoutTimeStampsStaysFixedSpeed()
    {
        var plain = Regex.Replace(GpxSample, "<time>.*?</time>", string.Empty);
        var route = LocationDocumentCodec.Import(plain, "fallback");
        Assert.False(route.UseGpxTiming);
        Assert.All(route.Waypoints, waypoint => Assert.Null(waypoint.TimeOffsetSeconds));
    }

    [Fact]
    public void ThinningKeepsFirstAndLastAndStaysWithinTheLimit()
    {
        var kept = LocationDocumentCodec.ThinIndices(10_000, 3000);
        Assert.True(kept.Count <= 3000);
        Assert.Equal(0, kept[0]);
        Assert.Equal(9_999, kept[^1]);
        Assert.Equal(kept.OrderBy(index => index).ToList(), kept);
        Assert.Equal(Enumerable.Range(0, 50), LocationDocumentCodec.ThinIndices(50, 3000));
    }

    [Fact]
    public void JsonRoundTripKeepsTimingAndOutagesAndOldDataStillLoads()
    {
        var route = new SimulationRoute(
            name: "r",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint(0.0, 0L),
                Waypoint(0.001, 100L),
            },
            speedKph: 5.0,
            loop: false,
            useGpxTiming: true,
            outages: new[] { new RouteOutage(10, 20), new RouteOutage(50, 5) });
        var decoded = RouteJsonCodec.Decode(RouteJsonCodec.Encode(route))!;
        Assert.True(decoded.UseGpxTiming);
        Assert.Equal(route.Outages, decoded.Outages);
        Assert.Equal(new long?[] { 0L, 100L }, decoded.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));

        // 舊版儲存的資料沒有這些欄位：照樣能讀，視為等速、沒有中斷
        var legacyNode = JsonNode.Parse(RouteJsonCodec.Encode(route))!.AsObject();
        legacyNode.Remove("useGpxTiming");
        legacyNode.Remove("outages");
        foreach (var waypoint in legacyNode["waypoints"]!.AsArray())
        {
            waypoint!.AsObject().Remove("timeOffsetSeconds");
        }

        var old = RouteJsonCodec.Decode(legacyNode.ToJsonString())!;
        Assert.False(old.UseGpxTiming);
        Assert.Empty(old.Outages);
    }

    // ---- 自然起伏與預估時間 ----
    [Fact]
    public void NaturalSpeedRampsUpFromASlowStartAndThenVariesWithinBounds()
    {
        var simulator = new RouteSimulator(FixedRoute(natural: true));
        var speeds = Enumerable.Range(1, 120)
            .Select(_ => simulator.Advance(1.0).SpeedMetersPerSecond)
            .ToArray();
        var baseSpeed = 5.0 / 3.6;
        Assert.True(speeds[0] < baseSpeed * 0.7, "起步比全速慢");
        Assert.True(
            speeds.Skip(10).All(speed => speed >= baseSpeed * 0.79 && speed <= baseSpeed * 1.21),
            "之後都在 ±20% 內");
        Assert.True(speeds.Skip(10).Max() - speeds.Skip(10).Min() > baseSpeed * 0.05, "速度確實有起伏");
    }

    [Fact]
    public void NaturalSpeedIsOffByDefaultAndForTimedRoutes()
    {
        var plain = new RouteSimulator(FixedRoute(natural: false));
        var speeds = Enumerable.Range(1, 20)
            .Select(_ => plain.Advance(1.0).SpeedMetersPerSecond)
            .ToArray();
        Assert.All(speeds, speed => Assert.True(Math.Abs(speed - 5.0 / 3.6) < 1e-6));

        var timed = new SimulationRoute(
            name: "r",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint(0.0, 0L),
                Waypoint(0.001, 100L),
            },
            speedKph: 5.0,
            loop: false,
            useGpxTiming: true,
            naturalSpeed: true);
        var timedSimulator = new RouteSimulator(timed);
        var first = timedSimulator.Advance(10.0).SpeedMetersPerSecond;
        var later = timedSimulator.Advance(40.0).SpeedMetersPerSecond;
        TestAssert.Close(first, later, 1e-6);
    }

    [Fact]
    public void NaturalSpeedStillReachesTheEndAndStaysDeterministic()
    {
        double Run()
        {
            var simulator = new RouteSimulator(FixedRoute(natural: true));
            var steps = 0;
            while (!simulator.Advance(1.0).IsComplete)
            {
                steps++;
            }

            return steps;
        }

        var a = Run();
        TestAssert.Close(a, Run(), 0.0);
        // 全速需要約 800 秒（1112 m / 1.389 m/s）；自然起伏平均略慢（含起步），但不應差太多
        Assert.True(a is >= 780.0 and <= 900.0, $"steps={a}");
    }

    [Fact]
    public void EstimatedTotalTimeCoversTimedFixedAndLoopRoutes()
    {
        TestAssert.Close(
            150.0,
            new RouteSimulator(TimedRoute(new[] { (0.0, 0L), (0.001, 100L), (0.002, 150L) })).EstimatedTotalSeconds!.Value,
            1e-6);
        var fixedEstimate = new RouteSimulator(FixedRoute(natural: false)).EstimatedTotalSeconds!.Value;
        TestAssert.Close(1111.95 / (5.0 / 3.6), fixedEstimate, 5.0);
        Assert.Null(new RouteSimulator(FixedRoute(natural: false, loop: true)).EstimatedTotalSeconds);
    }

    [Fact]
    public void JsonKeepsNaturalSpeedFlag()
    {
        var route = FixedRoute(natural: true);
        Assert.True(RouteJsonCodec.Decode(RouteJsonCodec.Encode(route))!.NaturalSpeed);
        Assert.False(RouteJsonCodec.Decode(RouteJsonCodec.Encode(FixedRoute(natural: false)))!.NaturalSpeed);
    }
}
