using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Gpx;

public sealed record PendingRecord(GpxTrack Track, GpxTrackEntry Entry, long CreatedAtMs);

/// <summary>
/// 尚未上傳成功的模擬紀錄。先存成本機 JSON，等網路恢復或定期檢查時再自動補傳；
/// 上傳成功或 GitHub 已有相同紀錄就刪除。
/// </summary>
public sealed class PendingRecordStore
{
    private readonly string _directory;

    public PendingRecordStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
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
        var document = new PendingRecordDocument
        {
            Track = build.Track,
            Entry = build.Entry,
            CreatedAtMs = createdAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var json = JsonSerializer.Serialize(document, GpxJson.Options);
        AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        return path;
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

    private PendingRecord? LoadFile(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<PendingRecordDocument>(
                File.ReadAllText(path),
                GpxJson.Options);
            if (document?.Track == null || document.Entry == null ||
                string.IsNullOrWhiteSpace(document.Entry.TrackId))
            {
                return null;
            }

            return new PendingRecord(document.Track, document.Entry, document.CreatedAtMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
    }
}
