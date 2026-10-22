using Xunit;

namespace GpsTrackerPc.Core.Tests;

internal static class TestAssert
{
    public static void Close(double expected, double actual, double tolerance, string? message = null)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            message ?? $"expected {expected} ± {tolerance}, actual {actual}");
    }
}
