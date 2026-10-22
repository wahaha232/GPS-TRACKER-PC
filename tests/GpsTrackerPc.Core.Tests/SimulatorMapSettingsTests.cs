using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class SimulatorMapSettingsTests : IDisposable
{
    private readonly string _root;

    public SimulatorMapSettingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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
    public void DefaultsToSimpleBasemapAndFollowEnabled()
    {
        var store = new MapSettingsStore(Path.Combine(_root, "map-settings.json"));

        var settings = store.Load();

        Assert.Equal(BasemapKind.Simple, settings.Basemap);
        Assert.True(settings.Follow);
    }

    [Fact]
    public void SavesAndLoadsBasemapAndFollowChoice()
    {
        var path = Path.Combine(_root, "map-settings.json");
        var store = new MapSettingsStore(path);

        store.Save(new SimulatorMapSettings(BasemapKind.Nlsc, Follow: false));

        var settings = store.Load();
        Assert.Equal(BasemapKind.Nlsc, settings.Basemap);
        Assert.False(settings.Follow);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void UnknownBasemapFallsBackToSimpleButKeepsFollow()
    {
        var path = Path.Combine(_root, "map-settings.json");
        File.WriteAllText(path, """{"basemap":"unknown","follow":false}""");

        var settings = new MapSettingsStore(path).Load();

        Assert.Equal(BasemapKind.Simple, settings.Basemap);
        Assert.False(settings.Follow);
    }

    [Fact]
    public void CorruptFileUsesDefaults()
    {
        var path = Path.Combine(_root, "map-settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new MapSettingsStore(path).Load();

        Assert.Equal(BasemapKind.Simple, settings.Basemap);
        Assert.True(settings.Follow);
    }

    [Fact]
    public void CatalogHasAllFourBasemapsAndAttribution()
    {
        Assert.Equal(4, BasemapCatalog.Options.Count);
        Assert.Contains(BasemapCatalog.Options, option => option.Kind == BasemapKind.Simple);
        Assert.Contains(BasemapCatalog.Options, option => option.Kind == BasemapKind.Satellite);
        Assert.Contains(BasemapCatalog.Options, option => option.Kind == BasemapKind.Nlsc);
        Assert.Contains(BasemapCatalog.Options, option => option.Kind == BasemapKind.None);
        Assert.Contains("OpenFreeMap", BasemapCatalog.Simple.Attribution);
        Assert.Contains("OpenMapTiles", BasemapCatalog.Simple.Attribution);
        Assert.Contains("OpenStreetMap", BasemapCatalog.Simple.Attribution);
        Assert.Contains("NLSC", BasemapCatalog.Nlsc.Attribution);
    }

    [Fact]
    public void FollowOnlyPansWhenPositionEntersOuterFifteenPercent()
    {
        var viewport = new ViewportBounds(
            West: 121.495,
            South: 24.995,
            East: 121.505,
            North: 25.005);

        Assert.False(ViewportFollowPolicy.ShouldPan(
            viewport,
            new Coordinates(25.000, 121.500),
            followEnabled: true));

        Assert.False(ViewportFollowPolicy.ShouldPan(
            viewport,
            new Coordinates(25.000, 121.502),
            followEnabled: true));

        Assert.True(ViewportFollowPolicy.ShouldPan(
            viewport,
            new Coordinates(25.000, 121.5045),
            followEnabled: true));

        Assert.True(ViewportFollowPolicy.ShouldPan(
            viewport,
            new Coordinates(25.0045, 121.500),
            followEnabled: true));

        Assert.False(ViewportFollowPolicy.ShouldPan(
            viewport,
            new Coordinates(25.000, 121.5045),
            followEnabled: false));
    }

    [Fact]
    public void FollowIgnoresInvalidViewport()
    {
        var invalid = new ViewportBounds(0, 0, 0, 0);

        Assert.False(ViewportFollowPolicy.ShouldPan(
            invalid,
            new Coordinates(25.0, 121.5),
            followEnabled: true));
    }
}
