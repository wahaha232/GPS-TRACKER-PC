using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Gpx;

public sealed record PendingRecord(
    GpxTrack Track,
    GpxTrackEntry Entry,
    long CreatedAtMs,
    int Attempts,
    long? LastAttemptAtMs,
    string? LastError);

/// <summary>
/// 尚未上傳成功的模擬紀錄。先存成本機 JSON，等網路恢復或定期檢查時再自動補傳；
/// 上傳成功或 GitHub 已有相同紀錄就刪除。最多保留 200 筆，超過時刪最舊的。
/// </summary>
public sealed class PendingRecordStore
{
    public const int DefaultMaxRecords = 200;

    private readonly string _directory;
    private readonly int _maxRecords;

    public PendingRecordStore(string directory, int maxRecords = DefaultMaxRecords)
    {
        _directory = Path.GetFullPath(directory);
        _maxRecords = Math.Max(1, maxRecords);
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<PendingRecord> LoadAll()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<PendingRecord>();
        }

        var records = new List<PendingRecord>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var record = LoadFile(file);
            if (record != null)
            {
                records.Add(record);
            }
        }

        return records
            .OrderBy(record => record.CreatedAtMs)
            .ToList();
    }

    public string Save(GpxBuildResult build, long? createdAtMs = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!build.IsSuccess || build.Track == null || build.Entry == null)
        {
            throw new ArgumentException("只有成功的紀錄可以存入待上傳清單", nameof(build));
        }

        Directory.CreateDirectory(_directory);
        var path = GetPath(build.Entry.TrackId);
        Write(path, new PendingRecordDocument
        {
            Track = build.Track,
            Entry = build.Entry,
            CreatedAtMs = createdAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        TrimToLimit();
        return path;
    }

    /// <summary>記錄一次上傳失敗的錯誤訊息與次數，讓管理畫面可以顯示原因。</summary>
    public void RecordFailure(string trackId, string error)
    {
        try
        {
            var path = GetPath(trackId);
            var document = ReadDocument(path);
            if (document == null)
            {
                return;
            }

            document.Attempts++;
            document.LastAttemptAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            document.LastError = error.Length > 500 ? error[..500] : error;
            Write(path, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            // 記錄失敗不影響上傳流程。
        }
    }

    public bool Remove(string trackId)
    {
        try
        {
            var path = GetPath(trackId);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void TrimToLimit()
    {
        try
        {
            var files = Directory.EnumerateFiles(_directory, "*.json")
                .Select(path => (Path: path, CreatedAt: ReadDocument(path)?.CreatedAtMs ?? long.MaxValue))
                .OrderBy(item => item.CreatedAt)
                .ToList();
            foreach (var file in files.Take(Math.Max(0, files.Count - _maxRecords)))
            {
                try
                {
                    File.Delete(file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PendingRecord? LoadFile(string path)
    {
        try
        {
            var document = ReadDocument(path);
            if (document?.Track == null || document.Entry == null ||
                string.IsNullOrWhiteSpace(document.Entry.TrackId))
            {
                return null;
            }

            return new PendingRecord(
                document.Track,
                document.Entry,
                document.CreatedAtMs,
                document.Attempts,
                document.LastAttemptAtMs,
                document.LastError);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static PendingRecordDocument? ReadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PendingRecordDocument>(
            File.ReadAllText(path),
            GpxJson.Options);
    }

    private static void Write(string path, PendingRecordDocument document)
    {
        var json = JsonSerializer.Serialize(document, GpxJson.Options);
        AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
    }

    private string GetPath(string trackId)
    {
        var safe = new string((trackId ?? string.Empty)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        if (safe.Length == 0 ||
            !string.Equals(safe, trackId, StringComparison.Ordinal))
        {
            throw new ArgumentException("紀錄編號不合法", nameof(trackId));
        }

        return Path.Combine(_directory, safe + ".json");
    }

    private sealed class PendingRecordDocument
    {
        public GpxTrack? Track { get; set; }

        public GpxTrackEntry? Entry { get; set; }

        public long CreatedAtMs { get; set; }

        public int Attempts { get; set; }

        public long? LastAttemptAtMs { get; set; }

        public string? LastError { get; set; }
    }
}
