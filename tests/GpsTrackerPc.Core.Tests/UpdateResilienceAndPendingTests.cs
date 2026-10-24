using System.Net;
using System.Security.Cryptography;
using System.Text;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class UpdateResilienceTests : IDisposable
{
    private readonly string _root;

    public UpdateResilienceTests()
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
        }
    }

    [Fact]
    public void UpdateSettingsRoundTripChannelAndLimits()
    {
        var path = Path.Combine(_root, "update.json");
        var store = new UpdateCheckStore(path);

        store.SetChannel(UpdateChannel.Beta);
        store.SetDownloadOnlyOnUnmetered(false);
        store.SetAutoUploadDailyLimit(7);

        var state = store.Load();
        Assert.Equal(UpdateChannel.Beta, state.Channel);
        Assert.False(state.DownloadOnlyOnUnmetered);
        Assert.Equal(7, state.AutoUploadDailyLimit);

        store.SetAutoUploadDailyLimit(9999);
        Assert.Equal(200, store.Load().AutoUploadDailyLimit);
    }

    [Fact]
    public async Task BetaChannelUsesBetaReleaseTag()
    {
        var handler = new RoutingHandler(request =>
        {
            Assert.Equal(UpdateService.BetaApiUrl, request.RequestUri!.AbsoluteUri);
            return Json(ReleaseJson("0.9.0", "gpstracker-desktop-beta"));
        });
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.8.0", UpdateChannel.Beta);

        Assert.NotNull(update);
        Assert.Equal("0.9.0", update!.Version);
        Assert.Equal(UpdateService.BetaTag, UpdateService.TagFor(UpdateChannel.Beta));
    }

    [Fact]
    public async Task DownloadResumesFromPartFile()
    {
        const string url =
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-beta/GpsTrackerPc.exe";
        const string checksumUrl = url + ".sha256";
        var content = Encoding.UTF8.GetBytes("abcdef");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var sawRange = false;
        var handler = new RoutingHandler(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == checksumUrl)
            {
                return Text($"{hash}  GpsTrackerPc.exe");
            }

            Assert.Equal(url, uri);
            var range = request.Headers.Range;
            if (range?.Ranges.FirstOrDefault() is { From: 3 } && range.Ranges.First().To == null)
            {
                sawRange = true;
                return Binary(content.Skip(3).ToArray(), HttpStatusCode.PartialContent);
            }

            return Binary(content);
        });

        var destination = Path.Combine(_root, "GpsTrackerPc.exe");
        await File.WriteAllBytesAsync(destination + ".part", content.Take(3).ToArray());
        var service = new UpdateService(new HttpClient(handler));
        var update = new UpdateInfo("0.9.0", "GPS Tracker PC v0.9.0", url, checksumUrl, "GpsTrackerPc.exe", content.Length, null);

        await service.DownloadWithResumeAsync(update, destination);

        Assert.True(sawRange);
        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public void RollbackRestoresBackupAndKeepsItUntilExpiry()
    {
        var exe = Path.Combine(_root, "GpsTrackerPc.exe");
        var old = exe + ".old";
        File.WriteAllText(exe, "new");
        File.WriteAllText(old, "old");

        var result = UpdateInstaller.Rollback(exe, () => { });

        Assert.True(result.Success);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.True(File.Exists(old));

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
        UpdateInstaller.CleanupOldVersion(exe);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void CleanupDownloadsKeepsNewerDownloadFolders()
    {
        var directory = Path.Combine(_root, "updates");
        foreach (var version in new[] { "0.7.0", "0.9.0", "0.10.0" })
        {
            var folder = Path.Combine(directory, version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "GpsTrackerPc.exe"), version);
        }

        var oldPart = Path.Combine(directory, "0.7.0", "x.part");
        File.WriteAllText(oldPart, "partial");
        File.SetLastWriteTimeUtc(oldPart, DateTime.UtcNow.AddDays(-8));

        UpdateInstaller.CleanupDownloads(directory, "0.9.0");

        Assert.False(Directory.Exists(Path.Combine(directory, "0.7.0")));
        Assert.False(Directory.Exists(Path.Combine(directory, "0.9.0")));
        Assert.True(Directory.Exists(Path.Combine(directory, "0.10.0")));
    }

    [Fact]
    public void DiskSpaceAndSignatureChecksWork()
    {
        Assert.True(UpdateInstallChecks.HasFreeSpace(_root, 1024, out var available));
        Assert.True(available > 0);
        Assert.False(UpdateInstallChecks.HasFreeSpace(_root, long.MaxValue, out _));

        var unsigned = Path.Combine(_root, "plain.exe");
        File.WriteAllText(unsigned, "not a signed PE");
        Assert.Equal(UpdateSignatureStatus.Unsigned, UpdateSignature.Inspect(unsigned).Status);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Binary(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new ByteArrayContent(bytes) };

    private static string ReleaseJson(string version, string tag)
    {
        var prefix = $"https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/{tag}/";
        return $$"""
        {
          "name": "GPS Tracker PC v{{version}}",
          "body": "notes",
          "assets": [
            { "name": "GpsTrackerPc.exe", "browser_download_url": "{{prefix}}GpsTrackerPc.exe", "size": 6 },
            { "name": "GpsTrackerPc.exe.sha256", "browser_download_url": "{{prefix}}GpsTrackerPc.exe.sha256", "size": 100 }
          ]
        }
        """;
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        {
            _handle = handle;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_handle(request));
    }
}

