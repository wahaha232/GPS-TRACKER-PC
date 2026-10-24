using System.Net;
using System.Text;
using System.Text.Json;

namespace GpsTrackerPc.Core.Social;

public sealed record SocialPollResult(
    IReadOnlyList<SocialSnapshot> Snapshots,
    string? LastMessageId);

public interface ISocialTransport
{
    Task PublishAsync(SocialTarget target, SocialSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<SocialPollResult> PollAsync(
        SocialTarget target,
        string? sinceMessageId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 用 ntfy 的公開中繼服務交換加密後的位置訊息。
/// 免費、免註冊；主題與金鑰都由邀請碼產生，換掉邀請碼就等於換一個新的分享空間。
/// </summary>
public sealed class NtfySocialTransport : ISocialTransport
{
    private const string UserAgent = "GpsTrackerPc/1.0";
    private const int MaxPollLines = 2000;

    private readonly HttpClient _httpClient;

    public NtfySocialTransport(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task PublishAsync(
        SocialTarget target,
        SocialSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(snapshot);

        var body = SocialCrypto.Encrypt(snapshot, target.KeyBase64);
        using var request = new HttpRequestMessage(HttpMethod.Post, TopicUrl(target));
        request.Headers.TryAddWithoutValidation("Title", "GPS Tracker");
        request.Headers.TryAddWithoutValidation("Priority", "1");
        request.Headers.TryAddWithoutValidation("Tags", "satellite");
        request.Headers.TryAddWithoutValidation("Cache", "yes");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Content = new StringContent(body, Encoding.UTF8, "text/plain");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await CreateExceptionAsync(response, "發布位置", cancellationToken).ConfigureAwait(false);
    }

    public async Task<SocialPollResult> PollAsync(
        SocialTarget target,
        string? sinceMessageId,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);

        var since = string.IsNullOrWhiteSpace(sinceMessageId) ? "30m" : sinceMessageId.Trim();
        var url = TopicUrl(target) + "/json?poll=1&since=" + Uri.EscapeDataString(since);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, "讀取好友位置", cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var snapshots = new List<SocialSnapshot>();
        string? lastMessageId = null;
        for (var lineNumber = 0; lineNumber < MaxPollLines; lineNumber++)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryReadMessage(line, target, out var id, out var snapshot))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(id))
            {
                lastMessageId = id;
            }

            if (snapshot != null)
            {
                snapshots.Add(snapshot);
            }
        }

        return new SocialPollResult(snapshots, lastMessageId);
    }

    private static bool TryReadMessage(
        string line,
        SocialTarget target,
        out string? id,
        out SocialSnapshot? snapshot)
    {
        id = null;
        snapshot = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("event", out var eventNode) ||
                !string.Equals(eventNode.GetString(), "message", StringComparison.Ordinal))
            {
                return false;
            }

            if (root.TryGetProperty("id", out var idNode))
            {
                id = idNode.GetString();
            }

            if (!root.TryGetProperty("message", out var messageNode) ||
                messageNode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            snapshot = SocialCrypto.TryDecrypt(messageNode.GetString(), target.KeyBase64);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateTarget(SocialTarget target)
    {
        if (!SocialValidation.IsValidTarget(target))
        {
            throw new SocialException(null, "好友／群組分享資料不完整，請重新加入。");
        }
    }

    private static string TopicUrl(SocialTarget target) =>
        target.Server.TrimEnd('/') + "/" + Uri.EscapeDataString(target.Topic);

    private static async Task<SocialException> CreateExceptionAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        var detail = string.Empty;
        try
        {
            detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (detail.Length > 200)
            {
                detail = detail[..200];
            }
        }
        catch (Exception)
        {
            // 讀不到回應內容不影響錯誤訊息。
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.NotFound => $"{action}失敗：中繼主題不存在（HTTP 404），請確認邀請碼是否正確。",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{action}失敗：中繼服務拒絕存取（HTTP {(int)response.StatusCode}）。",
            HttpStatusCode.TooManyRequests =>
                $"{action}失敗：中繼服務忙碌或已達頻率上限（HTTP 429），請稍後再試。",
            HttpStatusCode.RequestEntityTooLarge =>
                $"{action}失敗：訊息太大（HTTP 413）。",
            _ => $"{action}失敗：HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(),
        };

        if (!string.IsNullOrEmpty(detail))
        {
            message += " " + detail;
        }

        return new SocialException(response.StatusCode, message);
    }
}
