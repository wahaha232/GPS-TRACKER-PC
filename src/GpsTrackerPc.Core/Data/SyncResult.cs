namespace GpsTrackerPc.Core.Data;

/// <summary>一次資料同步的結果；錯誤訊息不含 Token 或任何機密內容。</summary>
public sealed record SyncResult(
    DateTimeOffset CompletedAt,
    bool Success,
    int DownloadedCount,
    int UpdatedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<string> Errors)
{
    /// <summary>這次同步是否有寫入任何檔案（用來決定要不要重新載入網頁）。</summary>
    public bool HasChanges => DownloadedCount + UpdatedCount > 0;
}
