using System.Net;
using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Social;
using GpsTrackerPc.Simulation.Model;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class SocialTests : IDisposable
{
    private readonly string _root;

    public SocialTests()
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
    public void FriendInviteRoundTrips()
    {
        var now = DateTimeOffset.Now;
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "等待好友加入", "https://ntfy.sh/", now);

        var code = SocialInviteCode.Encode(target, "我的電腦");
        var decoded = SocialInviteCode.Decode(code, now);

        Assert.Equal(SocialTargetKind.Friend, decoded.Kind);
        Assert.Equal(target.Id, decoded.Id);
        Assert.Equal(target.Server, decoded.Server);
        Assert.Equal(target.Topic, decoded.Topic);
        Assert.Equal(target.KeyBase64, decoded.KeyBase64);
        Assert.Equal("我的電腦", decoded.Name);
    }

    [Fact]
    public void GroupInviteRoundTripsWithGroupName()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Group, "週末登山", SocialLimits.DefaultServer, DateTimeOffset.Now);

        var decoded = SocialInviteCode.Decode(SocialInviteCode.Encode(target, "我的電腦"), DateTimeOffset.Now);

        Assert.Equal(SocialTargetKind.Group, decoded.Kind);
        Assert.Equal("週末登山", decoded.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC123")]
    [InlineData("GTF1-!!!!")]
    [InlineData("GTF1-abc")]
    public void InvalidInviteCodeIsRejected(string code)
    {
        Assert.Throws<SocialException>(() => SocialInviteCode.Decode(code, DateTimeOffset.Now));
    }

    [Fact]
    public void InviteCodeCannotBeUsedWithAnotherVersion()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 2,
            k = "friend",
            i = "abc",
            n = "name",
            s = "https://ntfy.sh",
            t = "topic",
            key = SocialCrypto.CreateKeyBase64(),
        });
        var code = SocialLimits.InvitePrefix +
                   Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<SocialException>(() => SocialInviteCode.Decode(code, DateTimeOffset.Now));
    }

    [Fact]
    public void CryptoRoundTripsAndRejectsWrongKey()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var snapshot = Snapshot("device-000a", "客廳電腦", 25.033964, 121.564472, now: DateTimeOffset.Now);

        var encrypted = SocialCrypto.Encrypt(snapshot, target.KeyBase64);
        var decrypted = SocialCrypto.TryDecrypt(encrypted, target.KeyBase64);

        Assert.NotNull(decrypted);
        Assert.Equal(snapshot.DeviceId, decrypted!.DeviceId);
        Assert.Equal(snapshot.DeviceName, decrypted.DeviceName);
        Assert.Equal(snapshot.Latitude, decrypted.Latitude);
        Assert.Equal(snapshot.Longitude, decrypted.Longitude);
        Assert.Equal(snapshot.SentAtMs, decrypted.SentAtMs);
        Assert.True(decrypted.Sharing);

        var otherKey = SocialCrypto.CreateKeyBase64();
        Assert.Null(SocialCrypto.TryDecrypt(encrypted, otherKey));
    }

    [Fact]
    public void CryptoUsesDifferentNonceEachTime()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Group, "群組", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var snapshot = Snapshot("device-000a", "電腦", 25.0, 121.0, now: DateTimeOffset.Now);

        var first = SocialCrypto.Encrypt(snapshot, target.KeyBase64);
        var second = SocialCrypto.Encrypt(snapshot, target.KeyBase64);

        Assert.NotEqual(first, second);
        Assert.NotNull(SocialCrypto.TryDecrypt(first, target.KeyBase64));
        Assert.NotNull(SocialCrypto.TryDecrypt(second, target.KeyBase64));
    }

    [Fact]
    public void TamperedEnvelopeIsRejected()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var encrypted = SocialCrypto.Encrypt(Snapshot("device-000a", "電腦", 25.0, 121.0, now: DateTimeOffset.Now), target.KeyBase64);
        var tampered = encrypted[..^1] + (encrypted[^1] == 'A' ? 'B' : 'A');

        Assert.Null(SocialCrypto.TryDecrypt(tampered, target.KeyBase64));
    }

    [Fact]
    public void SettingsStoreCreatesStableDeviceIdAndSavesTargets()
    {
        var path = Path.Combine(_root, "social.json");
        var store = new SocialSettingsStore(path);

        var first = store.Load();
        var second = store.Load();
        Assert.False(string.IsNullOrWhiteSpace(first.DeviceId));
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(SocialLimits.DefaultServer, second.Server);

        var target = SocialCrypto.CreateTarget(SocialTargetKind.Group, "家人", second.Server, DateTimeOffset.Now);
        store.AddTarget(target);
        var loaded = store.Load();
        Assert.Single(loaded.Targets);
        Assert.Equal(target.Id, loaded.Targets[0].Id);
    }

    [Fact]
    public void SettingsStoreDropsInvalidAndDuplicateTargets()
    {
        var path = Path.Combine(_root, "social.json");
        var store = new SocialSettingsStore(path);
        var valid = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var invalid = valid with { KeyBase64 = "not-a-key" };
        var settings = new SocialSettings
        {
            DeviceId = "device-12345678",
            Server = SocialLimits.DefaultServer,
            Targets = new List<SocialTarget> { valid, valid, invalid },
        };
        store.Save(settings);

        var loaded = store.Load();

        Assert.Single(loaded.Targets);
        Assert.Equal(valid.Id, loaded.Targets[0].Id);
    }

    [Fact]
    public void SettingsStoreKeepsOnlyFiftyTargets()
    {
        var path = Path.Combine(_root, "social.json");
        var store = new SocialSettingsStore(path);
        var settings = new SocialSettings
        {
            DeviceId = "device-12345678",
            Server = SocialLimits.DefaultServer,
            Targets = Enumerable.Range(0, 60)
                .Select(index => SocialCrypto.CreateTarget(
                    SocialTargetKind.Friend,
                    "好友" + index,
                    SocialLimits.DefaultServer,
                    DateTimeOffset.Now))
                .ToList(),
        };
        store.Save(settings);

        Assert.Equal(SocialLimits.MaxTargets, store.Load().Targets.Count);
    }

    [Fact]
    public void PresencePicksLatestSnapshotAndMarksStoppedSharingOffline()
    {
        var now = DateTimeOffset.Now;
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, now);
        var snapshots = new Dictionary<string, IReadOnlyList<SocialSnapshot>>
        {
            [target.Id] = new[]
            {
                Snapshot("peer-0001", "小明", 25.0, 121.0, now: now.AddMinutes(-5)),
                Snapshot("peer-0001", "小明", 25.1, 121.1, now: now.AddMinutes(-1)),
                Snapshot("peer-0001", "小明", 25.2, 121.2, now: now, sharing: false),
                Snapshot("self-0001", "自己", 24.0, 120.0, now: now),
            },
        };

        var peers = SocialPresence.Build(
            new[] { target },
            snapshots,
            "self-0001",
            new Coordinates(25.0, 121.0),
            now);

        var peer = Assert.Single(peers);
        Assert.Equal(25.2, peer.Latitude);
        Assert.False(peer.Sharing);
        Assert.False(peer.Online);
        Assert.NotNull(peer.DistanceMeters);
    }

    [Fact]
    public void PresenceMarksRecentSharingOnlineAndComputesDistance()
    {
        var now = DateTimeOffset.Now;
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Group, "群組", SocialLimits.DefaultServer, now);
        var snapshots = new Dictionary<string, IReadOnlyList<SocialSnapshot>>
        {
            [target.Id] = new[] { Snapshot("peer-0001", "小明", 25.033964, 121.564472, now: now.AddSeconds(-30)) },
        };

        var peers = SocialPresence.Build(
            new[] { target },
            snapshots,
            "self-0001",
            new Coordinates(25.033964, 121.564472),
            now);

        var peer = Assert.Single(peers);
        Assert.True(peer.Online);
        Assert.Equal(SocialTargetKind.Group, peer.Kind);
        Assert.NotNull(peer.DistanceMeters);
        Assert.True(peer.DistanceMeters < 1.0);
    }

    [Fact]
    public void PresenceIgnoresOldSnapshots()
    {
        var now = DateTimeOffset.Now;
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, now);
        var snapshots = new Dictionary<string, IReadOnlyList<SocialSnapshot>>
        {
            [target.Id] = new[] { Snapshot("peer-0001", "小明", 25.0, 121.0, now: now.AddDays(-8)) },
        };

        Assert.Empty(SocialPresence.Build(new[] { target }, snapshots, "self-0001", null, now));
    }

    [Fact]
    public void DirectoryKeepsLastMessageIdAndPrunesRemovedTargets()
    {
        var directory = new SocialDirectory();
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        directory.Merge(target.Id, new[] { Snapshot("peer-0001", "小明", 25.0, 121.0, now: DateTimeOffset.Now) });
        directory.SetLastMessageId(target.Id, "msg-1");

        Assert.Equal("msg-1", directory.GetLastMessageId(target.Id));
        Assert.Single(directory.CreateSnapshotView());

        directory.Retain(Array.Empty<string>());
        Assert.Empty(directory.CreateSnapshotView());
        Assert.Null(directory.GetLastMessageId(target.Id));
    }

    [Fact]
    public async Task TransportPublishesEncryptedBodyToTopic()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var handler = new FakeHandler
        {
            Response = _ => new HttpResponseMessage(HttpStatusCode.OK),
        };
        var transport = new NtfySocialTransport(new HttpClient(handler));
        var snapshot = Snapshot("device-000a", "電腦", 25.0, 121.0, now: DateTimeOffset.Now);

        await transport.PublishAsync(target, snapshot);

        Assert.Equal($"{SocialLimits.DefaultServer}/{target.Topic}", handler.LastUri!.AbsoluteUri);
        Assert.Equal("GPS Tracker", handler.LastTitle);
        var decrypted = SocialCrypto.TryDecrypt(handler.LastBody, target.KeyBase64);
        Assert.NotNull(decrypted);
        Assert.Equal(snapshot.DeviceId, decrypted!.DeviceId);
    }

    [Fact]
    public async Task TransportPollsJsonStreamAndUsesSinceId()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Group, "群組", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var first = Snapshot("peer-0001", "小明", 25.0, 121.0, now: DateTimeOffset.Now);
        var second = Snapshot("peer-0002", "小華", 25.1, 121.1, now: DateTimeOffset.Now);
        var lines = string.Join(
            "\n",
            JsonSerializer.Serialize(new { id = "msg-1", @event = "message", message = SocialCrypto.Encrypt(first, target.KeyBase64) }),
            "{ broken json",
            JsonSerializer.Serialize(new { id = "msg-2", @event = "message", message = SocialCrypto.Encrypt(second, target.KeyBase64) }),
            JsonSerializer.Serialize(new { id = "msg-3", @event = "keepalive" }));
        var handler = new FakeHandler
        {
            Response = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(lines, Encoding.UTF8, "application/x-ndjson"),
            },
        };
        var transport = new NtfySocialTransport(new HttpClient(handler));

        var result = await transport.PollAsync(target, "msg-0");

        Assert.Equal(2, result.Snapshots.Count);
        Assert.Equal("msg-2", result.LastMessageId);
        Assert.Contains("since=msg-0", handler.LastUri!.Query);
        Assert.Contains("poll=1", handler.LastUri.Query);
    }

    [Fact]
    public async Task TransportReportsRateLimitClearly()
    {
        var target = SocialCrypto.CreateTarget(SocialTargetKind.Friend, "好友", SocialLimits.DefaultServer, DateTimeOffset.Now);
        var handler = new FakeHandler
        {
            Response = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limit", Encoding.UTF8, "text/plain"),
            },
        };
        var transport = new NtfySocialTransport(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<SocialException>(() => transport.PollAsync(target, null));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("429", exception.Message);
    }

    [Fact]
    public void ServerNormalizationRejectsUnsafeUrls()
    {
        Assert.Equal(SocialLimits.DefaultServer, SocialValidation.NormalizeServer("ftp://example.com"));
        Assert.Equal(SocialLimits.DefaultServer, SocialValidation.NormalizeServer("https://user:pass@example.com"));
        Assert.Equal(SocialLimits.DefaultServer, SocialValidation.NormalizeServer("https://example.com/?x=1"));
        Assert.Equal("https://example.com", SocialValidation.NormalizeServer("https://example.com/"));
    }

    [Fact]
    public void DeviceIdIsPreservedWhenSettingsAreSaved()
    {
        var path = Path.Combine(_root, "social.json");
        var store = new SocialSettingsStore(path);
        var settings = store.Load();
        var original = settings.DeviceId;
        settings.ShareLocation = true;
        store.Save(settings);

        Assert.Equal(original, store.Load().DeviceId);
        Assert.True(store.Load().ShareLocation);
    }

    private static SocialSnapshot Snapshot(
        string deviceId,
        string name,
        double latitude,
        double longitude,
        DateTimeOffset now,
        bool sharing = true) =>
        new(deviceId, name, latitude, longitude, 15.0, now.ToUnixTimeMilliseconds(), sharing);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }

        public Uri? LastUri { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public string? LastTitle { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastTitle = request.Headers.TryGetValues("Title", out var values) ? values.FirstOrDefault() : null;
            return Response?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
