namespace GpsTrackerPc.Core.Gpx;

/// <summary>GPX 匯入前的檔案大小檢查；避免把超大檔案整個讀進記憶體。</summary>
public sealed record GpxImportFileCheck(bool IsAllowed, string? ErrorMessage);

public static class GpxImportFileGuard
{
    public const long MaxFileBytes = 100L * 1024 * 1024;

    public static GpxImportFileCheck Check(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new GpxImportFileCheck(false, "檔案不存在。");
            }

            if (info.Length > MaxFileBytes)
            {
                return new GpxImportFileCheck(
                    false,
                    $"檔案大小超過 100 MB 上限（{info.Length:N0} bytes），已略過。");
            }

            return new GpxImportFileCheck(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GpxImportFileCheck(false, "無法讀取檔案資訊：" + ex.Message);
        }
    }
}
