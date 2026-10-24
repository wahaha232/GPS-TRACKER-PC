using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class SimulationResumeStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public SimulationResumeStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "restart-session.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SavesAndLoadsSnapshot()
    {
        var store = new SimulationResumeStore(_path);
        var snapshot = new SimulationResumeSnapshot(
            RouteJsonCodec.Encode(CreateRoute()),
            12.5,
            345.6,
            2,
            4,
            true,
            new[]
            {
                new PlaybackSample(new Coordinates(25.0, 121.0), 0, 10, false),
                new PlaybackSample(new Coordinates(25.001, 121.0), 1, 10, false),
            },
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(12.5, loaded!.ElapsedSeconds);
        Assert.Equal(345.6, loaded.DistanceMeters);
        Assert.Equal(2, loaded.Laps);
        Assert.Equal(4, loaded.SpeedMultiplier);
        Assert.True(loaded.WasPlaying);
        Assert.Equal(2, loaded.Samples.Count);

        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void BrokenFileReturnsNull()
    {
        File.WriteAllText(_path, "{ not json");
        Assert.Null(new SimulationResumeStore(_path).Load());

        File.WriteAllText(_path, """{"routeJson":"","samples":[]}""");
        Assert.Null(new SimulationResumeStore(_path).Load());
    }

    private static SimulationRoute CreateRoute() =>
        new(
            name: "續傳測試",
            waypoints: new List<RouteWaypoint>
            {
                new("起點", new Coordinates(25.0, 121.0)),
                new("終點", new Coordinates(25.001, 121.0)),
            },
            speedKph: 18.0,
            loop: false);
}

public class PendingRecordStoreTests : IDisposable
{
    private readonly string _root;
    private readonly PendingRecordStore _store;

    public PendingRecordStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new PendingRecordStore(Path.Combine(_root, "pending"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SaveLoadAndRemove()
    {
        var build = CreateBuild("11111111-1111-1111-1111-111111111111");

        var path = _store.Save(build, 1000);
        Assert.True(File.Exists(path));

        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(build.Entry!.TrackId, loaded[0].Entry.TrackId);
        Assert.Equal(2, loaded[0].Track.Points.Count);
        Assert.Equal(1000, loaded[0].CreatedAtMs);

        Assert.True(_store.Remove(build.Entry.TrackId));
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void InvalidOrCorruptFilesAreIgnored()
    {
        Directory.CreateDirectory(_store.DirectoryPath);
        File.WriteAllText(Path.Combine(_store.DirectoryPath, "broken.json"), "{ not json");

        Assert.Empty(_store.LoadAll());
        Assert.Throws<ArgumentException>(() => _store.Save(CreateBuild("../escape")));
    }

    private static GpxBuildResult CreateBuild(string trackId)
    {
        var track = new GpxTrack
        {
            TrackId = trackId,
            StartTimeMs = 1_700_000_000_000,
            EndTimeMs = 1_700_000_010_000,
            DurationMs = 10_000,
            DistanceMeters = 120,
            GpsPointCount = 2,
            Points = new List<double[]>
            {
                new[] { 121.0, 25.0, 0.0, 10.0, 0.0 },
                new[] { 121.001, 25.0, 0.0, 10.0, 10.0 },
            },
            Segments = new List<object[]> { new object[] { "WALK", 0L, 10L } },
            Source = "simulated",
            DeviceName = "PC",
        };
        var entry = new GpxTrackEntry
        {
            TrackId = trackId,
            StartTimeMs = track.StartTimeMs,
            DistanceMeters = track.DistanceMeters,
            DurationMs = track.DurationMs,
            File = trackId + ".json",
            DeviceName = "PC",
            Source = "simulated",
        };
        return new GpxBuildResult(track, entry, null, null);
    }
}
