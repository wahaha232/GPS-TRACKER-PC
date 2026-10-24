using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GpsTrackerPc.Core.Social;

/// <summary>
/// 好友／群組邀請碼：<c>GTF1-</c> 加上 base64url(JSON)。
/// 內容包含中繼伺服器、主題與金鑰，等於分享權杖，請只傳給信任的人。
/// </summary>
public static class SocialInviteCode
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Encode(SocialTarget target, string displayName)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!SocialValidation.IsValidTarget(target))
        {
            throw new ArgumentException("分享連線資料不完整。", nameof(target));
        }

        var name = target.Kind == SocialTargetKind.Friend
            ? SocialValidation.NormalizeName(displayName)
            : target.Name;
        if (name.Length == 0)
        {
            name = target.Name;
        }

        var payload = new InvitePayload
        {
            Version = 1,
            Kind = target.Kind == SocialTargetKind.Friend ? "friend" : "group",
            Id = target.Id,
            Name = name,
            Server = target.Server,
            Topic = target.Topic,
            Key = target.KeyBase64,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        return SocialLimits.InvitePrefix + Base64Url.Encode(json);
    }

    public static SocialTarget Decode(string? code, DateTimeOffset now)
    {
        var normalized = (code ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new SocialException(null, "請貼上邀請碼。");
        }

        if (normalized.Length > SocialLimits.MaxInviteLength)
        {
            throw new SocialException(null, "邀請碼太長，可能貼到了其他內容。");
        }

        if (!normalized.StartsWith(SocialLimits.InvitePrefix, StringComparison.Ordinal))
        {
            throw new SocialException(null, "邀請碼格式不正確（開頭應為 GTF1-）。");
        }

        InvitePayload? payload;
        try
        {
            var bytes = Base64Url.Decode(normalized[SocialLimits.InvitePrefix.Length..]);
            payload = JsonSerializer.Deserialize<InvitePayload>(bytes, SerializerOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SocialException(null, "邀請碼內容損壞，請重新取得。");
        }

        if (payload == null || payload.Version != 1)
        {
            throw new SocialException(null, "不支援這個版本的邀請碼。");
        }

        var kind = payload.Kind switch
        {
            "friend" => SocialTargetKind.Friend,
            "group" => SocialTargetKind.Group,
            _ => throw new SocialException(null, "邀請碼類型不正確。"),
        };

        if (!SocialValidation.IsSafeId(payload.Id) ||
            !SocialValidation.IsSafeName(payload.Name) ||
            !SocialValidation.IsSafeTopic(payload.Topic) ||
            !SocialValidation.TryParseServer(payload.Server, out var server) ||
            !SocialCrypto.TryDecodeKey(payload.Key, out _))
        {
            throw new SocialException(null, "邀請碼缺少必要資料，請重新取得。");
        }

        return new SocialTarget(
            kind,
            payload.Id!,
            SocialValidation.NormalizeName(payload.Name),
            server,
            payload.Topic!,
            payload.Key!,
            now.ToUnixTimeMilliseconds());
    }

    private sealed class InvitePayload
    {
        public int Version { get; set; }

        public string? Kind { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Server { get; set; }

        public string? Topic { get; set; }

        public string? Key { get; set; }
    }
}
