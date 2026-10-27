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
    public void NonLoopRouteStopsAtLastWaypoint()
    {
        var route = ThreePointRoute(loop: false);

        var position = new RouteSimulator(route).Advance(1_000.0);

        Assert.True(position.IsComplete);
        Assert.Equal(route.Waypoints[^1].Coordinates, position.Coordinates);
        Assert.Equal(2, position.CurrentWaypointIndex);
        Assert.Equal(0, position.Lap);
    }

    [Fact]
    public void LoopRouteReturnsAlongTheSamePathInsteadOfJumpingToStart()
    {
        // A(0,0) → B(0.001,0) → C(0.001,0.001)，L 形。折返時要走 C → B → A，
        // 不能從 C 斜線直接回 A。
        var route = ThreePointRoute(loop: true);
        var legSeconds = GeoMath.DistanceMeters(route.Waypoints[0].Coordinates, route.Waypoints[1].Coordinates) / 10.0;
        var simulator = new RouteSimulator(route);

        // 去程走完兩段後，再往回走半段：應該在 C–B 線段中間（緯度維持 0.001）。
        var returning = simulator.Advance(legSeconds * 2.5);
        TestAssert.Close(0.001, returning.Coordinates.Latitude, 1e-6);
        Assert.InRange(returning.Coordinates.Longitude, 0.0002, 0.0008);
        Assert.Equal(0, returning.Lap);
        Assert.False(returning.IsComplete);

        // 再走半段到 B、再一段回到 A：完成一圈。
        var backAtStart = simulator.Advance(legSeconds * 1.5 + 0.01);
        Assert.Equal(1, backAtStart.Lap);
        Assert.Equal(0, backAtStart.CurrentWaypointIndex);
        Assert.True(GeoMath.DistanceMeters(route.Waypoints[0].Coordinates, backAtStart.Coordinates) < 0.5);
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

    private static SimulationRoute ThreePointRoute(bool loop) =>
        new(
            name: "Three points",
            waypoints: new List<RouteWaypoint>
            {
                Waypoint("A", new Coordinates(0.0, 0.0)),
                Waypoint("B", new Coordinates(0.001, 0.0)),
                Waypoint("C", new Coordinates(0.001, 0.001)),
            },
            speedKph: 36.0,
            loop: loop);

    private static RouteWaypoint Waypoint(string name, Coordinates coordinates, long stopSeconds = 0) =>
        new(name: name, coordinates: coordinates, stopDurationSeconds: stopSeconds);
}
