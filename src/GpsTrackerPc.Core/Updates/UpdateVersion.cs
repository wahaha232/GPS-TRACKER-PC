using System.Text.RegularExpressions;

namespace GpsTrackerPc.Core.Updates;

/// <summary>版本比較與下載網址檢查，規則和 Android 版 UpdateLogic 相同。</summary>
public static class UpdateVersion
{
    public const string Repo = "wahaha232/GPS-TRACKER-PC";

    public const string DownloadPrefix =
        "https://github.com/" + Repo + "/releases/download/";

    private const string DownloadPathPrefix =
        "/wahaha232/GPS-TRACKER-PC/releases/download/";

    private static readonly Regex VersionPattern = new(
        @"(\d+)\.(\d+)\.(\d+)",
        RegexOptions.Compiled);

    /// <summary>從「GPS Tracker PC 1.2.3」或「1.2.3」取出 1.2.3；找不到回傳 null。</summary>
    public static string? VersionOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = VersionPattern.Match(text);
        return match.Success ? match.Value : null;
    }

    public static bool IsNewer(string currentVersion, string remoteVersion)
    {
        var current = VersionOf(currentVersion);
        var remote = VersionOf(remoteVersion);
        if (current == null || remote == null)
        {
            return false;
        }

        var currentParts = current.Split('.');
        var remoteParts = remote.Split('.');
        for (var index = 0; index < 3; index++)
        {
            if (!long.TryParse(currentParts[index], out var currentPart) ||
                !long.TryParse(remoteParts[index], out var remotePart))
            {
                return false;
            }

            if (remotePart != currentPart)
            {
                return remotePart > currentPart;
            }
        }

        return false;
    }

    /// <summary>
    /// 只接受 GitHub Releases 的單一標籤／檔名路徑。
    /// 先看原始字串阻擋 ../ 與編碼點，再用 Uri 正規化後的 Host／Path 檢查，
    /// 不依賴 Uri 對路徑的合併結果。
    /// </summary>
    public static bool IsSafeDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // 不依賴 Uri 正規化：原始字串出現任何點點或編碼點就直接拒絕。
        if (url.Contains("..", StringComparison.Ordinal) ||
            url.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            url.Contains('\\'))
        {
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort)
        {
            return false;
        }

        var path = uri.AbsolutePath;
        if (!path.StartsWith(DownloadPathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remaining = path[DownloadPathPrefix.Length..];
        var segments = remaining.Split('/');
        if (segments.Length != 2)
        {
            return false;
        }

        return segments.All(segment =>
            segment.Length > 0 &&
            segment != "." &&
            segment != "..");
    }
}
