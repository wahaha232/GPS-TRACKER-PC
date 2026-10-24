using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Gpx;

public sealed record GpxFileHistoryEntry(string FilePath, string DisplayName, long UsedAt);

/// <summary>
/// 記住用過的 GPX 檔路徑，讓模擬頁可以重複載入同一批檔案，不用每次重新選檔。
/// 只存路徑（不複製檔案內容），檔案被移動或刪除時由呼叫端提示。
/// </summary>
public sealed class GpxFileHistoryStore
{
    public const int MaxEntries = 30;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly Func<DateTimeOffset> _clock;

    public GpxFileHistoryStore(string filePath, Func<DateTimeOffset>? clock = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string FilePath => _filePath;

    /// <summary>最近的排在前面；只回傳仍然有效的項目。</summary>
    public IReadOnlyList<GpxFileHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<GpxFileHistoryEntry>();
            }

            var document = JsonSerializer.Deserialize<HistoryDocument>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return (document?.Files ?? new List<GpxFileHistoryEntry>())
                .Where(entry => entry is not null && IsValid(entry))
                .OrderByDescending(entry => entry.UsedAt)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Array.Empty<GpxFileHistoryEntry>();
        }
    }

    /// <summary>記錄一個檔案；同一個路徑只留最新一筆並移到最前面。</summary>
    public GpxFileHistoryEntry Remember(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("檔案路徑不能是空白", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var entries = Load().ToList();
        entries.RemoveAll(entry => string.Equals(entry.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        var remembered = new GpxFileHistoryEntry(
            FilePath: fullPath,
            DisplayName: MakeDisplayName(fullPath),
            UsedAt: _clock().ToUnixTimeMilliseconds());
        entries.Insert(0, remembered);
        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        Save(entries);
        return remembered;
    }

    public bool Remove(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var entries = Load().ToList();
        var removed = entries.RemoveAll(
            entry => string.Equals(entry.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save(entries);
        }

        return removed;
    }

    private void Save(IReadOnlyList<GpxFileHistoryEntry> entries)
    {
        var document = new HistoryDocument { Files = entries.ToList() };
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    private static string MakeDisplayName(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        var folder = Path.GetFileName(Path.GetDirectoryName(fullPath));
        return string.IsNullOrEmpty(folder) ? name : $"{name}（{folder}）";
    }

    private static bool IsValid(GpxFileHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.FilePath) &&
        !string.IsNullOrWhiteSpace(entry.DisplayName);

    private sealed class HistoryDocument
    {
        public List<GpxFileHistoryEntry> Files { get; set; } = new();
    }
}
