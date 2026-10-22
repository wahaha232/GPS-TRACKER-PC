using System.Globalization;
using System.Net;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Data;

/// <summary>
/// 把公開的 gpstracker-web/data/ 同步到本機資料夾。
/// 只讀取公開資料，不需要 GitHub Token；下載失敗時保留本機舊檔，
/// 每個檔案都先寫暫存檔再改名覆蓋。
/// </summary>
public sealed class DataSyncService
{
    public static readonly Uri DefaultBaseUri =
        new("https://raw.githubusercontent.com/wahaha232/ANDROID-APP-DEV/main/gpstracker-web/data/");

    private const long MaxFileBytes = 50L * 1024 * 1024;
    private const string ManifestFileName = "manifest.json";
    private const string UserAgent = "GpsTrackerPc/1.0";

    // 這些檔案不是 manifest 列出的紀錄，但網頁會選用；不存在時不算失敗。
    private static readonly string[] OptionalFileNames =
    {
        "meta.json",
        "metro.json",
        "steps.json",
        "voyager.json",
    };

    private readonly HttpClient _httpClient;
    private readonly string _dataDirectory;
    private readonly string _cacheFilePath;
    private readonly Uri _baseUri;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SyncFileCache _cache;

    public DataSyncService(
        HttpClient httpClient,
        string dataDirectory,
        string cacheFilePath,
        Uri? baseUri = null,
        Func<DateTimeOffset>? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _cacheFilePath = Path.GetFullPath(cacheFilePath);
        _baseUri = NormalizeBaseUri(baseUri ?? DefaultBaseUri);
        _clock = clock ?? (() => DateTimeOffset.Now);

        Directory.CreateDirectory(_dataDirectory);
        var cacheDirectory = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
        }

