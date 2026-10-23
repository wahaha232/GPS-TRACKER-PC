using GpsTrackerPc.Core.Settings;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class StorageSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public StorageSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "storage.json");
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
    public void DefaultsToNoOverride()
    {
        var store = new StorageSettingsStore(_path);

        Assert.Null(store.Load().DataDirectory);
    }

    [Fact]
    public void SavesAndLoadsDataDirectory()
    {
        var store = new StorageSettingsStore(_path);
        var target = Path.Combine(_root, "my-data");

        store.SetDataDirectory(target);

        Assert.Equal(Path.GetFullPath(target), new StorageSettingsStore(_path).Load().DataDirectory);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void BlankPathMeansDefault()
    {
        var store = new StorageSettingsStore(_path);

        store.SetDataDirectory("   ");

        Assert.Null(store.Load().DataDirectory);
    }

    [Fact]
    public void CorruptFileUsesDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new StorageSettingsStore(_path);

        Assert.Null(store.Load().DataDirectory);
    }
}
