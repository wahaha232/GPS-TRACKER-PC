using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class RouteSimulatorTests
{
    [Fact]
    public void AdvanceMovesAtConfiguredSpeed()
    {
        var route = Route(
            start: new Coordinates(0.0, 0.0),
            end: new Coordinates(1.0, 0.0),
            speedKph: 36.0);
        var simulator = new RouteSimulator(route);

        var position = simulator.Advance(1_000.0);

        TestAssert.Close(10_000.0, GeoMath.DistanceMeters(route.Waypoints[0].Coordinates, position.Coordinates), 2.0);
        TestAssert.Close(10f, position.SpeedMetersPerSecond, 0.01f);
        Assert.False(position.IsComplete);
    }

    [Fact]
    public void WaypointStopIsAppliedBeforeCompletion()
    {
        var route = new SimulationRoute(
            name: "Stop test",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint("Start", new Coordinates(0.0, 0.0)),
                Waypoint("End", new Coordinates(0.001, 0.0), stopSeconds: 60),
            },
            speedKph: 36.0,
            loop: false);
        var simulator = new RouteSimulator(route);

        var stopped = simulator.Advance(12.0);
        Assert.True(stopped.IsStopped);
        Assert.False(stopped.IsComplete);

        var completed = simulator.Advance(60.0);
        Assert.True(completed.IsComplete);
        Assert.Equal(route.Waypoints[^1].Coordinates, completed.Coordinates);
    }

    [Fact]
    public void LoopIncludesReturnLegAndIncrementsLap()
    {
        var route = Route(
            start: new Coordinates(0.0, 0.0),
            end: new Coordinates(0.001, 0.0),
            speedKph: 36.0,
            loop: true);
        var lapSeconds = GeoMath.DistanceMeters(
            route.Waypoints[0].Coordinates,
            route.Waypoints[1].Coordinates) * 2 / 10.0;
        var position = new RouteSimulator(route).Advance(lapSeconds + 1.0);

        Assert.Equal(1, position.Lap);
        Assert.False(position.IsComplete);
        Assert.True(position.Progress > 0);
    }

    [Fact]
    public void InterpolationUsesShortestPathAcrossDateLine()
    {
        var midpoint = GeoMath.Interpolate(
            new Coordinates(0.0, 179.0),
            new Coordinates(0.0, -179.0),
            0.5);

        Assert.True(Math.Abs(midpoint.Longitude) > 179.9);
    }

    [Fact]
    public void DestinationMovesByRequestedDistanceAndBearing()
    {
        var start = new Coordinates(31.2304, 121.4737);
        var destination = GeoMath.Destination(start, 10.0, 90.0);

        TestAssert.Close(10.0, GeoMath.DistanceMeters(start, destination), 0.01);
        TestAssert.Close(90f, GeoMath.InitialBearingDegrees(start, destination), 0.1f);
    }

    private static SimulationRoute Route(
        Coordinates start,
        Coordinates end,
        double speedKph,
        bool loop = false) =>
        new(
            name: "Test route",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint("Start", start),
                Waypoint("End", end),
            },
            speedKph: speedKph,
            loop: loop);

    private static RouteWaypoint Waypoint(string name, Coordinates coordinates, long stopSeconds = 0) =>
        new(name: name, coordinates: coordinates, stopDurationSeconds: stopSeconds);
}
