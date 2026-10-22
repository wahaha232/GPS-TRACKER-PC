using System.Text.RegularExpressions;

namespace GpsTrackerPc.Core.Updates;

/// <summary>版本比較與下載網址檢查，規則和 Android 版 UpdateLogic 相同。</summary>
public static class UpdateVersion
{
    public const string Repo = "wahaha232/GPS-TRACKER-PC";

    public const string DownloadPrefix =
        "https://github.com/" + Repo + "/releases/download/";

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

    public static bool IsSafeDownloadUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal);
}
