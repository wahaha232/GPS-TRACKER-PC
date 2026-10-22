using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class SampleRouteFactoryTests
{
    [Fact]
    public void SampleRouteCreatesAValidLoopAroundTheMapCenter()
    {
        var center = new Coordinates(31.2304, 121.4737);
        var names = new[] { "Start", "A", "B", "Finish" };

        var waypoints = SampleRouteFactory.CreateWaypoints(center, names);

        Assert.Equal(names, waypoints.Select(waypoint => waypoint.Name));
        Assert.Equal(center, waypoints[0].Coordinates);
        Assert.Equal(center, waypoints[^1].Coordinates);
        Assert.Equal(3, waypoints.Select(waypoint => waypoint.Coordinates).Distinct().Count());
        _ = new SimulationRoute(
            name: "Sample",
            waypoints: waypoints,
            speedKph: 24.0,
            loop: false);
    }
}
