using System.Globalization;
using System.Text;
using System.Text.Json;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Simulator;

public sealed record GeocodingResult(
    string Name,
    string DisplayName,
    Coordinates Coordinates,
    double ZoomLevel);

/// <summary>確保兩次請求至少間隔指定時間（Nominatim 要求每秒最多一次）。</summary>
public sealed class RequestRateLimiter
{
    private readonly TimeSpan _minimumInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastRequestAt;

    public RequestRateLimiter(
        TimeSpan minimumInterval,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _minimumInterval = minimumInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
    }

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock();
            if (_lastRequestAt is { } last)
            {
                var elapsed = now - last;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }

                var wait = _minimumInterval - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastRequestAt = _clock();
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Nominatim 地點搜尋：帶自訂 User-Agent、限制回應大小，並用 <see cref="RequestRateLimiter"/>
/// 保證每秒最多一次。網路層由外部注入，方便用假的 HttpMessageHandler 測試。
/// </summary>
public sealed class NominatimGeocoder
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly int _maxResponseBytes;
    private readonly RequestRateLimiter _rateLimiter;

    public NominatimGeocoder(
        HttpClient httpClient,
        string userAgent = "GpsTrackerPc/1.0",
        int maxResponseBytes = MaxResponseBytes,
        RequestRateLimiter? rateLimiter = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "GpsTrackerPc/1.0" : userAgent;
        _maxResponseBytes = maxResponseBytes;
        _rateLimiter = rateLimiter ?? new RequestRateLimiter(TimeSpan.FromSeconds(1));
    }

    public async Task<IReadOnlyList<GeocodingResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<GeocodingResult>();
        }

        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        var url = "https://nominatim.openstreetmap.org/search" +
            "?format=jsonv2" +
            "&addressdetails=1" +
            "&limit=8" +
            "&accept-language=zh-TW,zh,en" +
            "&q=" + Uri.EscapeDataString(trimmed);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en;q=0.8");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nominatim 回傳 HTTP {(int)response.StatusCode}");
        }

        var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        return GeocodingResponseParser.Parse(body);
    }

    private async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > _maxResponseBytes)
        {
            throw new IOException($"Nominatim 回應超過 {_maxResponseBytes} bytes 上限");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > _maxResponseBytes)
            {
                throw new IOException($"Nominatim 回應超過 {_maxResponseBytes} bytes 上限");
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }
}

internal static class GeocodingResponseParser
{
    public static IReadOnlyList<GeocodingResult> Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<GeocodingResult>();
            }

            var results = new List<GeocodingResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetDouble(item, "lat", out var latitude) ||
                    !TryGetDouble(item, "lon", out var longitude))
                {
                    continue;
                }

                Coordinates coordinates;
                try
                {
                    coordinates = new Coordinates(latitude, longitude);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var displayName = GetString(item, "display_name").Trim();
                if (displayName.Length == 0)
                {
                    continue;
                }

                var name = GetString(item, "name").Trim();
                if (name.Length == 0)
                {
                    name = displayName.Split(',')[0].Trim();
                }

                var key = coordinates.FormattedLatitude() + "," + coordinates.FormattedLongitude();
                if (!seen.Add(key))
                {
                    continue;
                }

                var addressType = GetString(item, "addresstype");
                if (addressType.Length == 0)
                {
                    addressType = GetString(item, "type");
                }

                results.Add(new GeocodingResult(name, displayName, coordinates, ZoomFor(addressType)));
            }

            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<GeocodingResult>();
        }
    }

    private static bool TryGetDouble(JsonElement element, string property, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var node))
        {
            return false;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out value))
        {
            return true;
        }

        if (node.ValueKind == JsonValueKind.String &&
            double.TryParse(node.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;

    private static double ZoomFor(string addressType) => addressType.ToLowerInvariant() switch
    {
        "country" => 5.0,
        "state" or "province" or "region" => 7.0,
        "county" => 9.0,
        "city" or "town" or "municipality" => 12.0,
        "borough" or "district" or "suburb" or "quarter" => 14.0,
        "village" or "neighbourhood" => 15.0,
        _ => 16.0,
    };
}
