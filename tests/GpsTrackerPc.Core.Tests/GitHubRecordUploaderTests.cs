using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class GitHubRecordUploaderTests : IDisposable
{
    private const string ManifestPath = "gpstracker-web/data/manifest.json";
    private const string MetaPath = "gpstracker-web/data/meta.json";

    private readonly string _root;

    public GitHubRecordUploaderTests()
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
    public async Task UploadWritesTrackManifestAndMetaTitle()
    {
        var handler = new FakeGitHubHandler();
        handler.AddFile(ManifestPath, """{"tracks":[]}""");
        var build = BuildSample();
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Uploaded, result.Status);
        var trackPath = $"gpstracker-web/data/{build.Entry!.TrackId}.json";
        Assert.True(handler.Files.ContainsKey(trackPath));
        var track = JsonNode.Parse(handler.Files[trackPath].Content)!.AsObject();
        Assert.Equal("gpx-import", track["source"]!.GetValue<string>());
        Assert.Null(track["deviceName"]);

        var manifest = JsonNode.Parse(handler.Files[ManifestPath].Content)!.AsObject();
        var tracks = manifest["tracks"]!.AsArray();
        Assert.Single(tracks);
        Assert.Equal(build.Entry.TrackId, tracks[0]!["trackId"]!.GetValue<string>());
        Assert.Null(tracks[0]!["deviceName"]);

        var meta = JsonNode.Parse(handler.Files[MetaPath].Content)!.AsObject();
        Assert.Equal(
            "Morning Run",
            meta["tracks"]![build.Entry.TrackId]!["title"]!.GetValue<string>());

        Assert.Equal(3, handler.Requests.Count(request => request.Method == "PUT"));
        Assert.All(handler.Requests, request =>
            Assert.Equal("Bearer test-token", request.Authorization));
    }

    [Fact]
    public async Task UploadWithoutTitleDoesNotWriteMeta()
    {
        var handler = new FakeGitHubHandler();
        handler.AddFile(ManifestPath, """{"tracks":[]}""");
        var build = BuildSample(name: null);
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Uploaded, result.Status);
        Assert.False(handler.Files.ContainsKey(MetaPath));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == "PUT"));
    }

    [Fact]
    public async Task DuplicateIsSkippedWithoutAnyWrite()
    {
        var handler = new FakeGitHubHandler();
        var build = BuildSample();
        var entry = build.Entry!;
        handler.AddFile(
            ManifestPath,
            new JsonObject
            {
                ["tracks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["trackId"] = "existing",
                        ["startTimeMs"] = entry.StartTimeMs + 2000,
                        ["distanceMeters"] = entry.DistanceMeters + 5,
                    },
                },
            }.ToJsonString());
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Duplicate, result.Status);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "PUT");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task ManifestConflictIsMergedAndRetried(HttpStatusCode conflictStatus)
    {
        var handler = new FakeGitHubHandler();
        handler.AddFile(
            ManifestPath,
            new JsonObject
            {
                ["tracks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["trackId"] = "other",
                        ["startTimeMs"] = 1,
                        ["distanceMeters"] = 1,
                    },
                },
            }.ToJsonString());
        handler.FailPutPath = ManifestPath;
        handler.FailPutStatus = conflictStatus;
        handler.FailPutCount = 1;
        var build = BuildSample();
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Uploaded, result.Status);
        var manifest = JsonNode.Parse(handler.Files[ManifestPath].Content)!.AsObject();
        var trackIds = manifest["tracks"]!.AsArray()
            .Select(track => track!["trackId"]!.GetValue<string>())
            .ToList();
        Assert.Contains("other", trackIds);
        Assert.Contains(build.Entry!.TrackId, trackIds);
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == "PUT" && request.Path == ManifestPath));
    }

    [Fact]
    public async Task DuplicateAddedDuringConflictMergeIsSkipped()
    {
        var handler = new FakeGitHubHandler();
        handler.AddFile(ManifestPath, """{"tracks":[]}""");
        handler.FailPutPath = ManifestPath;
        handler.FailPutCount = 1;
        var build = BuildSample();
        handler.AfterFailedPut = () =>
        {
            // 模擬另一台電腦在我們重試前寫入了同一段活動（不同 trackId）。
            handler.AddFile(
                ManifestPath,
                new JsonObject
                {
                    ["tracks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["trackId"] = "raced",
                            ["startTimeMs"] = build.Entry!.StartTimeMs + 1000,
                            ["distanceMeters"] = build.Entry.DistanceMeters + 1,
                        },
                    },
                }.ToJsonString());
        };
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Duplicate, result.Status);
        var manifest = JsonNode.Parse(handler.Files[ManifestPath].Content)!.AsObject();
        var trackIds = manifest["tracks"]!.AsArray()
            .Select(track => track!["trackId"]!.GetValue<string>())
            .ToList();
        Assert.Equal(new[] { "raced" }, trackIds);
        Assert.Equal(1, handler.Requests.Count(request =>
            request.Method == "PUT" && request.Path == ManifestPath));
    }

    [Fact]
    public async Task TrackConflictReReadsShaAndRetries()
    {
        var handler = new FakeGitHubHandler();
        handler.AddFile(ManifestPath, """{"tracks":[]}""");
        var build = BuildSample();
        var trackPath = $"gpstracker-web/data/{build.Entry!.TrackId}.json";
        handler.FailPutPath = trackPath;
        handler.FailPutStatus = HttpStatusCode.Conflict;
        handler.FailPutCount = 1;
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(build);

        Assert.Equal(UploadStatus.Uploaded, result.Status);
        Assert.True(handler.Files.ContainsKey(trackPath));
        Assert.Equal(2, handler.Requests.Count(request =>
            request.Method == "PUT" && request.Path == trackPath));
    }

    [Fact]
    public async Task UnauthorizedTokenIsReportedAsInvalidCredentials()
    {
        var handler = new FakeGitHubHandler { FailGetStatus = HttpStatusCode.Unauthorized };
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(BuildSample());

        Assert.Equal(UploadStatus.Failed, result.Status);
        Assert.Contains("Token", result.ErrorMessage);
        Assert.Contains("權限", result.ErrorMessage);
    }

    [Fact]
    public async Task MissingTokenDoesNotCallGitHub()
    {
        var handler = new FakeGitHubHandler();
        var store = new TokenStore(
            Path.Combine(_root, "empty-token.bin"),
            new PassthroughProtector());
        var uploader = new GitHubRecordUploader(
            new HttpClient(handler),
            store,
            retryDelay: TimeSpan.Zero);

        var result = await uploader.UploadAsync(BuildSample());

        Assert.Equal(UploadStatus.Failed, result.Status);
        Assert.Contains("Token", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ParseErrorIsReportedWithoutCallingGitHub()
    {
        var handler = new FakeGitHubHandler();
        var uploader = CreateUploader(handler);

        var result = await uploader.UploadAsync(GpxBuildResult.Fail("no-time"));

        Assert.Equal(UploadStatus.Failed, result.Status);
        Assert.Contains("時間", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    private GitHubRecordUploader CreateUploader(FakeGitHubHandler handler)
    {
        var tokenPath = Path.Combine(_root, "token.bin");
        var store = new TokenStore(tokenPath, new PassthroughProtector());
        store.Save("test-token");
        return new GitHubRecordUploader(
            new HttpClient(handler),
            store,
            retryDelay: TimeSpan.Zero);
    }

    private static GpxBuildResult BuildSample(string? name = "Morning Run", string id = "test-track")
    {
        var nameTag = name == null ? string.Empty : $"<name>{name}</name>";
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\">" +
            $"<trk>{nameTag}<trkseg>" +
            "<trkpt lat=\"25.0000\" lon=\"121.5000\"><ele>10</ele><time>2026-10-10T08:00:00Z</time></trkpt>" +
            "<trkpt lat=\"25.0001\" lon=\"121.5000\"><ele>11</ele><time>2026-10-10T08:00:10Z</time></trkpt>" +
            "<trkpt lat=\"25.0002\" lon=\"121.5000\"><ele>12</ele><time>2026-10-10T08:00:20Z</time></trkpt>" +
            "</trkseg></trk></gpx>";
        return GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(xml),
            new GpxBuildOptions { Id = id, Mode = "WALKING" });
    }

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed record FakeFile(string Content, string Sha);

    private sealed record RequestRecord(string Method, string Path, string? Body, string? Authorization);

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        public Dictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

        public List<RequestRecord> Requests { get; } = new();

        public string? FailPutPath { get; set; }

        public int FailPutCount { get; set; }

        public HttpStatusCode FailPutStatus { get; set; } = HttpStatusCode.Conflict;

        public HttpStatusCode? FailGetStatus { get; set; }

        /// <summary>PUT 失敗後執行，用來模擬其他電腦在重試前寫入資料。</summary>
        public Action? AfterFailedPut { get; set; }

        public void AddFile(string path, string content)
        {
            Files[path] = new FakeFile(content, Guid.NewGuid().ToString("N"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = ExtractPath(request.RequestUri!.AbsolutePath);
            var body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestRecord(
                request.Method.Method,
                path,
                body,
                request.Headers.Authorization?.ToString()));

            if (request.Method == HttpMethod.Get)
            {
                if (FailGetStatus.HasValue)
                {
                    return new HttpResponseMessage(FailGetStatus.Value);
                }

                if (!Files.TryGetValue(path, out var file))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var payload = new JsonObject
                {
                    ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Content)),
                    ["sha"] = file.Sha,
                    ["encoding"] = "base64",
                };
                return JsonResponse(payload.ToJsonString());
            }

            if (request.Method == HttpMethod.Put)
            {
                if (path == FailPutPath && FailPutCount > 0)
                {
                    FailPutCount--;
                    AfterFailedPut?.Invoke();
                    return new HttpResponseMessage(FailPutStatus);
                }

                var json = JsonNode.Parse(body!)!;
                var content = Encoding.UTF8.GetString(
                    Convert.FromBase64String(json["content"]!.GetValue<string>()));
                Files[path] = new FakeFile(content, Guid.NewGuid().ToString("N"));
                return JsonResponse("""{"content":{"sha":"ok"}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static string ExtractPath(string absolutePath)
        {
            const string marker = "/contents/";
            var index = absolutePath.IndexOf(marker, StringComparison.Ordinal);
            return index < 0
                ? absolutePath
                : Uri.UnescapeDataString(absolutePath[(index + marker.Length)..]);
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }
}
