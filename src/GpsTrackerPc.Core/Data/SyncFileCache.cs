using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Data;

/// <summary>每個檔案的 ETag／Last-Modified，讓之後的同步可以用條件式請求跳過沒變的檔案。</summary>
internal sealed class SyncFileCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public Dictionary<string, SyncCacheEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static SyncFileCache Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SyncFileCache();
            }

            var loaded = JsonSerializer.Deserialize<SyncFileCache>(File.ReadAllText(path));
            var cache = new SyncFileCache();
            if (loaded?.Files != null)
            {
                foreach (var pair in loaded.Files)
                {
                    cache.Files[pair.Key] = pair.Value;
                }
            }

            return cache;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 快取壞掉只會讓下一次多下載幾個檔案，不影響資料正確性。
            return new SyncFileCache();
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        AtomicFile.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
    }

    public SyncCacheEntry? Get(string fileName) =>
        Files.TryGetValue(fileName, out var entry) ? entry : null;

    public void Remember(string fileName, string? eTag, string? lastModified)
    {
        Files[fileName] = new SyncCacheEntry
        {
            ETag = eTag,
            LastModified = lastModified,
        };
    }
}

internal sealed class SyncCacheEntry
{
    public string? ETag { get; set; }

    public string? LastModified { get; set; }
}
