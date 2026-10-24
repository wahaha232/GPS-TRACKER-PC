using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GpsTrackerPc.Core.Social;

/// <summary>位置快照的端對端加密（AES-256-GCM）。中繼服務只看得到密文。</summary>
public static class SocialCrypto
{
    private const byte EnvelopeVersion = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("GPS-TRACKER-PC-SOCIAL-1");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string CreateKeyBase64() =>
        Base64Url.Encode(RandomNumberGenerator.GetBytes(KeyBytes));

    public static string CreateTopic(string keyBase64)
    {
        if (!TryDecodeKey(keyBase64, out var key))
        {
            throw new ArgumentException("分享金鑰格式不正確。", nameof(keyBase64));
        }

        var hash = SHA256.HashData(key);
        return "gtf-" + Base64Url.Encode(hash.AsSpan(0, 16));
    }

    public static SocialTarget CreateTarget(
        SocialTargetKind kind,
        string name,
        string? server,
        DateTimeOffset now)
    {
        var normalizedName = SocialValidation.NormalizeName(name);
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("名稱不能空白。", nameof(name));
        }

        var key = CreateKeyBase64();
        return new SocialTarget(
            kind,
            Guid.NewGuid().ToString("N"),
            normalizedName,
            SocialValidation.NormalizeServer(server),
            CreateTopic(key),
            key,
            now.ToUnixTimeMilliseconds());
    }

    /// <summary>把快照加密成可直接放進 ntfy 訊息的 base64url 字串。</summary>
    public static string Encrypt(SocialSnapshot snapshot, string keyBase64)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryDecodeKey(keyBase64, out var key))
        {
            throw new ArgumentException("分享金鑰格式不正確。", nameof(keyBase64));
        }

        ValidateSnapshot(snapshot);

        var payload = new SnapshotPayload
        {
            Version = 1,
            DeviceId = snapshot.DeviceId,
            DeviceName = SocialValidation.NormalizeName(snapshot.DeviceName),
            Latitude = snapshot.Latitude,
            Longitude = snapshot.Longitude,
            AccuracyMeters = snapshot.AccuracyMeters,
            SentAtMs = snapshot.SentAtMs,
            Sharing = snapshot.Sharing,
        };

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

        var envelope = new byte[1 + NonceBytes + TagBytes + ciphertext.Length];
        envelope[0] = EnvelopeVersion;
        nonce.CopyTo(envelope, 1);
        tag.CopyTo(envelope, 1 + NonceBytes);
        ciphertext.CopyTo(envelope, 1 + NonceBytes + TagBytes);
        return Base64Url.Encode(envelope);
    }

    /// <summary>解密失敗（金鑰不符、內容損壞、遭到竄改）一律回傳 null，不讓單一壞訊息中斷整批。</summary>
    public static SocialSnapshot? TryDecrypt(string? envelope, string keyBase64)
    {
        if (string.IsNullOrWhiteSpace(envelope) || !TryDecodeKey(keyBase64, out var key))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Base64Url.Decode(envelope.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length <= 1 + NonceBytes + TagBytes || bytes[0] != EnvelopeVersion)
        {
            return null;
        }

        var nonce = bytes.AsSpan(1, NonceBytes);
        var tag = bytes.AsSpan(1 + NonceBytes, TagBytes);
        var ciphertext = bytes.AsSpan(1 + NonceBytes + TagBytes);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
        }
        catch (CryptographicException)
        {
            return null;
        }

        SnapshotPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SnapshotPayload>(plaintext, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload == null || payload.Version != 1)
        {
            return null;
        }

        var snapshot = new SocialSnapshot(
            payload.DeviceId ?? string.Empty,
            SocialValidation.NormalizeName(payload.DeviceName),
            payload.Latitude,
            payload.Longitude,
            payload.AccuracyMeters,
            payload.SentAtMs,
            payload.Sharing ?? true);

        return IsValidSnapshot(snapshot) ? snapshot : null;
    }

    public static bool TryDecodeKey(string? keyBase64, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            return false;
        }

        try
        {
            var decoded = Base64Url.Decode(keyBase64.Trim());
            if (decoded.Length != KeyBytes)
            {
                return false;
            }

            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateSnapshot(SocialSnapshot snapshot)
    {
        if (!SocialValidation.IsSafeId(snapshot.DeviceId) || snapshot.DeviceId.Length < 8)
        {
            throw new ArgumentException("裝置識別碼不合法。", nameof(snapshot));
        }

        if (!SocialValidation.IsSafeName(snapshot.DeviceName))
        {
            throw new ArgumentException("裝置名稱不合法。", nameof(snapshot));
        }

        if (!SocialValidation.IsValidLatitude(snapshot.Latitude) ||
            !SocialValidation.IsValidLongitude(snapshot.Longitude))
        {
            throw new ArgumentException("座標不合法。", nameof(snapshot));
        }

        if (snapshot.AccuracyMeters is { } accuracy && (!double.IsFinite(accuracy) || accuracy < 0 || accuracy > 100_000))
        {
            throw new ArgumentException("定位精度不合法。", nameof(snapshot));
        }

        if (!SocialValidation.IsPlausibleTimestamp(snapshot.SentAtMs))
        {
            throw new ArgumentException("時間戳記不合法。", nameof(snapshot));
        }
    }

    private static bool IsValidSnapshot(SocialSnapshot snapshot) =>
        SocialValidation.IsSafeId(snapshot.DeviceId) &&
        snapshot.DeviceId.Length >= 8 &&
        SocialValidation.IsSafeName(snapshot.DeviceName) &&
        SocialValidation.IsValidLatitude(snapshot.Latitude) &&
        SocialValidation.IsValidLongitude(snapshot.Longitude) &&
        (snapshot.AccuracyMeters is null ||
         (double.IsFinite(snapshot.AccuracyMeters.Value) && snapshot.AccuracyMeters is >= 0 and <= 100_000)) &&
        SocialValidation.IsPlausibleTimestamp(snapshot.SentAtMs);

    private sealed class SnapshotPayload
    {
        public int Version { get; set; }

        public string? DeviceId { get; set; }

        public string? DeviceName { get; set; }

        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public double? AccuracyMeters { get; set; }

        public long SentAtMs { get; set; }

        public bool? Sharing { get; set; } = true;
    }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding == 2)
        {
            normalized += "==";
        }
        else if (padding == 3)
        {
            normalized += "=";
        }
        else if (padding == 1)
        {
            throw new FormatException("base64url 長度不正確。");
        }

        return Convert.FromBase64String(normalized);
    }
}