public class PendingQueueTests : IDisposable
{
    private readonly string _root;

    public PendingQueueTests()
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
        }
    }

    [Fact]
    public void PendingStoreRecordsFailuresAndLimitsCount()
    {
        var store = new PendingRecordStore(Path.Combine(_root, "pending"), maxRecords: 2);
        store.Save(Build("11111111-1111-1111-1111-111111111111"), 100);
        store.Save(Build("22222222-2222-2222-2222-222222222222"), 200);
        store.Save(Build("33333333-3333-3333-3333-333333333333"), 300);

        var records = store.LoadAll();
        Assert.Equal(2, records.Count);
        Assert.DoesNotContain(records, record => record.Entry.TrackId.StartsWith("1111"));

        store.RecordFailure(records[0].Entry.TrackId, "network down");
        var failed = store.LoadAll().Single(record => record.Entry.TrackId == records[0].Entry.TrackId);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("network down", failed.LastError);
    }

    [Fact]
    public async Task PendingUploaderRemovesUploadedAndRecordsFailure()
    {
        var store = new PendingRecordStore(Path.Combine(_root, "pending"));
        store.Save(Build("11111111-1111-1111-1111-111111111111"), 100);
        store.Save(Build("22222222-2222-2222-2222-222222222222"), 200);
        var uploader = new PendingRecordUploader(store, new FakeUploader());

        var report = await uploader.UploadAllAsync(10);

        Assert.Equal(1, report.Uploaded);
        Assert.Equal(1, report.Failed);
        Assert.Equal("offline", report.LastError);
        var left = store.LoadAll();
        Assert.Single(left);
        Assert.Equal(1, left[0].Attempts);
    }

    [Fact]
    public void UploadQuotaResetsOnNewDay()
    {
        var store = new UploadQuotaStore(Path.Combine(_root, "quota.json"));
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(8));

        Assert.Equal(20, store.Remaining(20, now));
        store.Consume(5, now);
        Assert.Equal(15, store.Remaining(20, now));
        Assert.Equal(20, store.Remaining(20, now.AddDays(1)));
    }

    [Fact]
    public void ResumePreferenceRoundTripAndReset()
    {
        var store = new ResumePreferenceStore(Path.Combine(_root, "pref.json"));
        Assert.Equal(RestartPromptChoice.AlwaysAsk, store.Load());

        store.Save(RestartPromptChoice.SaveAndEnd);
        Assert.Equal(RestartPromptChoice.SaveAndEnd, store.Load());

        store.Reset();
        Assert.Equal(RestartPromptChoice.AlwaysAsk, store.Load());
    }

    [Fact]
    public void ResumeStoreAllowsRouteDraftWithoutSamples()
    {
        var path = Path.Combine(_root, "resume.json");
        var store = new SimulationResumeStore(path);
        var snapshot = new SimulationResumeSnapshot(
            RouteJsonCodec.Encode(CreateRoute()),
            0,
            0,
            1,
            1,
            false,
            Array.Empty<PlaybackSample>(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "草稿",
            "wp-1",
            true);

        store.Save(snapshot);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Samples);
        Assert.Equal("草稿", loaded.RouteName);
        Assert.Equal("wp-1", loaded.SelectedWaypointId);
        Assert.True(loaded.HasUnsavedRouteChanges);
    }

    private static SimulationRoute CreateRoute() =>
        new(
            name: "草稿",
            waypoints: new List<RouteWaypoint>
            {
                new("起點", new Coordinates(25.0, 121.0)),
                new("終點", new Coordinates(25.001, 121.0)),
            },
            speedKph: 18.0,
            loop: false);

    private static GpxBuildResult Build(string trackId)
    {
        var track = new GpxTrack
        {
            TrackId = trackId,
            StartTimeMs = 1_700_000_000_000,
            EndTimeMs = 1_700_000_010_000,
            DurationMs = 10_000,
            DistanceMeters = 120,
            GpsPointCount = 2,
            Points = new List<double[]>
            {
                new[] { 121.0, 25.0, 0.0, 10.0, 0.0 },
                new[] { 121.001, 25.0, 0.0, 10.0, 10.0 },
            },
            Segments = new List<object[]> { new object[] { "WALK", 0L, 10L } },
            Source = "simulated",
            DeviceName = "PC",
        };
        var entry = new GpxTrackEntry
        {
            TrackId = trackId,
            StartTimeMs = track.StartTimeMs,
            DistanceMeters = track.DistanceMeters,
            DurationMs = track.DurationMs,
            File = trackId + ".json",
            DeviceName = "PC",
            Source = "simulated",
        };
        return new GpxBuildResult(track, entry, null, null);
    }

    private sealed class FakeUploader : IRecordUploader
    {
        private int _calls;

        public Task<UploadResult> UploadAsync(GpxBuildResult build, CancellationToken cancellationToken = default)
        {
            _calls++;
            return Task.FromResult(_calls == 1
                ? UploadResult.Uploaded(build.Entry!.TrackId)
                : UploadResult.Failed("offline"));
        }
    }
}
