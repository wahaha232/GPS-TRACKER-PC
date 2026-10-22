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

public sealed class UpdateException : IOException
{
    public UpdateException(string message) : base(message)
    {
    }
}
