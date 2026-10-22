using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class RandomRouteGeneratorTests
{
    private static readonly Coordinates Taipei = new(25.0340, 121.5645);

    private static IReadOnlyList<Coordinates> Gen(
        int n = 30,
        double radius = 1000.0,
        RandomRouteShape shape = RandomRouteShape.Walk,
        int seed = 1,
        Coordinates? center = null) =>
        RandomRouteGenerator.Generate(center ?? Taipei, n, radius, shape, new Random(seed));

    [Fact]
    public void ProducesTheRequestedNumberOfValidPointsThatStartAtTheCentre()
    {
        foreach (var shape in Enum.GetValues<RandomRouteShape>())
        {
            foreach (var n in new[] { 2, 3, 30, 500 })
            {
                var points = Gen(n: n, shape: shape);
                Assert.Equal(n, points.Count);
                Assert.Equal(Taipei, points[0]);
                Assert.All(
                    points,
                    point => Assert.True(point.Latitude is >= -90.0 and <= 90.0 &&
                                        point.Longitude is >= -180.0 and <= 180.0));
            }
        }
    }

    [Fact]
    public void RandomWalkStaysNearTheRequestedRadius()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var points = Gen(n: 200, radius: 1000.0, shape: RandomRouteShape.Walk, seed: seed);
            var farthest = points.Max(point => GeoMath.DistanceMeters(Taipei, point));
            Assert.True(farthest <= 1000.0 * 1.5, $"seed={seed} farthest={farthest}");
        }
    }

    [Fact]
    public void RandomWalkReallyMovesAroundInsteadOfPilingUp()
    {
        var points = Gen(n: 100, radius: 2000.0, shape: RandomRouteShape.Walk, seed: 7);
        var farthest = points.Max(point => GeoMath.DistanceMeters(Taipei, point));
        Assert.True(farthest > 2000.0 * 0.2, $"farthest={farthest}");
        Assert.Equal(points.Count, points.Distinct().Count());
    }

    [Fact]
    public void StepsAreNotWildJumps()
    {
        var points = Gen(n: 100, radius: 1000.0, seed: 3);
        var steps = points
            .Zip(points.Skip(1), (first, second) => GeoMath.DistanceMeters(first, second))
            .ToArray();
        var mean = steps.Average();
        Assert.True(steps.Max() <= mean * 3, $"max step {steps.Max()} vs mean {mean}");
        Assert.True(steps.Min() > 0.0);
    }

    [Fact]
    public void LoopGoesAroundAndItsFarSideIsAboutTheRadiusAway()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var points = Gen(n: 60, radius: 1000.0, shape: RandomRouteShape.Loop, seed: seed);
            var farthest = points.Max(point => GeoMath.DistanceMeters(Taipei, point));
            Assert.True(farthest is >= 800.0 and <= 1200.0, $"seed={seed} farthest={farthest}");
            // 最後一點接回起點（循環）要很近：距離不超過相鄰兩點平均間距的 3 倍
            var steps = points
                .Zip(points.Skip(1), (first, second) => GeoMath.DistanceMeters(first, second))
                .ToArray();
            var closing = GeoMath.DistanceMeters(points[^1], points[0]);
            Assert.True(closing <= steps.Average() * 3, $"seed={seed} closing={closing} mean={steps.Average()}");
        }
    }

    [Fact]
    public void SameSeedGivesTheSameTrackAndDifferentSeedsDiffer()
    {
        Assert.Equal(Gen(seed: 42), Gen(seed: 42));
        Assert.NotEqual(Gen(seed: 42), Gen(seed: 43));
        Assert.Equal(Gen(shape: RandomRouteShape.Loop, seed: 5), Gen(shape: RandomRouteShape.Loop, seed: 5));
    }

    [Fact]
    public void WorksNearThePolesAndAcrossTheDateLine()
    {
        var pole = Gen(center: new Coordinates(89.9, 10.0), radius: 5000.0);
        Assert.All(pole, point => Assert.True(point.Latitude is >= -90.0 and <= 90.0));
        var dateLine = Gen(center: new Coordinates(0.0, 179.999), radius: 5000.0, n: 100);
        Assert.All(dateLine, point => Assert.True(point.Longitude is >= -180.0 and <= 180.0));
        // 越過換日線後仍然是合法路線（至少兩個不同座標）
        Assert.True(dateLine.Distinct().Count() >= 2);
    }

    [Fact]
    public void GeneratedPointsMakeAValidSimulationRoute()
    {
        foreach (var shape in Enum.GetValues<RandomRouteShape>())
        {
            var points = Gen(n: 25, shape: shape);
            var route = new SimulationRoute(
                name: "random",
                waypoints: points
                    .Select((coordinates, index) => new RouteWaypoint(name: $"P{index + 1}", coordinates: coordinates))
                    .ToList(),
                speedKph: 5.0,
                loop: shape == RandomRouteShape.Loop);
            var simulator = new RouteSimulator(route);
            var steps = 0;
            while (!simulator.Advance(5.0).IsComplete && steps < 100_000)
            {
                steps++;
            }

            if (shape == RandomRouteShape.Walk)
            {
                Assert.True(steps < 100_000, "walk should finish");
            }
        }
    }

    [Fact]
    public void RejectsOutOfRangeInput()
    {
        foreach (var bad in new[] { 1, 0, 501, -3 })
        {
            Assert.Throws<ArgumentException>(() => Gen(n: bad));
        }

        Assert.Throws<ArgumentException>(() => Gen(radius: 10.0));
        Assert.Throws<ArgumentException>(() => Gen(radius: 60_000.0));
    }
}
