using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class GeoMathTests
{
    private const double HalfCircumferenceMeters = Math.PI * 6_371_000.0;

    [Fact]
    public void AntipodalDistanceIsNeverNaN()
    {
        // 對蹠點附近浮點誤差會讓 haversine 的 a 略大於 1；逐一掃過多組對蹠點確認不會算出 NaN。
        for (var latitude = -89.0; latitude <= 89.0; latitude += 0.37)
        {
            for (var longitude = -179.0; longitude <= 179.0; longitude += 1.13)
            {
                var start = new Coordinates(latitude, longitude);
                var end = new Coordinates(-latitude, longitude > 0 ? longitude - 180.0 : longitude + 180.0);

                var distance = GeoMath.DistanceMeters(start, end);

                Assert.False(double.IsNaN(distance), $"({latitude}, {longitude}) 算出 NaN");
                TestAssert.Close(HalfCircumferenceMeters, distance, 1.0);
            }
        }
    }

    [Fact]
    public void SamePointDistanceIsZero()
    {
        var point = new Coordinates(25.0478, 121.5170);

        Assert.Equal(0.0, GeoMath.DistanceMeters(point, point));
    }
}
