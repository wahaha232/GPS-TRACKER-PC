namespace GpsTrackerPc.Core.Social;

/// <summary>邀請碼與本機設定共用的格式檢查；所有外部字串都要先經過這裡。</summary>
public static class SocialValidation
{
    public static bool IsSafeId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsSafeTopic(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsSafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > SocialLimits.MaxNameLength)
        {
            return false;
        }

        return value.Trim().All(character => !char.IsControl(character));
    }

    public static string NormalizeName(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > SocialLimits.MaxNameLength)
        {
            trimmed = trimmed[..SocialLimits.MaxNameLength];
        }

        return trimmed;
    }

    public static string NormalizeServer(string? value)
    {
        if (!TryParseServer(value, out var server))
        {
            return SocialLimits.DefaultServer;
        }

        return server;
    }

    public static bool TryParseServer(string? value, out string server)
    {
        server = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        server = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static bool IsValidLatitude(double value) => double.IsFinite(value) && value is >= -90.0 and <= 90.0;

    public static bool IsValidLongitude(double value) => double.IsFinite(value) && value is >= -180.0 and <= 180.0;

    public static bool IsPlausibleTimestamp(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0)
        {
            return false;
        }

        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        return time.Year is >= 2020 and <= 2100;
    }

    public static bool IsValidTarget(SocialTarget? target)
    {
        if (target == null)
        {
            return false;
        }

        if (!Enum.IsDefined(target.Kind) ||
            !IsSafeId(target.Id) ||
            !IsSafeName(target.Name) ||
            !IsSafeTopic(target.Topic) ||
            !TryParseServer(target.Server, out _))
        {
            return false;
        }

        if (!SocialCrypto.TryDecodeKey(target.KeyBase64, out _))
        {
            return false;
        }

        return target.CreatedAtMs >= 0;
    }
}
