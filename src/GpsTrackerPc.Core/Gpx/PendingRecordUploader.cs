using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Gpx;

public sealed record PendingUploadReport(
    int Uploaded,
    int Duplicates,
    int Failed,
    int Processed,
    string? LastError)
{
    public int Removed => Uploaded + Duplicates;
}

/// <summary>把待上傳紀錄逐一上傳；失敗會把原因寫回本機，讓管理畫面顯示。</summary>
public sealed class PendingRecordUploader
{
    private readonly PendingRecordStore _store;
    private readonly IRecordUploader _uploader;

    public PendingRecordUploader(PendingRecordStore store, IRecordUploader uploader)
    {
        _store = store;
        _uploader = uploader;
    }

    public async Task<PendingUploadReport> UploadAllAsync(
        int maxUploads,
        CancellationToken cancellationToken = default)
    {
        var records = _store.LoadAll();
        var uploaded = 0;
        var duplicates = 0;
        var failed = 0;
        var processed = 0;
        string? lastError = null;

        foreach (var record in records)
        {
            if (cancellationToken.IsCancellationRequested || processed >= maxUploads)
            {
                break;
            }

            processed++;
            try
            {
                var result = await _uploader.UploadAsync(
                    new GpxBuildResult(record.Track, record.Entry, null, null),
                    cancellationToken).ConfigureAwait(false);
                if (result.Status == UploadStatus.Uploaded)
                {
                    _store.Remove(record.Entry.TrackId);
                    uploaded++;
                }
                else if (result.Status == UploadStatus.Duplicate)
                {
                    _store.Remove(record.Entry.TrackId);
                    duplicates++;
                }
                else
                {
                    failed++;
                    lastError = result.ErrorMessage ?? "上傳失敗";
                    _store.RecordFailure(record.Entry.TrackId, lastError);
                    break; // 通常是 Token／網路問題，先停下來等下一輪。
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                failed++;
                lastError = ex.Message;
                _store.RecordFailure(record.Entry.TrackId, lastError);
                break;
            }
        }

        return new PendingUploadReport(uploaded, duplicates, failed, processed, lastError);
    }
}

/// <summary>每日自動補傳次數，避免一次把大量紀錄公開上傳。</summary>
public sealed class UploadQuotaStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    public UploadQuotaStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public int GetTodayCount(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return 0;
            }

            var state = JsonSerializer.Deserialize<QuotaDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return state != null && state.Date == Today(now) ? Math.Max(0, state.Count) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    public int Remaining(int dailyLimit, DateTimeOffset now) =>
        Math.Max(0, dailyLimit - GetTodayCount(now));

    public int Consume(int count, DateTimeOffset now)
    {
        if (count <= 0)
        {
            return GetTodayCount(now);
        }

        var today = Today(now);
        var next = GetTodayCount(now) + count;
        try
        {
            var json = JsonSerializer.Serialize(
                new QuotaDocument { Date = today, Count = next },
                SerializerOptions);
            AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 寫不進去時仍回傳本次預估，下一輪會重新讀檔。
        }

        return next;
    }

    private static string Today(DateTimeOffset now) => now.ToLocalTime().ToString("yyyy-MM-dd");

    private sealed class QuotaDocument
    {
        public string Date { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}
