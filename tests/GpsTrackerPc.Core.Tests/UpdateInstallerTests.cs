using GpsTrackerPc.Core.Updates;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class UpdateInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _currentExe;
    private readonly string _downloaded;

    public UpdateInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _currentExe = Path.Combine(_root, "GpsTrackerPc.exe");
        _downloaded = Path.Combine(_root, "update.exe");
        File.WriteAllText(_currentExe, "old-version");
        File.WriteAllText(_downloaded, "new-version");
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
    public void ApplyReplacesExeAndCleanupRemovesOldFile()
    {
        var restarted = false;

        var result = UpdateInstaller.Apply(_downloaded, _currentExe, () => restarted = true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(restarted);
        Assert.Equal("new-version", File.ReadAllText(_currentExe));
        Assert.True(File.Exists(_currentExe + ".old"));
        Assert.Equal("old-version", File.ReadAllText(_currentExe + ".old"));

        UpdateInstaller.CleanupOldVersion(_currentExe);
        Assert.True(File.Exists(_currentExe + ".old"));

        File.SetLastWriteTimeUtc(_currentExe + ".old", DateTime.UtcNow.AddDays(-8));
        UpdateInstaller.CleanupOldVersion(_currentExe);
        Assert.False(File.Exists(_currentExe + ".old"));
    }

    [Fact]
    public void OldFileKeepsSevenDaysFromInstallEvenWhenCurrentExeIsOld()
    {
        // 目前的 EXE 是 30 天前安裝的；換下來的 .old 要從這次安裝起算 7 天，不能沿用舊時間。
        File.SetLastWriteTimeUtc(_currentExe, DateTime.UtcNow.AddDays(-30));

        var result = UpdateInstaller.Apply(_downloaded, _currentExe, () => { });
        Assert.True(result.Success, result.ErrorMessage);

        UpdateInstaller.CleanupOldVersion(_currentExe);

        Assert.True(File.Exists(_currentExe + ".old"));
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(_currentExe + ".old") < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void NonExeDownloadIsRejectedWithoutChangingAnything()
    {
        var zipPath = Path.Combine(_root, "update.zip");
        File.WriteAllText(zipPath, "zip-content");
        var restarted = false;

        var result = UpdateInstaller.Apply(zipPath, _currentExe, () => restarted = true);

        Assert.False(result.Success);
        Assert.Contains("EXE", result.ErrorMessage);
        Assert.False(restarted);
        Assert.Equal("old-version", File.ReadAllText(_currentExe));
        Assert.False(File.Exists(_currentExe + ".old"));
    }

    [Fact]
    public void RestartFailureRestoresTheOldExe()
    {
        var result = UpdateInstaller.Apply(
            _downloaded,
            _currentExe,
            () => throw new IOException("restart failed"));

        Assert.False(result.Success);
        Assert.Contains("重新啟動", result.ErrorMessage);
        Assert.Equal("old-version", File.ReadAllText(_currentExe));
        Assert.False(File.Exists(_currentExe + ".old"));
    }

    [Fact]
    public void CopyFailureRestoresTheOldExe()
    {
        // 鎖住下載檔，讓 File.Copy 在備份完成後失敗。
        using var lockHandle = new FileStream(_downloaded, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = UpdateInstaller.Apply(_downloaded, _currentExe, () => { });

        Assert.False(result.Success);
        Assert.Contains("寫入新版本", result.ErrorMessage);
        Assert.Equal("old-version", File.ReadAllText(_currentExe));
        Assert.False(File.Exists(_currentExe + ".old"));
    }

    [Fact]
    public void MissingFilesAreRejectedWithoutChangingAnything()
    {
        var missing = UpdateInstaller.Apply(Path.Combine(_root, "missing.exe"), _currentExe, () => { });
        Assert.False(missing.Success);
        Assert.Equal("old-version", File.ReadAllText(_currentExe));

        var missingCurrent = UpdateInstaller.Apply(_downloaded, Path.Combine(_root, "missing.exe"), () => { });
        Assert.False(missingCurrent.Success);
    }

    [Fact]
    public void CleanupMissingOldFileDoesNotThrow()
    {
        UpdateInstaller.CleanupOldVersion(_currentExe);
        Assert.Equal("old-version", File.ReadAllText(_currentExe));
    }
}
