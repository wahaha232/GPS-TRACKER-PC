using System.Text;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class RouteLibraryStoreTests : IDisposable
{
    private readonly string _root;
    private readonly RouteLibraryStore _store;

    public RouteLibraryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        _store = new RouteLibraryStore(Path.Combine(_root, "routes"));
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
    public void SaveLoadAllAndDeleteMultipleRoutes()
    {
        var older = CreateRoute("first", "第一條", 1000);
        var newer = CreateRoute("second", "第二條", 2000, loop: true);

        _store.Save(older);
        _store.Save(newer);

        var all = _store.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("second", all[0].Id);
        Assert.True(all[0].Loop);
        Assert.Equal("第一條", all[1].Name);

        var loaded = _store.Load("first");
        Assert.NotNull(loaded);
        Assert.Equal("第一條", loaded!.Name);
        Assert.Equal(2, loaded.Waypoints.Count);

        Assert.True(_store.Delete("first"));
        Assert.Single(_store.LoadAll());
        Assert.False(_store.Delete("first"));
        Assert.Empty(Directory.GetFiles(_store.DirectoryPath, "*.tmp-*"));
    }

    [Fact]
    public void SavingTheSameIdOverwritesTheRoute()
    {
        _store.Save(CreateRoute("same", "舊名稱", 1000));
        _store.Save(CreateRoute("same", "新名稱", 2000));

        var route = _store.Load("same");
        Assert.NotNull(route);
        Assert.Equal("新名稱", route!.Name);
        Assert.Single(_store.LoadAll());
    }

    [Fact]
    public void CorruptRouteFileIsSkipped()
    {
        _store.Save(CreateRoute("good", "好的", 1000));
        File.WriteAllText(Path.Combine(_store.DirectoryPath, "broken.json"), "{ not json");

        var all = _store.LoadAll();
        Assert.Single(all);
        Assert.Equal("good", all[0].Id);
    }

    [Fact]
    public void UnsafeRouteIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => _store.Load("../escape"));
        Assert.Throws<ArgumentException>(() => _store.Delete("C:\\escape"));
    }

    [Fact]
    public void StoreFileUsesRouteJsonCodec()
    {
        _store.Save(CreateRoute("json", "JSON", 1000));
        var path = Path.Combine(_store.DirectoryPath, "json.json");
        var text = File.ReadAllText(path);
        var decoded = RouteJsonCodec.Decode(text);

        Assert.NotNull(decoded);
        Assert.Equal("JSON", decoded!.Name);
        Assert.Equal("json", decoded.Id);
    }

    private static SimulationRoute CreateRoute(string id, string name, long createdAt, bool loop = false) =>
        new(
            name: name,
            waypoints: new List<RouteWaypoint>
            {
                new("A", new Coordinates(25.0, 121.5)),
                new("B", new Coordinates(25.001, 121.501)),
            },
            speedKph: 5.0,
            loop: loop,
            createdAt: createdAt,
            id: id);
}
