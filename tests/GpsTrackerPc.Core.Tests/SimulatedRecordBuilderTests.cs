using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class SimulatedRecordBuilderTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OutageSamplesAreExcludedAndPcMetadataIsAdded()
    {
        var playback = CreatePlayback();
        var result = SimulatedRecordBuilder.Build(playback, StartTime, "PC", id: "sim-track");

        Assert.True(result.IsSuccess);
        var track = result.Track!;
        var entry = result.Entry!;
        Assert.Equal("PC", track.DeviceName);
        Assert.Equal("simulated", track.Source);
        Assert.Equal("PC", entry.DeviceName);
        Assert.Equal("simulated", entry.Source);
        Assert.Equal("sim-track.json", entry.File);
        Assert.Equal(StartTime.ToUnixTimeMilliseconds(), track.StartTimeMs);
        Assert.Equal(StartTime.ToUnixTimeMilliseconds(), entry.StartTimeMs);
        Assert.Equal(4, track.Points.Count);
        Assert.DoesNotContain(track.Points, point => Math.Abs(point[4] - 2.0) < 1e-9);
        Assert.Equal(4, track.GpsPointCount);
    }

    [Fact]
    public void ProvidedModeIsWrittenToEntryAndTrack()
    {
        var result = SimulatedRecordBuilder.Build(
            CreatePlayback(),
            StartTime,
            "PC",
            id: "sim-track",
            mode: "BICYCLE");

        Assert.True(result.IsSuccess);
        Assert.Equal("BICYCLE", result.Entry!.DominantMode);
        Assert.Equal("BICYCLE", result.Track!.DominantMode);
    }

    [Fact]
    public void StatisticsFollowGpxTrackBuilderRules()
    {
        var playback = CreatePlayback();
        var result = SimulatedRecordBuilder.Build(playback, StartTime, "PC", id: "sim-track");

        var filtered = playback.Samples
            .Where(sample => !sample.IsInOutage)
            .ToList();
        var document = new GpxDocument(
            playback.Route.Name,
            filtered
                .Select(sample => new GpxPoint(
                    sample.Coordinates.Latitude,
                    sample.Coordinates.Longitude,
                    null,
                    StartTime.ToUnixTimeMilliseconds() + sample.ElapsedSeconds * 1000))
                .ToList());
        var expected = GpxTrackBuilder.Build(
            document,
            new GpxBuildOptions { Id = "sim-track", Mode = "UNKNOWN" });

        Assert.True(expected.IsSuccess);
        TestAssert.Close(expected.Track!.DistanceMeters, result.Track!.DistanceMeters, 1e-9);
        Assert.Equal(expected.Track.DurationMs, result.Track.DurationMs);
        TestAssert.Close(expected.Track.AvgSpeedMps, result.Track.AvgSpeedMps, 1e-9);
        TestAssert.Close(expected.Track.MaxSpeedMps, result.Track.MaxSpeedMps, 1e-9);
        TestAssert.Close(expected.Track.TotalAscentMeters, result.Track.TotalAscentMeters, 1e-9);
        Assert.Equal(expected.Track.GpsPointCount, result.Track.GpsPointCount);
        Assert.Equal(expected.Entry!.DistanceMeters, result.Entry!.DistanceMeters);
    }

    [Fact]
    public void OutageAtTheBeginningKeepsTheSelectedStartTime()
    {
        var route = CreateRoute();
        var playback = new PlaybackResult(
            route,
            new List<PlaybackSample>
            {
                new(new Coordinates(25.0, 121.5), 0, 1, IsInOutage: true),
                new(new Coordinates(25.0, 121.5), 1, 1, IsInOutage: true),
                new(new Coordinates(25.0001, 121.5), 2, 1, IsInOutage: false),
                new(new Coordinates(25.0002, 121.5), 3, 1, IsInOutage: false),
                new(new Coordinates(25.0003, 121.5), 4, 1, IsInOutage: false),
            },
            ElapsedSeconds: 4,
            DistanceMeters: 33,
            Completed: true,
            LapsCompleted: 0);

        var result = SimulatedRecordBuilder.Build(playback, StartTime, "PC", id: "late-start");

        Assert.True(result.IsSuccess);
        Assert.Equal(StartTime.ToUnixTimeMilliseconds(), result.Track!.StartTimeMs);
        Assert.Equal(2.0, result.Track.Points[0][4]);
        Assert.Equal(StartTime.ToUnixTimeMilliseconds(), result.Entry!.StartTimeMs);
        Assert.Equal(StartTime.ToUnixTimeMilliseconds(), result.Summary!.StartTimeMs);
    }

    [Fact]
    public void JsonContainsDeviceAndSourceOnTrackAndManifestEntry()
    {
        var result = SimulatedRecordBuilder.Build(CreatePlayback(), StartTime, "PC", id: "sim-track");

        var trackJson = GpxJson.SerializeTrack(result.Track!);
        var entryJson = GpxJson.SerializeEntry(result.Entry!);

        Assert.Contains("\"deviceName\":\"PC\"", trackJson);
        Assert.Contains("\"source\":\"simulated\"", trackJson);
        Assert.Contains("\"deviceName\":\"PC\"", entryJson);
        Assert.Contains("\"source\":\"simulated\"", entryJson);
    }

    [Fact]
    public void FewerThanTwoSamplesCannotBecomeARecord()
    {
        var route = CreateRoute();
        var sampling = new PlaybackResult(
            route,
            new List<PlaybackSample>
            {
                new(new Coordinates(25.0, 121.5), 0, 1, IsInOutage: false),
            },
            0,
            0,
            true,
            0);

        var result = SimulatedRecordBuilder.Build(sampling, StartTime, "PC");

        Assert.Equal("too-few", result.Error);
    }

    private static PlaybackResult CreatePlayback()
    {
        var route = CreateRoute();
        var samples = new List<PlaybackSample>
        {
            new(new Coordinates(25.0000, 121.5000), 0, 1, IsInOutage: false),
            new(new Coordinates(25.0001, 121.5000), 1, 1, IsInOutage: false),
            new(new Coordinates(25.0002, 121.5000), 2, 1, IsInOutage: true),
            new(new Coordinates(25.0003, 121.5000), 3, 1, IsInOutage: false),
            new(new Coordinates(25.0004, 121.5000), 4, 1, IsInOutage: false),
        };
        return new PlaybackResult(
            route,
            samples,
            ElapsedSeconds: 4,
            DistanceMeters: 44,
            Completed: true,
            LapsCompleted: 0);
    }

    private static SimulationRoute CreateRoute() =>
        new(
            name: "模擬測試路線",
            waypoints: new List<RouteWaypoint>
            {
                new("A", new Coordinates(25.0, 121.5)),
                new("B", new Coordinates(25.001, 121.5)),
            },
            speedKph: 5.0,
            loop: false);
}
