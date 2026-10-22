using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Data;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class DataSyncServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 22, 6, 30, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _dataDirectory;
    private readonly FakeGitHubHandler _handler = new();

    public DataSyncServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        _dataDirectory = Path.Combine(_root, "data");
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
    public async Task FirstSync_DownloadsManifestTracksAndOptionalFiles()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json"), ("track-b", null)), "m1");
        _handler.Add("a.json", "{\"id\":\"a\"}", "a1");
        // 沒有 file 欄位時，檔名由 trackId 推算。
        _handler.Add("track-b.json", "{\"id\":\"b\"}", "b1");
        _handler.Add("meta.json", "{\"tracks\":{}}", "meta1");
        _handler.Add("metro.json", "{\"routes\":[]}", "metro1");
        _handler.Add("steps.json", "{\"days\":[]}", "steps1");
        _handler.Add("voyager.json", "{\"data\":{}}", "voyager1");

        var result = await CreateService().SyncAsync();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(7, result.DownloadedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("manifest.json", _handler.RequestedFiles[0]);
        Assert.Equal("{\"id\":\"a\"}", ReadData("a.json"));
        Assert.Equal("{\"id\":\"b\"}", ReadData("track-b.json"));
        Assert.Equal("{\"routes\":[]}", ReadData("metro.json"));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task SecondSync_UsesConditionalRequestsAndSkipsUnchangedFiles()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m1");
        _handler.Add("a.json", "{\"id\":\"a\"}", "a1");
        var service = CreateService();

        var first = await service.SyncAsync();
        var second = await service.SyncAsync();

        Assert.True(first.Success);
        Assert.True(second.Success, string.Join("；", second.Errors));
        Assert.Equal(0, second.DownloadedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.True(second.SkippedCount >= 2);
        Assert.Equal(2, _handler.RequestedFiles.Count(name => name == "manifest.json"));
        Assert.Equal("{\"id\":\"a\"}", ReadData("a.json"));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task ChangedTrack_IsUpdatedAndOtherFilesStayUntouched()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json"), ("track-b", "b.json")), "m1");
        _handler.Add("a.json", "old-a", "a1");
        _handler.Add("b.json", "old-b", "b1");
        var service = CreateService();
        await service.SyncAsync();

        _handler.Add("a.json", "new-a", "a2");
        var result = await service.SyncAsync();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.DownloadedCount);
        Assert.Equal("new-a", ReadData("a.json"));
        Assert.Equal("old-b", ReadData("b.json"));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task FailedManifest_KeepsEveryLocalFileAndReportsFailure()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m1");
        _handler.Add("a.json", "old-a", "a1");
        var service = CreateService();
        await service.SyncAsync();
        var oldManifest = ReadData("manifest.json");

        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m2");
        _handler.Add("a.json", "new-a", "a2");
        _handler.FailingFiles.Add("manifest.json");
        var result = await service.SyncAsync();

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error => error.Contains("manifest.json", StringComparison.Ordinal));
        Assert.Equal(oldManifest, ReadData("manifest.json"));
        Assert.Equal("old-a", ReadData("a.json"));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task FailedTrack_KeepsOldFileAndContinuesWithTheRest()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json"), ("track-b", "b.json")), "m1");
        _handler.Add("a.json", "old-a", "a1");
        _handler.Add("b.json", "old-b", "b1");
        var service = CreateService();
        await service.SyncAsync();

        _handler.Add("manifest.json", Manifest(("track-a", "a.json"), ("track-b", "b.json")), "m2");
        _handler.Add("b.json", "new-b", "b2");
        _handler.FailingFiles.Add("a.json");
        var result = await service.SyncAsync();

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("old-a", ReadData("a.json"));
        Assert.Equal("new-b", ReadData("b.json"));
        Assert.True(result.UpdatedCount >= 2, "manifest 與 b.json 都應更新");
        Assert.Contains(result.Errors, error => error.Contains("a.json", StringComparison.Ordinal));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task MissingOptionalFiles_AreSkippedWithoutFailingTheSync()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m1");
        _handler.Add("a.json", "a", "a1");

        var result = await CreateService().SyncAsync();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(4, result.SkippedCount);
    }

    [Fact]
    public async Task InvalidManifest_KeepsTheOldManifestAndDoesNotDownloadTracks()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m1");
        _handler.Add("a.json", "old-a", "a1");
        var service = CreateService();
        await service.SyncAsync();
        var oldManifest = ReadData("manifest.json");

        _handler.Add("manifest.json", "this is not json", "m2");
        var result = await service.SyncAsync();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("格式錯誤", StringComparison.Ordinal));
        Assert.Equal(oldManifest, ReadData("manifest.json"));
        Assert.Equal("old-a", ReadData("a.json"));
    }

    [Fact]
    public async Task UnsafeFileNamesInManifest_AreIgnored()
    {
        _handler.Add(
            "manifest.json",
            Manifest(("evil", "../evil.json"), ("absolute", "C:\\evil.json"), ("ok", "ok.json")),
            "m1");
        _handler.Add("ok.json", "ok", "ok1");

        var result = await CreateService().SyncAsync();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal("ok", ReadData("ok.json"));
        Assert.False(File.Exists(Path.Combine(_root, "evil.json")));
        Assert.DoesNotContain("evil.json", _handler.RequestedFiles);
        AssertNoTempFiles();
    }

    [Fact]
    public async Task CancelledSync_DoesNotWriteFiles()
    {
        _handler.Add("manifest.json", Manifest(("track-a", "a.json")), "m1");
        _handler.Add("a.json", "a", "a1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().SyncAsync(cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_dataDirectory, "manifest.json")));
    }

    private DataSyncService CreateService() =>
        new(
            new HttpClient(_handler),
            _dataDirectory,
            Path.Combine(_root, "sync-cache.json"),
            clock: () => FixedNow);

    private string ReadData(string fileName) =>
        File.ReadAllText(Path.Combine(_dataDirectory, fileName));

    private void AssertNoTempFiles()
    {
        if (!Directory.Exists(_dataDirectory))
        {
            return;
        }

        Assert.Empty(Directory.GetFiles(_dataDirectory, "*.tmp-*", SearchOption.AllDirectories));
    }

    private static string Manifest(params (string TrackId, string? File)[] tracks)
    {
        var items = new List<Dictionary<string, object?>>();
        foreach (var track in tracks)
        {
            var item = new Dictionary<string, object?>
            {
                ["trackId"] = track.TrackId,
            };
            if (track.File != null)
            {
                item["file"] = track.File;
            }

            items.Add(item);
        }

        return JsonSerializer.Serialize(new { tracks = items });
    }

    private sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, FakeFile> _files = new(StringComparer.Ordinal);

        public HashSet<string> FailingFiles { get; } = new(StringComparer.Ordinal);

        public List<string> RequestedFiles { get; } = new();

        public void Add(string fileName, string content, string version)
        {
            _files[fileName] = new FakeFile(content, "\"" + version + "\"");
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(request.RequestUri!.LocalPath);
            RequestedFiles.Add(fileName);

            if (FailingFiles.Contains(fileName))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            if (!_files.TryGetValue(fileName, out var file))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == file.ETag))
            {
                var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModified.Headers.ETag = new EntityTagHeaderValue(file.ETag);
                return Task.FromResult(notModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Content)),
            };
            response.Headers.ETag = new EntityTagHeaderValue(file.ETag);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
            return Task.FromResult(response);
        }

        private sealed record FakeFile(string Content, string ETag);
    }
}
