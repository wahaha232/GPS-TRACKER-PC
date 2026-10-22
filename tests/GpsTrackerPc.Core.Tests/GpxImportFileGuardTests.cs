using GpsTrackerPc.Core.Gpx;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class GpxImportFileGuardTests : IDisposable
{
    private readonly string _root;

    public GpxImportFileGuardTests()
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
    public void SmallFileIsAllowed()
    {
        var path = Path.Combine(_root, "small.gpx");
        File.WriteAllText(path, "<gpx></gpx>");

        var result = GpxImportFileGuard.Check(path);

        Assert.True(result.IsAllowed);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void FileExactlyAtLimitIsAllowed()
    {
        var path = Path.Combine(_root, "limit.gpx");
        using (var stream = File.Create(path))
        {
            stream.SetLength(GpxImportFileGuard.MaxFileBytes);
        }

        Assert.True(GpxImportFileGuard.Check(path).IsAllowed);
    }

    [Fact]
    public void FileOverLimitIsSkippedWithReason()
    {
        var path = Path.Combine(_root, "too-large.gpx");
        using (var stream = File.Create(path))
        {
            stream.SetLength(GpxImportFileGuard.MaxFileBytes + 1);
        }

        var result = GpxImportFileGuard.Check(path);

        Assert.False(result.IsAllowed);
        Assert.Contains("100 MB", result.ErrorMessage);
        Assert.Contains("略過", result.ErrorMessage);
    }

    [Fact]
    public void MissingFileIsRejected()
    {
        var result = GpxImportFileGuard.Check(Path.Combine(_root, "missing.gpx"));

        Assert.False(result.IsAllowed);
        Assert.Contains("不存在", result.ErrorMessage);
    }
}
