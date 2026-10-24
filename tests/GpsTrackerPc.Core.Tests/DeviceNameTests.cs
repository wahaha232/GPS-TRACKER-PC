using System.Text.Json;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class DeviceNameTests : IDisposable
{
    private readonly string _root;

    public DeviceNameTests()
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
    public void StoreDefaultsToPcAndRoundTripsConfiguredName()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new DeviceNameStore(path);

        Assert.Equal("PC", store.Load());

        store.Save("  NB  ");

        Assert.Equal("NB", store.Load());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void StoreRejectsBlankTooLongAndControlCharacters()
    {
        var store = new DeviceNameStore(Path.Combine(_root, "settings.json"));

        Assert.Throws<ArgumentException>(() => store.Save("   "));
        Assert.Throws<ArgumentException>(() => store.Save(new string('a', 81)));
        Assert.Throws<ArgumentException>(() => store.Save("NB\u0001"));
    }

    [Fact]
    public void CorruptSettingsFallBackToDefault()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{ not json");

        Assert.Equal("PC", new DeviceNameStore(path).Load());
    }

    [Fact]
    public void PolicyAllowsUniqueName()
    {
        var result = DeviceNamePolicy.Validate(
            "NB",
            new[]
            {
                new ManifestDevice("POCO X6 Pro 5G", null),
                new ManifestDevice("PC", "simulated"),
            });

        Assert.True(result.IsValid);
        Assert.False(result.HasSimulatedConflict);
    }

    [Fact]
    public void PolicyAllowsNameOfNonSimulatedDeviceWithWarning()
    {
        var result = DeviceNamePolicy.Validate(
            "POCO X6 Pro 5G",
            new[] { new ManifestDevice("POCO X6 Pro 5G", null) });

        Assert.True(result.IsValid);
        Assert.True(result.HasNonSimulatedConflict);
        Assert.False(result.HasSimulatedConflict);
        Assert.Contains("合併", result.WarningMessage);
    }

    [Fact]
    public void PolicyAllowsKeepingCurrentNameThatBelongsToNonSimulatedDevice()
    {
        var result = DeviceNamePolicy.Validate(
            "HOMEPC",
            new[] { new ManifestDevice("HOMEPC", null) },
            currentName: "HOMEPC");

        Assert.True(result.IsValid);
        Assert.Null(result.WarningMessage);
        Assert.False(result.HasNonSimulatedConflict);
    }

    [Fact]
    public void PolicyWarnsWhenAnotherSimulatedDeviceUsesTheName()
    {
        var result = DeviceNamePolicy.Validate(
            "NB",
            new[] { new ManifestDevice("NB", "simulated") });

        Assert.True(result.IsValid);
        Assert.True(result.HasSimulatedConflict);
        Assert.Contains("合併", result.WarningMessage);
    }

    [Fact]
    public void PolicyDoesNotWarnWhenSavingTheCurrentName()
    {
        var result = DeviceNamePolicy.Validate(
            "PC",
            new[] { new ManifestDevice("PC", "simulated") },
            currentName: "PC");

        Assert.True(result.IsValid);
        Assert.False(result.HasSimulatedConflict);
    }

    [Fact]
    public void PolicyRejectsBlankName()
    {
        var result = DeviceNamePolicy.Validate("   ", Array.Empty<ManifestDevice>());

        Assert.False(result.IsValid);
        Assert.Contains("空白", result.ErrorMessage);
    }

    [Fact]
    public void ManifestReaderReturnsDeviceNameAndSource()
    {
        var path = Path.Combine(_root, "manifest.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                tracks = new object[]
                {
                    new { trackId = "a", deviceName = "POCO X6 Pro 5G" },
                    new { trackId = "b", deviceName = "PC", source = "simulated" },
                    new { trackId = "c", deviceName = "NB", source = "simulated" },
                },
            }));

        var devices = ManifestDeviceReader.Read(path);

        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, device => device.DeviceName == "PC" && device.Source == "simulated");
        Assert.Contains(devices, device => device.DeviceName == "POCO X6 Pro 5G" && device.Source == null);
    }

    [Fact]
    public void RecordDeviceNamingAppliesConfiguredNameToGpxImport()
    {
        const string gpx = """
            <gpx><trk><trkseg>
            <trkpt lat="25.0" lon="121.5"><time>2026-10-22T08:00:00Z</time></trkpt>
            <trkpt lat="25.0001" lon="121.5"><time>2026-10-22T08:00:10Z</time></trkpt>
            </trkseg></trk></gpx>
            """;
        var build = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(gpx),
            new GpxBuildOptions { Id = "gpx-device" });

        RecordDeviceNaming.ApplyDeviceName(build, "NB");

        Assert.Equal("NB", build.Track!.DeviceName);
        Assert.Equal("NB", build.Entry!.DeviceName);
        Assert.Contains("\"deviceName\":\"NB\"", GpxJson.SerializeTrack(build.Track));
    }

    [Fact]
    public void SimulatedRecordBuilderUsesConfiguredNameInsteadOfHardcodedPc()
    {
        var route = new SimulationRoute(
            "測試",
            new List<RouteWaypoint>
            {
                new("A", new Coordinates(25.0, 121.5)),
                new("B", new Coordinates(25.001, 121.5)),
            },
            speedKph: 36.0,
            loop: false);
        var playback = PlaybackController.GenerateDirect(route);

        var build = SimulatedRecordBuilder.Build(
            playback,
            new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero),
            "NB",
            id: "device-sim");

        Assert.True(build.IsSuccess);
        Assert.Equal("NB", build.Track!.DeviceName);
        Assert.Equal("NB", build.Entry!.DeviceName);
        Assert.Equal("simulated", build.Track.Source);
    }
}
