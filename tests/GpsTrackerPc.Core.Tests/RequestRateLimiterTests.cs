using GpsTrackerPc.Core.Simulator;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class RequestRateLimiterTests
{
    [Fact]
    public async Task SecondRequestWaitsUntilOneSecondHasPassed()
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

        await limiter.WaitAsync();
        now += TimeSpan.FromMilliseconds(300);
        await limiter.WaitAsync();

        Assert.Single(waits);
        Assert.Equal(TimeSpan.FromMilliseconds(700), waits[0]);

        await limiter.WaitAsync();
        Assert.Equal(2, waits.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), waits[1]);
    }

    [Fact]
    public async Task FirstRequestAndZeroIntervalDoNotWait()
    {
        var waits = new List<TimeSpan>();
        var limiter = new RequestRateLimiter(
            TimeSpan.Zero,
            () => DateTimeOffset.UnixEpoch,
            (delay, _) =>
            {
                waits.Add(delay);
                return Task.CompletedTask;
            });

        await limiter.WaitAsync();
        await limiter.WaitAsync();

        Assert.Empty(waits);
    }

    [Fact]
    public async Task CancelledTokenDoesNotRequest()
    {
        var limiter = new RequestRateLimiter(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cancellation.Token));
    }
}
