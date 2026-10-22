using System.Net;
using System.Text;
using GpsTrackerPc.Core.Simulator;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class NominatimGeocoderTests
{
    private const string SampleJson = """
        [
          {"lat":"25.033976","lon":"121.564539","display_name":"台北 101, 信義區, 台北市, 台灣","name":"台北 101","addresstype":"building"},
          {"lat":"25.033976","lon":"121.564539","display_name":"重複座標","name":"重複"},
          {"lat":"999","lon":"121.0","display_name":"不合法","name":"不合法"},
          {"lat":"25.0478","lon":"121.5170","display_name":"台北車站, 中正區, 台北市, 台灣","name":"台北車站","addresstype":"city"}
        ]
        """;

    [Fact]
    public async Task SearchSendsUserAgentAndParsesResults()
    {
        var handler = new FakeHandler(SampleJson);
        var geocoder = new NominatimGeocoder(
            new HttpClient(handler),
            userAgent: "GpsTrackerPc/1.0",
            rateLimiter: new RequestRateLimiter(TimeSpan.Zero));

        var results = await geocoder.SearchAsync("台北 101");

        Assert.Equal(2, results.Count);
        Assert.Equal("台北 101", results[0].Name);
        Assert.Equal(25.033976, results[0].Coordinates.Latitude);
        Assert.Equal(16.0, results[0].ZoomLevel);
        Assert.Equal(12.0, results[1].ZoomLevel);
        Assert.Single(handler.Requests);
        Assert.Contains("GpsTrackerPc/1.0", handler.Requests[0].UserAgent);
        Assert.Contains("format=jsonv2", handler.Requests[0].Query);
        Assert.Contains("q=%E5%8F%B0%E5%8C%97%20101", handler.Requests[0].Query);
    }

    [Fact]
    public async Task EmptyQueryDoesNotCallNetwork()
    {
        var handler = new FakeHandler(SampleJson);
        var geocoder = new NominatimGeocoder(new HttpClient(handler), rateLimiter: new RequestRateLimiter(TimeSpan.Zero));

        var results = await geocoder.SearchAsync("   ");

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var handler = new FakeHandler(SampleJson);
        var geocoder = new NominatimGeocoder(
            new HttpClient(handler),
            maxResponseBytes: 20,
            rateLimiter: new RequestRateLimiter(TimeSpan.Zero));

        await Assert.ThrowsAsync<IOException>(() => geocoder.SearchAsync("台北"));
    }

    [Fact]
    public async Task HttpErrorIsReported()
    {
        var handler = new FakeHandler("error", HttpStatusCode.InternalServerError);
        var geocoder = new NominatimGeocoder(new HttpClient(handler), rateLimiter: new RequestRateLimiter(TimeSpan.Zero));

        await Assert.ThrowsAsync<HttpRequestException>(() => geocoder.SearchAsync("台北"));
    }

    [Fact]
    public async Task GeocoderUsesTheOnePerSecondRateLimiter()
    {
        var now = new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
        var waits = new List<TimeSpan>();
        var limiter = new RequestRateLimiter(
            TimeSpan.FromSeconds(1),
            () => now,
            (delay, _) =>
            {
                waits.Add(delay);
                now += delay;
                return Task.CompletedTask;
            });
        var handler = new FakeHandler(SampleJson);
        var geocoder = new NominatimGeocoder(new HttpClient(handler), rateLimiter: limiter);

        await geocoder.SearchAsync("台北");
        await geocoder.SearchAsync("高雄");

        Assert.Single(waits);
        Assert.Equal(TimeSpan.FromSeconds(1), waits[0]);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed record RecordedRequest(string UserAgent, string Query);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Headers.UserAgent.ToString(),
                request.RequestUri?.Query ?? string.Empty));
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
