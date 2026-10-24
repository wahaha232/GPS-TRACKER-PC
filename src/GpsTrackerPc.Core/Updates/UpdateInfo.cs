namespace GpsTrackerPc.Core.Updates;

/// <summary>GitHub Release「gpstracker-desktop-latest」上的一筆更新資訊。</summary>
public sealed record UpdateInfo(
    string Version,
    string? ReleaseName,
    string DownloadUrl,
    string ChecksumUrl,
    string AssetName,
    long Size,
    string? Notes);

/// <summary>背景下載進度：比例、已下載位元組、總量與目前速度（位元組／秒）。</summary>
public sealed record UpdateDownloadProgress(
    double? Fraction,
    long BytesReceived,
    long? TotalBytes,
    double BytesPerSecond);

public sealed class UpdateException : IOException
{
    public UpdateException(string message) : base(message)
    {
    }
}
