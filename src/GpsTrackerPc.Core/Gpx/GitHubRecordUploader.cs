using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GpsTrackerPc.Core.Security;

namespace GpsTrackerPc.Core.Gpx;

public enum UploadStatus
{
    Uploaded,
    Duplicate,
    Failed,
}

public sealed record UploadResult(UploadStatus Status, string? TrackId = null, string? ErrorMessage = null)
{
    public static UploadResult Uploaded(string trackId) => new(UploadStatus.Uploaded, trackId);

    public static UploadResult Duplicate(string trackId) => new(UploadStatus.Duplicate, trackId);

    public static UploadResult Failed(string message) => new(UploadStatus.Failed, ErrorMessage: message);
}

/// <summary>批次匯入用的上傳介面；方便用假的實作測試「失敗不中斷整批」。</summary>
public interface IRecordUploader
{
    Task<UploadResult> UploadAsync(GpxBuildResult build, CancellationToken cancellationToken = default);
}

public sealed class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// 用 GitHub Contents API 上傳 GPX 匯入的紀錄：
/// 先寫 data/&lt;trackId&gt;.json，再更新 manifest.json；GPX 有標題時再更新 meta.json。
/// 遇到 409／422（同時有人寫入）會重新讀最新內容、合併後重試，最多 3 次。
/// </summary>
public sealed class GitHubRecordUploader : IRecordUploader
{
    private const int MaxAttempts = 3;
    private const string UserAgent = "GpsTrackerPc/1.0";

    private readonly HttpClient _httpClient;
    private readonly TokenStore _tokenStore;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _branch;
    private readonly string _dataPath;
    private readonly TimeSpan _retryDelay;

    public GitHubRecordUploader(
        HttpClient httpClient,
        TokenStore tokenStore,
        string owner = "wahaha232",
        string repo = "ANDROID-APP-DEV",
        string branch = "main",
        string dataPath = "gpstracker-web/data",
        TimeSpan? retryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _owner = owner;
        _repo = repo;
        _branch = branch;
        _dataPath = dataPath.TrimEnd('/');
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<UploadResult> UploadAsync(
        GpxBuildResult build,
        CancellationToken cancellationToken = default)
    {
        if (build.Error != null)
        {
            return UploadResult.Failed(GpxErrors.Get(build.Error));
        }

        if (build.Track == null || build.Entry == null)
        {
            return UploadResult.Failed("匯入資料不完整。");
        }

        if (!_tokenStore.TryLoad(out var token) || string.IsNullOrWhiteSpace(token))
        {
            return UploadResult.Failed("尚未設定 GitHub Token。請先到設定畫面輸入。");
        }

        try
        {
            var manifestPath = CombineDataPath("manifest.json");
            var manifest = await ReadJsonAsync(manifestPath, token, cancellationToken).ConfigureAwait(false)
                ?? new JsonObject { ["tracks"] = new JsonArray() };

            var identities = ReadTrackIdentities(manifest);
            if (GpxTrackBuilder.IsDuplicate(
                    identities,
                    new TrackIdentity(build.Entry.StartTimeMs, build.Entry.DistanceMeters)))
            {
                return UploadResult.Duplicate(build.Entry.TrackId);
            }

            var trackPath = CombineDataPath(build.Entry.File);
            await PutFileAsync(
                trackPath,
                GpxJson.SerializeTrack(build.Track),
                $"GPS Tracker: import GPX {build.Entry.TrackId}",
                token,
                cancellationToken).ConfigureAwait(false);

            var entryNode = JsonNode.Parse(GpxJson.SerializeEntry(build.Entry))
                ?? throw new InvalidDataException("manifest 項目序列化失敗。");

            await UpdateJsonAsync(
                manifestPath,
                () => new JsonObject { ["tracks"] = new JsonArray() },
                node =>
                {
                    if (node["tracks"] is not JsonArray tracks)
                    {
                        tracks = new JsonArray();
                        node["tracks"] = tracks;
                    }

                    var exists = tracks
                        .OfType<JsonObject>()
                        .Any(item => item["trackId"]?.GetValue<string>() == build.Entry.TrackId);
                    if (!exists)
                    {
                        tracks.Add(entryNode.DeepClone());
                    }

                    node["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    return node;
                },
                "GPS Tracker: update manifest after GPX import",
                pretty: false,
                token,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(build.Summary?.Name))
            {
                await UpdateJsonAsync(
                    CombineDataPath("meta.json"),
                    () => new JsonObject { ["tracks"] = new JsonObject() },
                    node =>
                    {
                        if (node["tracks"] is not JsonObject tracks)
                        {
                            tracks = new JsonObject();
                            node["tracks"] = tracks;
                        }

                        if (tracks[build.Entry.TrackId] is not JsonObject item)
                        {
                            item = new JsonObject();
                            tracks[build.Entry.TrackId] = item;
                        }

                        item["title"] = build.Summary.Name;
                        return node;
                    },
                    $"GPS Tracker: title for imported {build.Entry.TrackId}",
                    pretty: true,
                    token,
                    cancellationToken).ConfigureAwait(false);
            }

            return UploadResult.Uploaded(build.Entry.TrackId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or IOException or
                                   JsonException or InvalidDataException or InvalidOperationException)
        {
            return UploadResult.Failed(ex.Message);
        }
    }

    private async Task<JsonNode?> ReadJsonAsync(
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        var file = await GetFileAsync(relativePath, token, cancellationToken).ConfigureAwait(false);
        return file == null ? null : JsonNode.Parse(file.Content);
    }

    private async Task<GitHubFile?> GetFileAsync(
        string relativePath,
        string token,
        CancellationToken cancellationToken)
    {
        var url = ContentsUrl(relativePath) + "?ref=" + Uri.EscapeDataString(_branch);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GitHubApiException(
                response.StatusCode,
                $"讀取 {relativePath} 失敗：HTTP {(int)response.StatusCode} {Snippet(body)}".Trim());
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var sha = root.TryGetProperty("sha", out var shaValue) ? shaValue.GetString() ?? string.Empty : string.Empty;
        var encoding = root.TryGetProperty("encoding", out var encodingValue)
            ? encodingValue.GetString()
            : "base64";
        var content = root.TryGetProperty("content", out var contentValue)
            ? contentValue.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(sha))
        {
            // 超過 Contents API 大小上限的檔案不會直接回傳內容，改用 Git Blob API。
            content = await GetBlobAsync(sha, token, cancellationToken).ConfigureAwait(false);
            encoding = "base64";
        }

        if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{relativePath} 的回傳格式不支援：{encoding}");
        }

        var bytes = Convert.FromBase64String(RemoveWhitespace(content));
        return new GitHubFile(Encoding.UTF8.GetString(bytes), sha);
    }

    private async Task<string> GetBlobAsync(
        string sha,
        string token,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/git/blobs/{Uri.EscapeDataString(sha)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, token);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(
                response.StatusCode,
                $"讀取 blob 失敗：HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task PutFileAsync(
        string relativePath,
        string content,
        string message,
        string token,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await GetFileAsync(relativePath, token, cancellationToken).ConfigureAwait(false);
            var body = new JsonObject
            {
                ["message"] = message,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                ["branch"] = _branch,
            };
            if (existing != null)
            {
                body["sha"] = existing.Sha;
            }

            var result = await PutJsonAsync(relativePath, body, token, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return;
            }

            if (IsConflict(result.StatusCode) && attempt < MaxAttempts - 1)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new GitHubApiException(
                result.StatusCode,
                $"寫入 {relativePath} 失敗：HTTP {(int)result.StatusCode} {Snippet(result.Body)}".Trim());
        }

        throw new GitHubApiException(HttpStatusCode.Conflict, $"寫入 {relativePath} 失敗：重試次數用完。");
    }

    private async Task<JsonObject> UpdateJsonAsync(
        string relativePath,
        Func<JsonObject> emptyFactory,
        Func<JsonObject, JsonObject> mutate,
        string message,
        bool pretty,
        string token,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var existing = await GetFileAsync(relativePath, token, cancellationToken).ConfigureAwait(false);
            var node = existing == null
                ? emptyFactory()
                : JsonNode.Parse(existing.Content) as JsonObject
                  ?? throw new InvalidDataException($"{relativePath} 不是 JSON 物件。");

            var updated = mutate(node) ?? node;
            var text = pretty
                ? updated.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                : updated.ToJsonString();
            var body = new JsonObject
            {
                ["message"] = message,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                ["branch"] = _branch,
            };
            if (existing != null)
            {
                body["sha"] = existing.Sha;
            }

            var result = await PutJsonAsync(relativePath, body, token, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return updated;
            }

            if (IsConflict(result.StatusCode) && attempt < MaxAttempts - 1)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new GitHubApiException(
                result.StatusCode,
                $"寫入 {relativePath} 失敗：HTTP {(int)result.StatusCode} {Snippet(result.Body)}".Trim());
        }

        throw new GitHubApiException(HttpStatusCode.Conflict, $"寫入 {relativePath} 失敗：重試次數用完。");
    }

