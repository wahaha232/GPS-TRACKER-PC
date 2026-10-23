using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Updates;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class UpdateServiceTests : IDisposable
{
    private const string ZipUrl =
        "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest/GpsTrackerPc-win-x64.zip";

    private const string ShaUrl = ZipUrl + ".sha256";

    private readonly string _root;

    public UpdateServiceTests()
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
    public async Task CheckReturnsNewerReleaseAndUsesUserAgent()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(ReleaseJson("0.2.0"));
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.1.0");

        Assert.NotNull(update);
        Assert.Equal("0.2.0", update!.Version);
        Assert.Equal(ZipUrl, update.DownloadUrl);
        Assert.Equal(ShaUrl, update.ChecksumUrl);
        Assert.Equal("GpsTrackerPc-win-x64.zip", update.AssetName);
        Assert.Equal(123, update.Size);
        Assert.Contains("GpsTrackerPc/1.0", handler.UserAgents[0]);
        Assert.Equal(UpdateService.LatestApiUrl, handler.Requested[0]);
    }

    [Fact]
    public async Task CheckPrefersSingleExeOverLegacyZip()
    {
        var exeUrl = ZipUrl.Replace(".zip", ".exe");
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(JsonSerializer.Serialize(new
        {
            name = "GPS Tracker PC v0.2.0",
            body = "release notes",
            assets = new object[]
            {
                new { name = "GpsTrackerPc-win-x64.zip", browser_download_url = ZipUrl, size = 123 },
                new { name = "GpsTrackerPc.exe", browser_download_url = exeUrl, size = 456 },
                new { name = "GpsTrackerPc.exe.sha256", browser_download_url = exeUrl + ".sha256", size = 100 },
            },
        }));
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.1.0");

        Assert.NotNull(update);
        Assert.Equal("GpsTrackerPc.exe", update!.AssetName);
        Assert.Equal(exeUrl, update.DownloadUrl);
        Assert.Equal(456, update.Size);
    }

    [Fact]
    public async Task CheckReturnsNullWhenVersionIsNotNewer()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(ReleaseJson("0.1.0"));
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.1.0");

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckReturnsNullForOlderVersion()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(ReleaseJson("0.0.9"));
        var service = new UpdateService(new HttpClient(handler));

        var update = await service.CheckAsync("0.2.0");

        Assert.Null(update);
    }

    [Fact]
    public async Task MissingReleaseGivesClear404Message()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] =
            () => new HttpResponseMessage(HttpStatusCode.NotFound);
        var service = new UpdateService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.2.0"));

        Assert.Contains("404", error.Message);
        Assert.Contains("Release", error.Message);
    }

    [Fact]
    public async Task NetworkFailureGivesClearMessage()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] =
            () => throw new HttpRequestException("offline");
        var service = new UpdateService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.2.0"));

        Assert.Contains("無法連線", error.Message);
    }

    [Fact]
    public async Task CheckRejectsUnsafeDownloadUrl()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
            JsonResponse(ReleaseJson("0.2.0", zipUrl: "https://evil.example/GpsTrackerPc.zip"));
        var service = new UpdateService(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.1.0"));
    }

    [Fact]
    public async Task CheckRequiresChecksumAsset()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
            JsonResponse(ReleaseJson("0.2.0", includeChecksum: false));
        var service = new UpdateService(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.1.0"));
    }

    [Fact]
    public async Task DownloadVerifiesSha256()
    {
        var content = Encoding.UTF8.GetBytes("new build contents");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => BinaryResponse(content);
        handler.Routes[ShaUrl] = () => TextResponse($"{hash}  GpsTrackerPc-win-x64.zip");
        var service = new UpdateService(new HttpClient(handler));
        var destination = Path.Combine(_root, "update.zip");

        await service.DownloadAsync(CreateUpdate(content.Length), destination);

        Assert.Equal(content, File.ReadAllBytes(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAllowsObjectsGitHubUserContentRedirect()
    {
        var content = Encoding.UTF8.GetBytes("new build contents");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => BinaryResponse(
            content,
            "https://objects.githubusercontent.com/github-production-release-asset/GpsTrackerPc.exe");
        handler.Routes[ShaUrl] = () => TextResponse($"{hash}  GpsTrackerPc.exe");
        var service = new UpdateService(new HttpClient(handler));
        var destination = Path.Combine(_root, "update.exe");

        await service.DownloadAsync(CreateUpdate(content.Length), destination);

        Assert.Equal(content, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task DownloadRejectsRedirectOutsideGitHubAndWritesNothing()
    {
        var content = Encoding.UTF8.GetBytes("tampered contents");
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => BinaryResponse(
            content,
            "https://evil.example/payload.exe");
        var service = new UpdateService(new HttpClient(handler));
        var destination = Path.Combine(_root, "update.exe");

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            service.DownloadAsync(CreateUpdate(content.Length), destination));

        Assert.Contains("不允許的網域", error.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.DoesNotContain(ShaUrl, handler.Requested);
    }

    [Fact]
    public async Task BadChecksumDeletesDownload()
    {
        var content = Encoding.UTF8.GetBytes("tampered contents");
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => BinaryResponse(content);
        handler.Routes[ShaUrl] = () => TextResponse(new string('0', 64) + "  GpsTrackerPc-win-x64.zip");
        var service = new UpdateService(new HttpClient(handler));
        var destination = Path.Combine(_root, "update.zip");

        await Assert.ThrowsAsync<UpdateException>(() =>
            service.DownloadAsync(CreateUpdate(content.Length), destination));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task UnsafeDownloadUrlIsRejectedBeforeNetwork()
    {
        var handler = new FakeHandler();
        var service = new UpdateService(new HttpClient(handler));
        var update = new UpdateInfo(
            "0.2.0",
            "GPS Tracker PC v0.2.0",
            "https://evil.example/update.zip",
            ShaUrl,
            "update.zip",
            1,
            null);
        var destination = Path.Combine(_root, "update.zip");

        await Assert.ThrowsAsync<UpdateException>(() =>
            service.DownloadAsync(update, destination));

        Assert.Empty(handler.Requested);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task HttpErrorIsReported()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var service = new UpdateService(new HttpClient(handler));

        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.1.0"));
    }

    [Fact]
    public async Task CheckSendsConfiguredToken()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(ReleaseJson("0.2.0"));
        var service = new UpdateService(new HttpClient(handler), tokenProvider: () => "test-token");

        var update = await service.CheckAsync("0.1.0");

        Assert.NotNull(update);
        Assert.Equal("Bearer test-token", handler.Authorizations[0]);
    }

    [Fact]
    public async Task DownloadDoesNotSendToken()
    {
        var payload = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => BinaryResponse(payload);
        handler.Routes[ShaUrl] = () => TextResponse(hash);
        var service = new UpdateService(new HttpClient(handler), tokenProvider: () => "test-token");
        var destination = Path.Combine(_root, "update.zip");

        await service.DownloadAsync(CreateUpdate(payload.Length), destination);

        Assert.True(File.Exists(destination));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    [Fact]
    public async Task InvalidTokenFallsBackToAnonymousCheck()
    {
        var handler = new FakeHandler();
        handler.Dynamic = request =>
            request.Headers.Contains("Authorization")
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(ReleaseJson("0.2.0"));
        var service = new UpdateService(new HttpClient(handler), tokenProvider: () => "expired-token");

        var update = await service.CheckAsync("0.1.0");

        Assert.NotNull(update);
        Assert.Equal(2, handler.Requested.Count);
        Assert.Equal("Bearer expired-token", handler.Authorizations[0]);
        Assert.Null(handler.Authorizations[1]);
    }

    [Fact]
    public async Task TokenReadFailureFallsBackToAnonymousCheck()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => JsonResponse(ReleaseJson("0.2.0"));
        var service = new UpdateService(
            new HttpClient(handler),
            tokenProvider: () => throw new InvalidOperationException("DPAPI failed"));

        var update = await service.CheckAsync("0.1.0");

        Assert.NotNull(update);
        Assert.Null(handler.Authorizations[0]);
    }

    [Fact]
    public async Task RateLimitedCheckExplainsResetTimeAndToken()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(10);
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation(
                "X-RateLimit-Reset",
                reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        };
        var service = new UpdateService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.5.2"));

        Assert.Contains("403", error.Message);
        Assert.Contains("查詢次數已用完", error.Message);
        Assert.Contains("GitHub Token", error.Message);
        Assert.Contains(reset.ToLocalTime().ToString("HH:mm"), error.Message);
    }

    [Fact]
    public async Task TooManyRequestsSuggestsWaiting()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        };
        var service = new UpdateService(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.5.2"));

        Assert.Contains("429", error.Message);
        Assert.Contains("30 秒", error.Message);
    }

    [Fact]
    public async Task ForbiddenWithDetailShowsGitHubMessage()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"message\":\"Resource not accessible by personal access token\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "4999");
            return response;
        };
        var service = new UpdateService(new HttpClient(handler), tokenProvider: () => "limited-token");

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.5.2"));

        Assert.Contains("403", error.Message);
        Assert.Contains("Resource not accessible by personal access token", error.Message);
    }

    [Fact]
    public async Task DownloadTimesOutWhenServerStalls()
    {
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => StallingResponse(TimeSpan.FromSeconds(5));
        var service = new UpdateService(
            new HttpClient(handler),
            downloadTimeout: TimeSpan.FromMilliseconds(150));
        var destination = Path.Combine(_root, "timeout.zip");

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            service.DownloadAsync(CreateUpdate(100), destination));

        Assert.Contains("逾時", error.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadCanBeCancelledByCaller()
    {
        var handler = new FakeHandler();
        handler.Routes[ZipUrl] = () => StallingResponse(TimeSpan.FromSeconds(5));
        var service = new UpdateService(
            new HttpClient(handler),
            downloadTimeout: TimeSpan.FromMinutes(1));
        var destination = Path.Combine(_root, "cancelled.zip");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(
                CreateUpdate(100),
                destination,
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CheckTimesOutWhenServerStalls()
    {
        var handler = new FakeHandler();
        handler.Routes[UpdateService.LatestApiUrl] = () => StallingResponse(TimeSpan.FromSeconds(5));
        var service = new UpdateService(
            new HttpClient(handler),
            checkTimeout: TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync("0.5.5"));

        Assert.Contains("逾時", error.Message);
    }

    private static UpdateInfo CreateUpdate(long size) =>
        new(
            "0.2.0",
            "GPS Tracker PC v0.2.0",
            ZipUrl,
            ShaUrl,
            "GpsTrackerPc-win-x64.zip",
            size,
            "notes");

    private static string ReleaseJson(
        string version,
        string zipUrl = ZipUrl,
        string? checksumUrl = ShaUrl,
        bool includeChecksum = true)
    {
        var assets = new List<object>
        {
            new
            {
                name = "GpsTrackerPc-win-x64.zip",
                browser_download_url = zipUrl,
                size = 123,
            },
        };
        if (includeChecksum)
        {
            assets.Add(new
            {
                name = "GpsTrackerPc-win-x64.zip.sha256",
                browser_download_url = checksumUrl,
                size = 100,
            });
        }

        return JsonSerializer.Serialize(new
        {
            name = $"GPS Tracker PC v{version}",
            body = "release notes",
            assets,
        });
    }

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

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string? finalUri = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        if (!string.IsNullOrWhiteSpace(finalUri))
        {
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        }

        return response;
    }

    private static HttpResponseMessage StallingResponse(TimeSpan delay) =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(delay)),
        };

    private sealed class StallingStream : Stream
    {
        private readonly TimeSpan _delay;

        public StallingStream(TimeSpan delay)
        {
            _delay = delay;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

        /// <summary>需要依請求內容（例如有沒有帶 Authorization）回應時使用。</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? Dynamic { get; set; }

        public List<string> Requested { get; } = new();

        public List<string> UserAgents { get; } = new();

        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requested.Add(uri);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            Authorizations.Add(request.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null);
            if (Dynamic != null)
            {
                return Task.FromResult(Dynamic(request));
            }

            if (!Routes.TryGetValue(uri, out var factory))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(factory());
        }
    }
}
