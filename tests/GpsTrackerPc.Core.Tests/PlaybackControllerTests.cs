using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class PlaybackControllerTests
{
    [Fact]
    public void SpeedMultiplierChangesTheSimulatedTimePerAdvance()
    {
        var controller = new PlaybackController(CreateRoute());
        controller.Start();
        controller.SetSpeedMultiplier(4);

        controller.Advance(TimeSpan.FromMilliseconds(250)); // 4 倍速 × 0.25 秒 = 1 模擬秒

        TestAssert.Close(1.0, controller.ElapsedSeconds, 1e-6);
        Assert.Equal(2, controller.Samples.Count); // 0 秒與 1 秒

        controller.SetSpeedMultiplier(8);
        controller.Advance(TimeSpan.FromMilliseconds(125)); // 8 倍速 × 0.125 秒 = 1 模擬秒

        TestAssert.Close(2.0, controller.ElapsedSeconds, 1e-6);
        Assert.Equal(3, controller.Samples.Count);
    }

    [Fact]
    public void PauseStopsAdvancingAndResumeContinues()
    {
        var controller = new PlaybackController(CreateRoute());
        controller.Start();
        controller.Advance(TimeSpan.FromSeconds(1));
        var pausedAt = controller.ElapsedSeconds;

        controller.Pause();
        controller.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaybackState.Paused, controller.State);
        TestAssert.Close(pausedAt, controller.ElapsedSeconds, 1e-6);
        Assert.Equal(2, controller.Samples.Count);

        controller.Resume();
        controller.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackState.Playing, controller.State);
        TestAssert.Close(2.0, controller.ElapsedSeconds, 1e-6);
        Assert.Equal(3, controller.Samples.Count);
    }

    [Fact]
    public void OutagePeriodDoesNotCreateSamplesButStillShowsOutageState()
    {
        var route = CreateRoute(outages: new[] { new RouteOutage(3, 2) });
        var controller = new PlaybackController(route);
        controller.Start();
        controller.Advance(TimeSpan.FromSeconds(4.5));

        Assert.True(controller.IsInOutage, "第 4.5 秒應該落在中斷期間");
        Assert.DoesNotContain(
            controller.Samples,
            sample => sample.ElapsedSeconds >= 3.0 && sample.ElapsedSeconds < 5.0);
        Assert.All(controller.Samples, sample => Assert.False(sample.IsInOutage));

        var result = controller.BuildResult();
        Assert.DoesNotContain(
            result.Samples,
            sample => sample.ElapsedSeconds >= 3.0 && sample.ElapsedSeconds < 5.0);
    }

    [Fact]
    public void DirectGenerationMatchesRealtimePlayback()
    {
        var route = CreateRoute();
        var direct = PlaybackController.GenerateDirect(route);

        var realtime = new PlaybackController(route);
        realtime.Start();
        for (var index = 0; index < 80 && realtime.State == PlaybackState.Playing; index++)
        {
            realtime.Advance(TimeSpan.FromMilliseconds(250));
        }

        var played = realtime.BuildResult();

        Assert.True(direct.Completed);
        Assert.True(played.Completed);
        AssertSamplesEqual(direct.Samples, played.Samples);
        TestAssert.Close(direct.ElapsedSeconds, played.ElapsedSeconds, 1e-6);
        TestAssert.Close(direct.DistanceMeters, played.DistanceMeters, 1e-6);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(500)]
    public void DifferentUiTickIntervalsProduceTheSameSamples(int tickMilliseconds)
    {
        var route = CreateRoute();
        var direct = PlaybackController.GenerateDirect(route);

        var realtime = new PlaybackController(route);
        realtime.Start();
        while (realtime.State == PlaybackState.Playing)
        {
            realtime.Advance(TimeSpan.FromMilliseconds(tickMilliseconds));
        }

        AssertSamplesEqual(direct.Samples, realtime.BuildResult().Samples);
    }

    [Fact]
    public void DirectGenerationMatchesRealtimeWithOutage()
    {
        var route = CreateRoute(outages: new[] { new RouteOutage(3, 2) });
        var direct = PlaybackController.GenerateDirect(route);

        var realtime = new PlaybackController(route);
        realtime.Start();
        for (var index = 0; index < 80 && realtime.State == PlaybackState.Playing; index++)
        {
            realtime.Advance(TimeSpan.FromMilliseconds(250));
        }

        AssertSamplesEqual(direct.Samples, realtime.BuildResult().Samples);
        Assert.DoesNotContain(
            direct.Samples,
            sample => sample.ElapsedSeconds >= 3.0 && sample.ElapsedSeconds < 5.0);
    }

    [Fact]
    public void LoopRouteStopsAfterTheRequestedNumberOfLaps()
    {
        var route = CreateRoute(loop: true);
        var result = PlaybackController.GenerateDirect(route, maxLaps: 2);

        Assert.True(result.Completed);
        Assert.True(result.LapsCompleted >= 2);
        Assert.True(result.Samples.Count > 20);
        var last = result.Samples[^1].Coordinates;
        var start = route.Waypoints[0].Coordinates;
        // 圈數邊界可能落在 0.1 秒的子步之間，允許不到一秒的些微超出。
        Assert.True(GeoMath.DistanceMeters(last, start) < 5.0);
    }

    [Fact]
    public void StopKeepsSamplesButMarksResultIncomplete()
    {
        var controller = new PlaybackController(CreateRoute());
        controller.Start();
        controller.Advance(TimeSpan.FromSeconds(2));
        controller.Stop();

        var result = controller.BuildResult();

        Assert.Equal(PlaybackState.Stopped, controller.State);
        Assert.False(result.Completed);
        Assert.True(result.Samples.Count >= 3);
        Assert.True(result.ElapsedSeconds > 0);
    }

    [Fact]
    public void InvalidLapsAndSpeedMultiplierAreRejected()
    {
        var route = CreateRoute();
        Assert.Throws<ArgumentException>(() => new PlaybackController(route, 0));

        var controller = new PlaybackController(route);
        Assert.Throws<ArgumentException>(() => controller.SetSpeedMultiplier(3));
        Assert.Throws<ArgumentException>(() => controller.SetSpeedMultiplier(0));
    }


    [Fact]
    public void RestoreContinuesFromSnapshotWithoutDuplicatingSamples()
    {
        var route = CreateRoute();
        var original = new PlaybackController(route);
        original.SetSpeedMultiplier(2);
        original.Start();
        original.Advance(TimeSpan.FromSeconds(3));
        var elapsed = original.ElapsedSeconds;
        var samples = original.Samples.ToList();

        var snapshot = new SimulationResumeSnapshot(
            RouteJsonCodec.Encode(route),
            elapsed,
            original.DistanceMeters,
            1,
            2,
            WasPlaying: true,
            samples,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var restored = PlaybackController.Restore(route, snapshot);

        Assert.Equal(PlaybackState.Playing, restored.State);
        Assert.Equal(samples.Count, restored.Samples.Count);
        TestAssert.Close(elapsed, restored.ElapsedSeconds, 1e-3);

        restored.Advance(TimeSpan.FromSeconds(1));

        Assert.True(restored.Samples.Count > samples.Count);
        TestAssert.Close(elapsed + 2, restored.ElapsedSeconds, 1e-3);
        Assert.Equal(
            restored.Samples.Select(sample => sample.ElapsedSeconds).Distinct().Count(),
            restored.Samples.Count);
    }

    [Fact]
    public void RestoreKeepsPausedState()
    {
        var route = CreateRoute();
        var snapshot = new SimulationResumeSnapshot(
            RouteJsonCodec.Encode(route),
            1,
            0,
            1,
            1,
            WasPlaying: false,
            new[]
            {
                new PlaybackSample(new Coordinates(0.0, 0.0), 0, 0, false),
                new PlaybackSample(new Coordinates(0.0005, 0.0), 1, 10, false),
            },
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var restored = PlaybackController.Restore(route, snapshot);

        Assert.Equal(PlaybackState.Paused, restored.State);
        Assert.False(restored.BuildResult().Completed);
    }
    private static void AssertSamplesEqual(
        IReadOnlyList<PlaybackSample> expected,
        IReadOnlyList<PlaybackSample> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            TestAssert.Close(expected[index].ElapsedSeconds, actual[index].ElapsedSeconds, 1e-6);
            TestAssert.Close(expected[index].Coordinates.Latitude, actual[index].Coordinates.Latitude, 1e-9);
            TestAssert.Close(expected[index].Coordinates.Longitude, actual[index].Coordinates.Longitude, 1e-9);
            TestAssert.Close(expected[index].SpeedMetersPerSecond, actual[index].SpeedMetersPerSecond, 1e-4);
        }
    }

    private static SimulationRoute CreateRoute(
        bool loop = false,
        IReadOnlyList<RouteOutage>? outages = null) =>
        new(
            name: "播放測試",
            waypoints: new List<RouteWaypoint>
            {
                new("起點", new Coordinates(0.0, 0.0)),
                new("終點", new Coordinates(0.001, 0.0)),
            },
            speedKph: 36.0,
            loop: loop,
            outages: outages);
}
