using GpsTrackerPc.Core.Appearance;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class ThemeSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ThemeSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "theme.json");
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
    public void DefaultsToSystemTheme()
    {
        Assert.Equal(AppThemePreference.System, new ThemeSettingsStore(_path).Load());
    }

    [Fact]
    public void SavesAndLoadsLightAndDark()
    {
        var store = new ThemeSettingsStore(_path);

        store.Save(AppThemePreference.Light);
        Assert.Equal(AppThemePreference.Light, store.Load());

        store.Save(AppThemePreference.Dark);
        Assert.Equal(AppThemePreference.Dark, store.Load());
    }

    [Fact]
    public void UnknownOrCorruptValueFallsBackToSystem()
    {
        File.WriteAllText(_path, """{"theme":"purple"}""");
        Assert.Equal(AppThemePreference.System, new ThemeSettingsStore(_path).Load());

        File.WriteAllText(_path, "{ not json");
        Assert.Equal(AppThemePreference.System, new ThemeSettingsStore(_path).Load());
    }
}
