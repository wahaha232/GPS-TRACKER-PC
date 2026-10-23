using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GpsTrackerPc.Core.Updates;

/// <summary>
/// 查詢 GitHub Release 與下載更新檔。公開 repo，不需要 Token；
/// 下載網址必須是固定的 GitHub Releases 前綴，下載後一定要通過 SHA-256。
/// </summary>
public sealed class UpdateService
{
    public const string LatestTag = "gpstracker-desktop-latest";

    public const string LatestApiUrl =
        "https://api.github.com/repos/" + UpdateVersion.Repo + "/releases/tags/" + LatestTag;

    private const int MaxJsonBytes = 512 * 1024;
    private const int MaxChecksumBytes = 16 * 1024;
    private const long MaxAssetBytes = 500L * 1024 * 1024;

    private static readonly Regex Sha256Pattern = new(
        @"\b[0-9a-fA-F]{64}\b",
        RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly Func<string?>? _tokenProvider;

    /// <param name="tokenProvider">
    /// 選用的 GitHub Token 來源。未登入查詢每小時只有 60 次（同一出口網路共用），
    /// 有 Token 時查詢 Release 會帶上授權以提高上限；Token 只用於 api.github.com，不會帶到下載網址。
    /// </param>
    public UpdateService(
        HttpClient httpClient,
        string userAgent = "GpsTrackerPc/1.0",
        Func<string?>? tokenProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "GpsTrackerPc/1.0" : userAgent;
        _tokenProvider = tokenProvider;
    }

    /// <summary>回傳比目前版本新的更新；沒有新版本時回傳 null。</summary>
    public async Task<UpdateInfo?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var token = ReadToken();
        var usedToken = !string.IsNullOrEmpty(token);
        var response = await SendCheckAsync(token, cancellationToken).ConfigureAwait(false);

        // Token 失效就改回未登入查詢，不要讓更新檢查因此失敗。
        if (usedToken && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            usedToken = false;
            response = await SendCheckAsync(token: null, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException(
                    "找不到更新 Release（HTTP 404），可能尚未建立，或 GitHub repo 還不存在。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await CreateFailureAsync(response, usedToken, cancellationToken).ConfigureAwait(false);
            }

            var json = await ReadBoundedAsync(response, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
            return ParseRelease(json, currentVersion);
        }
    }

    private async Task<HttpResponseMessage> SendCheckAsync(string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        AddHeaders(request);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private string? ReadToken()
    {
        if (_tokenProvider == null)
        {
            return null;
        }

        try
        {
            return _tokenProvider();
        }
        catch (Exception)
        {
            // Token 讀取失敗（例如 DPAPI 解密失敗）就當作未設定，不影響更新檢查。
            return null;
        }
    }

    /// <summary>把 GitHub 的 403／429 等錯誤轉成使用者看得懂、可以照著處理的說明。</summary>
    private static async Task<UpdateException> CreateFailureAsync(
        HttpResponseMessage response,
        bool usedToken,
        CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new UpdateException(
                "GitHub 拒絕這個 Token（HTTP 401），可能已失效或複製不完整。請到設定畫面重新輸入 Token。");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = ReadRetryAfter(response);
            return new UpdateException(
                "GitHub 暫時限制查詢（HTTP 429）" +
                (wait is null ? "。" : $"，約 {wait.Value.TotalSeconds:0} 秒後可再試。") +
                "請稍後再檢查。");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (IsRateLimited(response))
            {
                var reset = ReadRateLimitReset(response);
                var resetText = reset is null
                    ? string.Empty
                    : $"配額約在 {reset.Value:HH:mm} 重設，";
                var hint = usedToken
                    ? "已設定的 GitHub Token 可能沒有讀取 Release 的權限，請確認後再試。"
                    : "未登入每小時只有 60 次查詢，同一網路的所有程式共用；到設定畫面填入 GitHub Token 可提高上限。";
                return new UpdateException(
                    "GitHub 查詢次數已用完（HTTP 403）。" + resetText + hint);
            }

            var detail = await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            return new UpdateException(
                "GitHub 拒絕查詢（HTTP 403）" +
                (detail.Length == 0 ? "。" : $"：{detail}"));
        }

        return new UpdateException($"查詢更新失敗（HTTP {status}）");
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
        string.Equals(values.FirstOrDefault(), "0", StringComparison.Ordinal);

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values) ||
            !long.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static async Task<string> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await ReadBoundedAsync(response, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var message = GetString(document.RootElement, "message");
            return message.Length > 200 ? message[..200] : message;
        }
        catch (Exception ex) when (ex is JsonException or UpdateException)
        {
            return string.Empty;
        }
    }

    private static UpdateInfo? ParseRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var releaseName = GetString(root, "name");
        var notes = GetString(root, "body");
        var version = UpdateVersion.VersionOf(releaseName) ?? UpdateVersion.VersionOf(notes)
            ?? throw new UpdateException("Release 標題看不出最新版本號");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new UpdateException("Release 沒有附檔");
        }

