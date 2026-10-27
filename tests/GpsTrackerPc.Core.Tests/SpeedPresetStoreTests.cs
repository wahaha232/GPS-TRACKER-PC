using GpsTrackerPc.Core.Simulator;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class SpeedPresetStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _file;

    public SpeedPresetStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "speed-presets.json");
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
    public void DefaultsMatchOriginalButtons()
    {
        var presets = new SpeedPresetStore(_file).Load();

        Assert.Equal(new[] { 5.0, 10.0, 18.0, 40.0 }, presets.Select(preset => preset.SpeedKph));
        Assert.Equal("走路 5", presets[0].ButtonText);
    }

    [Fact]
    public void SavedSpeedsAreLoadedBack()
    {
        var store = new SpeedPresetStore(_file);
        var edited = store.Load()
            .Select(preset => preset.Key == "walk" ? preset with { SpeedKph = 4.5 } : preset)
            .ToList();

        store.Save(edited);
        var loaded = new SpeedPresetStore(_file).Load();

        Assert.Equal(4.5, loaded[0].SpeedKph);
        Assert.Equal("走路 4.5", loaded[0].ButtonText);
        Assert.Equal(10.0, loaded[1].SpeedKph);
    }

    [Fact]
    public void OutOfRangeSpeedIsRejectedWithoutWritingFile()
    {
        var store = new SpeedPresetStore(_file);
        var invalid = store.Load()
            .Select(preset => preset.Key == "car" ? preset with { SpeedKph = 0.1 } : preset)
            .ToList();

        Assert.Throws<ArgumentException>(() => store.Save(invalid));
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void CorruptOrInvalidValuesFallBackPerItem()
    {
        File.WriteAllText(_file, "{\"walk\": 6, \"run\": 9999}");
        var loaded = new SpeedPresetStore(_file).Load();
        Assert.Equal(6.0, loaded[0].SpeedKph);
        Assert.Equal(10.0, loaded[1].SpeedKph);

        File.WriteAllText(_file, "not json");
        Assert.Equal(5.0, new SpeedPresetStore(_file).Load()[0].SpeedKph);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var store = new SpeedPresetStore(_file);
        store.Save(store.Load().Select(preset => preset with { SpeedKph = 7 }).ToList());

        store.Reset();

        Assert.Equal(5.0, store.Load()[0].SpeedKph);
    }
}
