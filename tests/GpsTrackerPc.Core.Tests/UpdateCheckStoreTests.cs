using GpsTrackerPc.Core.Updates;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class UpdateCheckStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public UpdateCheckStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "update-check.json");
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
    public void DefaultsToAutoCheckOncePerDay()
    {
        var store = new UpdateCheckStore(_path);
        var now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

        Assert.True(store.ShouldAutoCheck(now));

        store.MarkChecked(now);

        Assert.False(store.ShouldAutoCheck(now.AddHours(23)));
        Assert.True(store.ShouldAutoCheck(now.AddHours(24)));
        Assert.False(store.ShouldAutoCheck(now.AddHours(-1)));
    }

    [Fact]
    public void DisabledAutoCheckNeverChecks()
    {
        var store = new UpdateCheckStore(_path);
        store.SetAutoCheck(false);

        Assert.False(store.ShouldAutoCheck(DateTimeOffset.UtcNow));
        Assert.False(store.Load().AutoCheckEnabled);
    }

    [Fact]
    public void SavesAndLoadsLastCheckTime()
    {
        var store = new UpdateCheckStore(_path);
        var now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

        store.MarkChecked(now);

        var state = store.Load();
        Assert.Equal(now.ToUnixTimeMilliseconds(), state.LastCheckMs);
        Assert.True(state.AutoCheckEnabled);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void CorruptFileUsesDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new UpdateCheckStore(_path);

        Assert.True(store.Load().AutoCheckEnabled);
        Assert.Null(store.Load().LastCheckMs);
        Assert.True(store.ShouldAutoCheck(DateTimeOffset.UtcNow));
    }
}
