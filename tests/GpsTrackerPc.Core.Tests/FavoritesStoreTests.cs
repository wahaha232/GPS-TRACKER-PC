using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class FavoritesStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _file;
    private readonly FavoritesStore _store;
    private DateTimeOffset _now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    public FavoritesStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "favorites.json");
        _store = new FavoritesStore(_file, () => _now);
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
    public void AddLoadAndRemoveFavorites()
    {
        var taipei = new Coordinates(25.033964, 121.564472);
        var kaohsiung = new Coordinates(22.6273, 120.3014);

        var first = _store.Add("台北 101", taipei);
        _now = _now.AddSeconds(1);
        var second = _store.Add("高雄", kaohsiung);

        var loaded = _store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(second.Id, loaded[0].Id);
        Assert.Equal("台北 101", loaded[1].Name);

        Assert.True(_store.Remove(first.Id));
        Assert.Single(_store.Load());
        Assert.False(_store.Remove("missing"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void AddingTheSameCoordinatesUpdatesTheName()
    {
        var coordinates = new Coordinates(25.0, 121.5);
        var first = _store.Add("舊名稱", coordinates);
        var second = _store.Add("新名稱", coordinates);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_store.Load());
        Assert.Equal("新名稱", _store.Load()[0].Name);
    }

    [Fact]
    public void BlankNameIsRejected()
    {
        Assert.Throws<ArgumentException>(() => _store.Add("   ", new Coordinates(25, 121)));
    }

    [Fact]
    public void CorruptFileIsTreatedAsEmpty()
    {
        File.WriteAllText(_file, "{ this is not json");
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void MissingFileIsEmpty()
    {
        Assert.Empty(_store.Load());
    }

    [Fact]
    public void NameIsTrimmedAndLimitedTo80Characters()
    {
        var favorite = _store.Add("  " + new string('a', 120) + "  ", new Coordinates(25, 121));

        Assert.Equal(80, favorite.Name.Length);
        Assert.Equal(new string('a', 80), favorite.Name);
    }
}