        string? checksumUrl = null;
        string? exeUrl = null;
        string? exeName = null;
        long exeSize = 0;
        string? zipUrl = null;
        string? zipName = null;
        long zipSize = 0;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                if (checksumUrl == null && UpdateVersion.IsSafeDownloadUrl(url))
                {
                    checksumUrl = url;
                }

                continue;
            }

            if (!UpdateVersion.IsSafeDownloadUrl(url))
            {
                continue;
            }

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exeUrl == null)
            {
                exeUrl = url;
                exeName = name;
                exeSize = ReadSize(asset);
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zipUrl == null)
            {
                zipUrl = url;
                zipName = name;
                zipSize = ReadSize(asset);
            }
        }

        // 新流程發佈單一 EXE；若 Release 還留著舊 ZIP，也優先用 EXE。
        var downloadUrl = exeUrl ?? zipUrl;
        var assetName = exeName ?? zipName;
        var size = exeUrl != null ? exeSize : zipSize;

        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
        {
            throw new UpdateException("Release 裡找不到安全的 EXE／ZIP");
        }

        if (string.IsNullOrWhiteSpace(checksumUrl))
        {
            throw new UpdateException("Release 裡找不到 SHA-256 檢查碼");
        }

        if (!UpdateVersion.IsNewer(currentVersion, version))
        {
            return null;
        }

        return new UpdateInfo(
            version,
            releaseName,
            downloadUrl,
            checksumUrl,
            assetName,
            size,
            notes);
    }

    /// <summary>下載到指定路徑並驗證 SHA-256；任何失敗都不會留下半個檔案。</summary>
    public async Task DownloadAsync(
        UpdateInfo update,
        string destinationPath,
        IProgress<double?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!UpdateVersion.IsSafeDownloadUrl(update.DownloadUrl) ||
            !UpdateVersion.IsSafeDownloadUrl(update.ChecksumUrl))
        {
            throw new UpdateException("下載網址不安全，已中止");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = destinationPath + ".part";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
            AddHeaders(request);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException($"下載更新失敗（HTTP {(int)response.StatusCode}）");
            }

            EnsureAllowedDownloadResponse(response);

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is { } length && length > MaxAssetBytes)
            {
                throw new UpdateException("更新檔過大，已中止");
            }

            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            {
                await using var output = new FileStream(
                    partPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxAssetBytes)
                    {
                        throw new UpdateException("更新檔過大，已中止");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(contentLength is > 0
                        ? Math.Clamp((double)total / contentLength.Value, 0.0, 1.0)
                        : null);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var expectedHash = await DownloadChecksumAsync(update.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destinationPath);
                throw new UpdateException("SHA-256 檢查碼不符，已刪除下載檔");
            }

            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"下載 SHA-256 檢查碼失敗（HTTP {(int)response.StatusCode}）");
        }

        EnsureAllowedDownloadResponse(response);

        var text = await ReadBoundedAsync(response, MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
        var match = Sha256Pattern.Match(text);
        if (!match.Success)
        {
            throw new UpdateException("SHA-256 檢查碼格式不正確");
        }

        return match.Value.ToLowerInvariant();
    }

    /// <summary>
    /// 允許 GitHub 把 Releases 檔案轉到 objects.githubusercontent.com 等下載伺服器；
    /// 轉到其他網域就拒絕。假的 handler 沒有 RequestMessage 時，初始網址已經檢查過，直接放行。
    /// </summary>
    private static void EnsureAllowedDownloadResponse(HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri == null)
        {
            return;
        }

        if (!IsAllowedDownloadHost(finalUri.Host))
        {
            throw new UpdateException($"下載被重新導向到不允許的網域：{finalUri.Host}");
        }
    }

    private static bool IsAllowedDownloadHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("無法連線到 GitHub：" + ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("連線 GitHub 逾時：" + ex.Message);
        }
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new UpdateException("伺服器回應過大");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                throw new UpdateException("伺服器回應過大");
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadSize(JsonElement asset) =>
        asset.TryGetProperty("size", out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetInt64(out var size)
            ? size
            : 0L;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失敗不覆蓋原本的錯誤。
        }
    }
}
