using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

/// <summary>把 JUnit 的「期望值 ± 容許誤差」寫法集中起來。</summary>
internal static class TestAssert
{
    public static void Close(double expected, double actual, double tolerance, string? message = null)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            message ?? $"expected {expected} ± {tolerance}, actual {actual}");
    }

    public static void Close(float expected, float actual, float tolerance, string? message = null)
    {
        Assert.True(
            Math.Abs(expected - actual) <= tolerance,
            message ?? $"expected {expected} ± {tolerance}, actual {actual}");
    }
}
