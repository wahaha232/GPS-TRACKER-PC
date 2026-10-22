using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpsTrackerPc.Core.Data;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

/// <summary>
/// 第 8 階段整合測試：串起路線→播放→紀錄→上傳、GPX、同步與更新。
/// 全部使用假 HttpMessageHandler，不會真的連 GitHub，也不會啟動任何視窗。
/// </summary>
public class EndToEndFlowTests : IDisposable
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public EndToEndFlowTests()
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
    public async Task SimulatedRouteBecomesPcRecordAndMergesManifest()
    {
        var route = CreateRoute(outages: new[] { new RouteOutage(3, 2) });
        var playback = PlaybackController.GenerateDirect(route);
        var build = SimulatedRecordBuilder.Build(playback, StartTime, deviceName: "NB", id: "e2e-track");
        Assert.True(build.IsSuccess, build.Error);

        var github = new FakeGitHubHandler();
        github.AddFile(
            "gpstracker-web/data/manifest.json",
            """{"tracks":[{"trackId":"existing","startTimeMs":1,"distanceMeters":1}]}""");
        var uploader = new GitHubRecordUploader(
            new HttpClient(github),
            CreateTokenStore(),
            retryDelay: TimeSpan.Zero);

        var upload = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Uploaded, upload.Status);
        var trackPath = "gpstracker-web/data/e2e-track.json";
        var track = JsonNode.Parse(github.Files[trackPath].Content)!.AsObject();
        Assert.Equal("NB", track["deviceName"]!.GetValue<string>());
        Assert.Equal("simulated", track["source"]!.GetValue<string>());
        var offsets = track["points"]!.AsArray()
            .Select(point => point![4]!.GetValue<double>())
            .ToList();
        Assert.Equal(build.Track!.Points.Count, offsets.Count);
        Assert.DoesNotContain(offsets, offset => Math.Abs(offset - 3.0) < 0.1);
        Assert.DoesNotContain(offsets, offset => Math.Abs(offset - 4.0) < 0.1);

        var manifest = JsonNode.Parse(github.Files["gpstracker-web/data/manifest.json"].Content)!.AsObject();
        var tracks = manifest["tracks"]!.AsArray();
        Assert.Equal(2, tracks.Count);
        Assert.Contains(tracks.OfType<JsonObject>(), item => item["trackId"]!.GetValue<string>() == "existing");
        var imported = tracks.OfType<JsonObject>()
            .Single(item => item["trackId"]!.GetValue<string>() == "e2e-track");
        Assert.Equal("NB", imported["deviceName"]!.GetValue<string>());
        Assert.Equal("simulated", imported["source"]!.GetValue<string>());
        Assert.Equal("e2e-track.json", imported["file"]!.GetValue<string>());
    }

    [Fact]
    public async Task GpxImportUploadAndExportReimportStayConsistent()
    {
        const string gpx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
            <trk><name>E2E GPX</name><trkseg>
            <trkpt lat="25.0000" lon="121.5000"><ele>10</ele><time>2026-10-22T08:00:00Z</time></trkpt>
            <trkpt lat="25.0001" lon="121.5000"><ele>11</ele><time>2026-10-22T08:00:10Z</time></trkpt>
            <trkpt lat="25.0002" lon="121.5000"><ele>12</ele><time>2026-10-22T08:00:20Z</time></trkpt>
            </trkseg></trk></gpx>
            """;
        var build = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(gpx),
            new GpxBuildOptions { Id = "gpx-e2e" });
        Assert.True(build.IsSuccess);
        RecordDeviceNaming.ApplyDeviceName(build, "NB");

        var github = new FakeGitHubHandler();
        github.AddFile("gpstracker-web/data/manifest.json", """{"tracks":[]}""");
        var uploader = new GitHubRecordUploader(
            new HttpClient(github),
            CreateTokenStore(),
            retryDelay: TimeSpan.Zero);
        var upload = await uploader.UploadAsync(build);
        Assert.Equal(UploadStatus.Uploaded, upload.Status);

        var uploaded = JsonNode.Parse(
            github.Files["gpstracker-web/data/gpx-e2e.json"].Content)!.AsObject();
        Assert.Equal("gpx-import", uploaded["source"]!.GetValue<string>());
        Assert.Equal("NB", uploaded["deviceName"]!.GetValue<string>());
        var manifest = JsonNode.Parse(github.Files["gpstracker-web/data/manifest.json"].Content)!.AsObject();
        var manifestEntry = manifest["tracks"]!.AsArray()[0]!.AsObject();
        Assert.Equal("NB", manifestEntry["deviceName"]!.GetValue<string>());

        var exported = GpxExporter.Export(new GpxTrackDocument
        {
            StartTimeMs = build.Track!.StartTimeMs,
            Points = build.Track.Points,
        });
        var reimported = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(exported),
            new GpxBuildOptions { Id = "gpx-e2e-again" });

        Assert.True(reimported.IsSuccess);
        Assert.Equal(build.Track.Points.Count, reimported.Track!.Points.Count);
        Assert.Equal(build.Track.DurationMs, reimported.Track.DurationMs);
        TestAssert.Close(build.Track.DistanceMeters, reimported.Track.DistanceMeters, 0.01);
        Assert.Equal(build.Track.StartTimeMs, reimported.Track.StartTimeMs);
    }

    [Fact]
    public async Task DataSyncWritesLocalFilesAndKeepsThemWhenManifestFails()
    {
        var dataDir = Path.Combine(_root, "data");
        var handler = new FakeDataHandler();
        handler.Files["manifest.json"] = """{"tracks":[{"trackId":"a","file":"a.json"}]}""";
        handler.Files["a.json"] = """{"name":"local"}""";
        handler.Files["meta.json"] = """{"tracks":{}}""";
        var service = new DataSyncService(
            new HttpClient(handler),
            dataDir,
            Path.Combine(_root, "sync-cache.json"),
            clock: () => new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));

        var first = await service.SyncAsync();
        Assert.True(first.Success);
        Assert.Equal("""{"name":"local"}""", File.ReadAllText(Path.Combine(dataDir, "a.json")));

        var oldManifest = File.ReadAllText(Path.Combine(dataDir, "manifest.json"));
        handler.Fail.Add("manifest.json");
        var second = await service.SyncAsync();

        Assert.False(second.Success);
        Assert.Equal(oldManifest, File.ReadAllText(Path.Combine(dataDir, "manifest.json")));
        Assert.Equal("""{"name":"local"}""", File.ReadAllText(Path.Combine(dataDir, "a.json")));
    }

    [Fact]
    public async Task UpdateCheckDownloadShaAndReplaceWorksTogether()
    {
        var exeBytes = Encoding.UTF8.GetBytes("new-version-binary");
        var hash = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();
        var handler = new FakeUpdateHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(UpdateReleaseJson("0.2.0"));
        handler.Routes[UpdateZipUrl] = () => BinaryResponse(exeBytes);
        handler.Routes[UpdateShaUrl] = () => TextResponse($"{hash}  GpsTrackerPc.exe");
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.1.0");
        Assert.NotNull(update);
        var downloaded = Path.Combine(_root, "downloaded.exe");
        await service.DownloadAsync(update!, downloaded);

        var currentExe = Path.Combine(_root, "current.exe");
        File.WriteAllText(currentExe, "old-version-binary");
        var restarted = false;
        var apply = UpdateInstaller.Apply(downloaded, currentExe, () => restarted = true);

        Assert.True(apply.Success, apply.ErrorMessage);
        Assert.True(restarted);
        Assert.Equal(exeBytes, File.ReadAllBytes(currentExe));
        Assert.Equal("old-version-binary", File.ReadAllText(currentExe + ".old"));
    }

    [Fact]
    public async Task UpdateInstallFailureRestoresCurrentExe()
    {
        var exeBytes = Encoding.UTF8.GetBytes("new-version-binary");
        var hash = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();
        var handler = new FakeUpdateHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(UpdateReleaseJson("0.2.0"));
        handler.Routes[UpdateZipUrl] = () => BinaryResponse(exeBytes);
        handler.Routes[UpdateShaUrl] = () => TextResponse($"{hash}  GpsTrackerPc.exe");
        var service = new UpdateService(new HttpClient(handler));
        var update = await service.CheckAsync("0.1.0");
        var downloaded = Path.Combine(_root, "downloaded.exe");
        await service.DownloadAsync(update!, downloaded);

        var currentExe = Path.Combine(_root, "current.exe");
        File.WriteAllText(currentExe, "old-version-binary");
        var apply = UpdateInstaller.Apply(
            downloaded,
            currentExe,
            () => throw new IOException("restart failed"));

        Assert.False(apply.Success);
        Assert.Equal("old-version-binary", File.ReadAllText(currentExe));
        Assert.False(File.Exists(currentExe + ".old"));
    }

    private const string UpdateZipUrl =
        "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest/GpsTrackerPc.exe";

    private const string UpdateShaUrl = UpdateZipUrl + ".sha256";

    private TokenStore CreateTokenStore()
    {
        var store = new TokenStore(
            Path.Combine(_root, "token.bin"),
            new PassthroughProtector());
        store.Save("test-token");
        return store;
    }

    private static SimulationRoute CreateRoute(IReadOnlyList<RouteOutage>? outages = null) =>
        new(
            name: "E2E 路線",
            waypoints: new List<RouteWaypoint>
            {
                new("A", new Coordinates(25.0, 121.5)),
                new("B", new Coordinates(25.001, 121.5)),
            },
            speedKph: 36.0,
            loop: false,
            outages: outages);

    private static string UpdateReleaseJson(string version) => JsonSerializer.Serialize(new
    {
        name = $"GPS Tracker PC v{version}",
        body = "notes",
        assets = new object[]
        {
            new { name = "GpsTrackerPc.exe", browser_download_url = UpdateZipUrl, size = 123 },
            new { name = "GpsTrackerPc.exe.sha256", browser_download_url = UpdateShaUrl, size = 100 },
        },
    });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage TextResponse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage BinaryResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed record FakeFile(string Content, string Sha);

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        public Dictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

        public void AddFile(string path, string content) =>
            Files[path] = new FakeFile(content, Guid.NewGuid().ToString("N"));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = ExtractContentsPath(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                if (!Files.TryGetValue(path, out var file))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return JsonResponse(new JsonObject
                {
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content)),
                    ["sha"] = file.Sha,
                    ["encoding"] = "base64",
                }.ToJsonString());
            }

            if (request.Method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                var content = Encoding.UTF8.GetString(
                    Convert.FromBase64String(body["content"]!.GetValue<string>()));
                Files[path] = new FakeFile(content, Guid.NewGuid().ToString("N"));
                return JsonResponse("""{"content":{"sha":"ok"}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static string ExtractContentsPath(string absolutePath)
        {
            const string marker = "/contents/";
            var index = absolutePath.IndexOf(marker, StringComparison.Ordinal);
            return index < 0
                ? absolutePath
                : Uri.UnescapeDataString(absolutePath[(index + marker.Length)..]);
        }
    }

    private sealed class FakeDataHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Fail { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(request.RequestUri!.LocalPath);
            if (Fail.Contains(name))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (!Files.TryGetValue(name, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(JsonResponse(content));
        }
    }

    private sealed class FakeUpdateHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } =
            new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var factory))
            {
                return Task.FromResult(factory());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
