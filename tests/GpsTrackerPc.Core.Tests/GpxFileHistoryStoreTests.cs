using GpsTrackerPc.Core.Gpx;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class GpxFileHistoryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _file;
    private readonly GpxFileHistoryStore _store;
    private DateTimeOffset _now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    public GpxFileHistoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "gpx-history.json");
        _store = new GpxFileHistoryStore(_file, () => _now);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 測試暫存資料夾清不掉不影響結果。
        }
    }

    [Fact]
    public void RememberMovesSamePathToFrontAndKeepsDisplayName()
    {
        var first = Path.Combine(_root, "day1.gpx");
        var second = Path.Combine(_root, "day2.gpx");

        _store.Remember(first);
        _now = _now.AddMinutes(1);
        _store.Remember(second);

        Assert.Equal(second, _store.Load()[0].FilePath);

        _now = _now.AddMinutes(1);
        _store.Remember(first);

        var loaded = _store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(first, loaded[0].FilePath);
        Assert.Equal("day1.gpx（" + Path.GetFileName(_root) + "）", loaded[0].DisplayName);
    }

    [Fact]
    public void RememberNormalizesPathAndIgnoresCase()
    {
        var path = Path.Combine(_root, "Track.gpx");

        _store.Remember(path);
        _now = _now.AddMinutes(1);
        _store.Remember(path.ToUpperInvariant());

        Assert.Single(_store.Load());
    }

    [Fact]
    public void LoadKeepsNewestEntriesAndLimitsCount()
    {
        for (var index = 0; index < GpxFileHistoryStore.MaxEntries + 5; index++)
        {
            _now = _now.AddMinutes(1);
            _store.Remember(Path.Combine(_root, $"track-{index:D2}.gpx"));
        }

        var loaded = _store.Load();
        Assert.Equal(GpxFileHistoryStore.MaxEntries, loaded.Count);
        Assert.Equal(
            Path.Combine(_root, $"track-{GpxFileHistoryStore.MaxEntries + 4:D2}.gpx"),
            loaded[0].FilePath);
    }

    [Fact]
    public void LoadReturnsEmptyForMissingOrBrokenFile()
    {
        Assert.Empty(_store.Load());

        File.WriteAllText(_file, "{ not json");
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void RemoveDeletesOnlyMatchingPath()
    {
        var first = Path.Combine(_root, "a.gpx");
        var second = Path.Combine(_root, "b.gpx");
        _store.Remember(first);
        _store.Remember(second);

        Assert.True(_store.Remove(first));
        Assert.False(_store.Remove(first));

        var loaded = _store.Load();
        Assert.Single(loaded);
        Assert.Equal(second, loaded[0].FilePath);
    }
}
