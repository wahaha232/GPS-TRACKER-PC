using GpsTrackerPc.Core.Updates;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class UpdateVersionTests
{
    [Fact]
    public void VersionOfFindsThreePartVersion()
    {
        Assert.Equal("1.2.3", UpdateVersion.VersionOf("GPS Tracker PC v1.2.3"));
        Assert.Equal("10.0.4", UpdateVersion.VersionOf("10.0.4"));
        Assert.Null(UpdateVersion.VersionOf("沒有版本號"));
        Assert.Null(UpdateVersion.VersionOf(null));
    }

    [Fact]
    public void IsNewerComparesEachPart()
    {
        Assert.True(UpdateVersion.IsNewer("1.2.3", "1.2.4"));
        Assert.True(UpdateVersion.IsNewer("1.2.9", "1.3.0"));
        Assert.True(UpdateVersion.IsNewer("1.9.9", "2.0.0"));
        // 次版本進到兩位數要用數字比，不能用字串比（"0.10.0" 字串上小於 "0.9.2"）。
        Assert.True(UpdateVersion.IsNewer("0.9.2", "0.10.0"));
        Assert.False(UpdateVersion.IsNewer("0.10.0", "0.9.2"));
        Assert.False(UpdateVersion.IsNewer("1.2.3", "1.2.3"));
        Assert.False(UpdateVersion.IsNewer("2.0.0", "1.9.9"));
        Assert.False(UpdateVersion.IsNewer("bad", "1.0.0"));
        Assert.False(UpdateVersion.IsNewer("1.0.0", "bad"));
    }

    [Fact]
    public void SafeDownloadUrlAcceptsExeAndChecksumOnGithub()
    {
        Assert.True(UpdateVersion.IsSafeDownloadUrl(
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest/GpsTrackerPc.exe"));
        Assert.True(UpdateVersion.IsSafeDownloadUrl(
            "https://GITHUB.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest/GpsTrackerPc.exe.sha256"));
    }

    [Fact]
    public void SafeDownloadUrlRejectsTraversalEncodedDotsAndUnsafeParts()
    {
        var unsafeUrls = new[]
        {
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/../../../other/repo/releases/download/x/evil.exe",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/%2e%2e/evil.exe",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/%2E%2E/evil.exe",
            "http://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/a.exe",
            "https://github.com.evil.com/wahaha232/GPS-TRACKER-PC/releases/download/x/a.exe",
            "https://user@github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/a.exe",
            "https://github.com:8443/wahaha232/GPS-TRACKER-PC/releases/download/x/a.exe",
            "https://github.com/wahaha232/GPS-TRACKER-PC-evil/releases/download/x/a.exe",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/a/b.exe",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x/",
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download//a.exe",
            "https://github.com/wahaha232/OTHER/releases/download/x/a.exe",
            "",
            "not a url",
        };

        Assert.All(unsafeUrls, url => Assert.False(UpdateVersion.IsSafeDownloadUrl(url)));
        Assert.False(UpdateVersion.IsSafeDownloadUrl(null));
    }
}