        _cache = SyncFileCache.Load(_cacheFilePath);
    }

    public string DataDirectory => _dataDirectory;

    /// <summary>同步一次；同時間只允許一個同步執行。</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Failure("同步正在進行中");
        }

        try
        {
            return await SyncCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 任何未預期錯誤都不清空本機資料；已經完成的檔案維持新內容。
            return Failure(ex.Message);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<SyncResult> SyncCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);

        var downloaded = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        // 一定要先取得 manifest，才能知道有哪些紀錄檔。
        var manifest = await TryFetchAsync(ManifestFileName, optional: false, needBytes: true, cancellationToken)
            .ConfigureAwait(false);
        if (manifest.Status is not (FetchStatus.Downloaded or FetchStatus.Updated or FetchStatus.NotModified))
        {
            return Failure($"{ManifestFileName}：{manifest.Error ?? "下載失敗"}");
        }

        List<string> trackFiles;
        try
        {
            trackFiles = ParseTrackFiles(manifest.Bytes!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Failure($"{ManifestFileName} 格式錯誤：{ex.Message}");
        }

        if (manifest.Status != FetchStatus.NotModified)
        {
            AtomicFile.WriteAllBytes(GetTargetPath(ManifestFileName), manifest.Bytes!);
            Remember(ManifestFileName, manifest);
            if (manifest.Status == FetchStatus.Downloaded)
            {
                downloaded++;
            }
            else
            {
                updated++;
            }
        }

        foreach (var fileName in trackFiles)
        {
            var outcome = await TryFetchAsync(fileName, optional: false, needBytes: false, cancellationToken)
                .ConfigureAwait(false);
            Apply(outcome, fileName);
        }

        foreach (var fileName in OptionalFileNames)
        {
            var outcome = await TryFetchAsync(fileName, optional: true, needBytes: false, cancellationToken)
                .ConfigureAwait(false);
            Apply(outcome, fileName);
        }

        try
        {
            _cache.Save(_cacheFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 快取只是最佳化；存不進去不影響這次已同步的資料。
        }

        return new SyncResult(
            _clock(),
            failed == 0,
            downloaded,
            updated,
            skipped,
            failed,
            errors);

        void Apply(FetchOutcome outcome, string fileName)
        {
            switch (outcome.Status)
            {
                case FetchStatus.Downloaded:
                case FetchStatus.Updated:
                    AtomicFile.WriteAllBytes(GetTargetPath(fileName), outcome.Bytes!);
                    Remember(fileName, outcome);
                    if (outcome.Status == FetchStatus.Downloaded)
                    {
                        downloaded++;
                    }
                    else
                    {
                        updated++;
                    }

                    break;
                case FetchStatus.NotModified:
                case FetchStatus.Skipped:
                    skipped++;
                    break;
                default:
                    failed++;
                    errors.Add($"{fileName}：{outcome.Error ?? "下載失敗"}");
                    break;
            }
        }
    }

    private async Task<FetchOutcome> TryFetchAsync(
        string fileName,
        bool optional,
        bool needBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await FetchAsync(fileName, optional, needBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return new FetchOutcome(FetchStatus.Failed, null, null, null, ex.Message);
        }
    }

    private async Task<FetchOutcome> FetchAsync(
        string fileName,
        bool optional,
        bool needBytes,
        CancellationToken cancellationToken)
    {
        var targetPath = GetTargetPath(fileName);
        var existsLocally = File.Exists(targetPath);
        var cached = _cache.Get(fileName);

        // 第一次帶條件式標頭；若伺服器回 304 但本機其實沒有檔案，再無條件下載一次。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, Uri.EscapeDataString(fileName)));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            if (attempt == 0 && cached != null)
            {
                if (!string.IsNullOrEmpty(cached.ETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
                }

                if (!string.IsNullOrEmpty(cached.LastModified))
                {
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified);
                }
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (!existsLocally)
                {
                    continue;
                }

                return new FetchOutcome(
                    FetchStatus.NotModified,
                    needBytes ? await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false) : null,
                    cached?.ETag,
                    cached?.LastModified,
                    null);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return optional
                    ? new FetchOutcome(FetchStatus.Skipped, null, null, null, null)
                    : new FetchOutcome(FetchStatus.Failed, null, null, null, "HTTP 404");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new FetchOutcome(
                    FetchStatus.Failed,
                    null,
                    null,
                    null,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            var bytes = await ReadBoundedAsync(response, fileName, cancellationToken).ConfigureAwait(false);
            return new FetchOutcome(
                existsLocally ? FetchStatus.Updated : FetchStatus.Downloaded,
                bytes,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture),
                null);
        }

        return new FetchOutcome(
            FetchStatus.Failed,
            null,
            null,
            null,
            "伺服器回傳 304 但本機沒有對應檔案");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxFileBytes)
        {
            throw new IOException($"{fileName} 超過 {MaxFileBytes} bytes 上限");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxFileBytes)
            {
                throw new IOException($"{fileName} 超過 {MaxFileBytes} bytes 上限");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private void Remember(string fileName, FetchOutcome outcome) =>
        _cache.Remember(fileName, outcome.ETag, outcome.LastModified);

    private string GetTargetPath(string fileName) => Path.Combine(_dataDirectory, fileName);

    private SyncResult Failure(string message) =>
        new(_clock(), false, 0, 0, 0, 1, new[] { message });

    private static Uri NormalizeBaseUri(Uri baseUri)
    {
        var text = baseUri.AbsoluteUri;
        return text.EndsWith('/') ? baseUri : new Uri(text + "/");
    }

    /// <summary>從 manifest 取出紀錄檔名；不安全的檔名直接忽略，避免路徑跳脫。</summary>
    private static List<string> ParseTrackFiles(byte[] manifestBytes)
    {
        using var document = JsonDocument.Parse(manifestBytes);
        if (!document.RootElement.TryGetProperty("tracks", out var tracks) ||
            tracks.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("缺少 tracks 陣列");
        }

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fileName = ReadString(track, "file");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                var trackId = ReadString(track, "trackId");
                if (!string.IsNullOrWhiteSpace(trackId))
                {
                    fileName = trackId + ".json";
                }
            }

            if (string.IsNullOrWhiteSpace(fileName) || !IsSafeFileName(fileName))
            {
                continue;
            }

            if (seen.Add(fileName))
            {
                files.Add(fileName);
            }
        }

        return files;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200)
        {
            return false;
        }

        if (fileName is "." or "..")
        {
            return false;
        }

        if (fileName != Path.GetFileName(fileName))
        {
            return false;
        }

        if (fileName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private enum FetchStatus
    {
        Downloaded,
        Updated,
        NotModified,
        Skipped,
        Failed,
    }

    private sealed record FetchOutcome(
        FetchStatus Status,
        byte[]? Bytes,
        string? ETag,
        string? LastModified,
        string? Error);
}
