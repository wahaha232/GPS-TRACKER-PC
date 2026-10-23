using GpsTrackerPc.Core.Settings;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class TraySettingsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public TraySettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "tray.json");
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
    public void DefaultsToDisabled()
    {
        var store = new TraySettingsStore(_path);

        Assert.False(store.Load().MinimizeToTray);
    }

    [Fact]
    public void SavesAndLoadsMinimizeToTray()
    {
        var store = new TraySettingsStore(_path);

        store.SetMinimizeToTray(true);

        Assert.True(new TraySettingsStore(_path).Load().MinimizeToTray);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void CorruptFileUsesDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new TraySettingsStore(_path);

        Assert.False(store.Load().MinimizeToTray);
    }
}