    private async Task<PutOutcome> PutJsonAsync(
        string relativePath,
        JsonObject body,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ContentsUrl(relativePath));
        AddHeaders(request, token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return new PutOutcome(true, response.StatusCode, string.Empty);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new PutOutcome(false, response.StatusCode, responseBody);
    }

    private static List<TrackIdentity> ReadTrackIdentities(JsonNode manifest)
    {
        var identities = new List<TrackIdentity>();
        if (manifest["tracks"] is not JsonArray tracks)
        {
            return identities;
        }

        foreach (var track in tracks.OfType<JsonObject>())
        {
            identities.Add(new TrackIdentity(
                ReadLong(track["startTimeMs"]),
                ReadDouble(track["distanceMeters"])));
        }

        return identities;
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var parsed))
            {
                return parsed;
            }

            if (value.TryGetValue<double>(out var asDouble))
            {
                return (long)asDouble;
            }
        }

        return 0L;
    }

    private static double ReadDouble(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var parsed))
            {
                return parsed;
            }

            if (value.TryGetValue<long>(out var asLong))
            {
                return asLong;
            }
        }

        return 0.0;
    }

    private string CombineDataPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName != Path.GetFileName(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("紀錄檔名不合法。");
        }

        return _dataPath + "/" + fileName;
    }

    private string ContentsUrl(string relativePath)
    {
        var escapedPath = string.Join(
            "/",
            relativePath.Split('/').Select(Uri.EscapeDataString));
        return $"https://api.github.com/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/contents/{escapedPath}";
    }

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(_retryDelay.TotalMilliseconds * (attempt + 1));
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsConflict(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity;

    private static string RemoveWhitespace(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string Snippet(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    private sealed record GitHubFile(string Content, string Sha);

    private sealed record PutOutcome(bool Success, HttpStatusCode StatusCode, string Body);
}
