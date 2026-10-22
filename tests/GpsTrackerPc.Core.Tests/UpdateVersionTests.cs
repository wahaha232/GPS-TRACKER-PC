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
        Assert.False(UpdateVersion.IsNewer("1.2.3", "1.2.3"));
        Assert.False(UpdateVersion.IsNewer("2.0.0", "1.9.9"));
        Assert.False(UpdateVersion.IsNewer("bad", "1.0.0"));
        Assert.False(UpdateVersion.IsNewer("1.0.0", "bad"));
    }

    [Fact]
    public void OnlyGitHubReleaseDownloadUrlsAreAllowed()
    {
        Assert.True(UpdateVersion.IsSafeDownloadUrl(
            "https://github.com/wahaha232/GPS-TRACKER-PC/releases/download/gpstracker-desktop-latest/GpsTrackerPc.zip"));
        Assert.False(UpdateVersion.IsSafeDownloadUrl(
            "https://evil.example/GpsTrackerPc.zip"));
        Assert.False(UpdateVersion.IsSafeDownloadUrl(
            "http://github.com/wahaha232/GPS-TRACKER-PC/releases/download/x.zip"));
        Assert.False(UpdateVersion.IsSafeDownloadUrl(
            "https://github.com/wahaha232/OTHER/releases/download/x.zip"));
        Assert.False(UpdateVersion.IsSafeDownloadUrl(null));
    }
}
