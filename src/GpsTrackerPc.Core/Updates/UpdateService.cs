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

    public UpdateService(HttpClient httpClient, string userAgent = "GpsTrackerPc/1.0")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "GpsTrackerPc/1.0" : userAgent;
    }

    /// <summary>回傳比目前版本新的更新；沒有新版本時回傳 null。</summary>
    public async Task<UpdateInfo?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        AddHeaders(request);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateException(
                "找不到更新 Release（HTTP 404），可能尚未建立，或 GitHub repo 還不存在。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"查詢更新失敗（HTTP {(int)response.StatusCode}）");
        }

        var json = await ReadBoundedAsync(response, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
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

        var text = await ReadBoundedAsync(response, MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
        var match = Sha256Pattern.Match(text);
        if (!match.Success)
        {
            throw new UpdateException("SHA-256 檢查碼格式不正確");
        }

        return match.Value.ToLowerInvariant();
    }

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
