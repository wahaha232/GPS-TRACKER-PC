using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class RouteEditorModelTests
{
    private static readonly Coordinates Taipei = new(25.033964, 121.564472);
    private static readonly Coordinates NearTaipei = new(25.0345, 121.5650);
    private static readonly Coordinates FarTaipei = new(25.0400, 121.5700);

    [Fact]
    public void AddMoveStopReorderAndDeleteWaypoints()
    {
        var model = new RouteEditorModel();
        var first = model.AddWaypoint(Taipei);
        var second = model.AddWaypoint(NearTaipei, stopDurationSeconds: 12);
        var third = model.AddWaypoint(FarTaipei);

        Assert.Equal(3, model.Waypoints.Count);
        Assert.Equal("路線點 1", model.Waypoints[0].Name);
        Assert.Equal(12L, model.Waypoints[1].StopDurationSeconds);

        Assert.True(model.MoveWaypoint(second, new Coordinates(25.035, 121.566)));
        Assert.Equal(25.035, model.Waypoints[1].Coordinates.Latitude);

        Assert.True(model.SetWaypointStop(second, 30));
        Assert.Equal(30L, model.Waypoints[1].StopDurationSeconds);
        Assert.Throws<ArgumentException>(() => model.SetWaypointStop(second, -1));
        Assert.Throws<ArgumentException>(() => model.SetWaypointStop(second, 86_401));

        Assert.True(model.ReorderWaypoint(third, 0));
        Assert.Equal(new[] { third, first, second }, model.Waypoints.Select(waypoint => waypoint.Id));

        Assert.True(model.DeleteWaypoint(first));
        Assert.Equal(2, model.Waypoints.Count);
        Assert.False(model.DeleteWaypoint("not-found"));
    }

    [Fact]
    public void ClearWaypointsRemovesEveryPointButKeepsOutages()
    {
        var model = new RouteEditorModel();
        model.AddWaypoint(Taipei, timeOffsetSeconds: 0);
        model.AddWaypoint(NearTaipei, timeOffsetSeconds: 10);
        model.SetUseGpxTiming(true);
        model.AddOutage(30, 10);

        model.ClearWaypoints();

        Assert.Empty(model.Waypoints);
        Assert.False(model.UseGpxTiming);
        Assert.Single(model.Outages);
    }

    [Fact]
    public void SpeedMustStayWithinTheSimulationLimits()
    {
        var model = new RouteEditorModel();

        model.SetSpeed(0.5);
        Assert.Equal(0.5, model.SpeedKph);
        model.SetSpeed(500);
        Assert.Equal(500, model.SpeedKph);

        Assert.Throws<ArgumentException>(() => model.SetSpeed(0.4));
        Assert.Throws<ArgumentException>(() => model.SetSpeed(500.1));
        Assert.Throws<ArgumentException>(() => model.SetSpeed(double.NaN));
    }

    [Fact]
    public void TimingIsAvailableOnlyWhenEveryPointHasATime()
    {
        var model = new RouteEditorModel();
        model.AddWaypoint(Taipei, timeOffsetSeconds: 0);
        model.AddWaypoint(NearTaipei, timeOffsetSeconds: 10);

        Assert.True(model.CanUseGpxTiming);
        model.SetUseGpxTiming(true);
        Assert.True(model.UseGpxTiming);

        model.AddWaypoint(FarTaipei); // 新點沒有時間 → 自動關閉依時間播放
        Assert.False(model.CanUseGpxTiming);
        Assert.False(model.UseGpxTiming);
        Assert.Throws<InvalidOperationException>(() => model.SetUseGpxTiming(true));
    }

    [Fact]
    public void OutagesAreValidatedAndSorted()
    {
        var model = new RouteEditorModel();
        var later = model.AddOutage(60, 30);
        var earlier = model.AddOutage(10, 5);

        Assert.Equal(new[] { earlier, later }, model.Outages);

        Assert.True(model.RemoveOutage(earlier));
        Assert.Single(model.Outages);
        Assert.Throws<ArgumentException>(() => model.AddOutage(-1, 10));
        Assert.Throws<ArgumentException>(() => model.AddOutage(0, 0));
    }

    [Fact]
    public void RandomRouteUsesTheCenterAndReplacesTheRoute()
    {
        var model = new RouteEditorModel();
        model.AddWaypoint(FarTaipei);
        model.AddOutage(1, 2);

        model.GenerateRandom(
            Taipei,
            pointCount: 20,
            radiusMeters: 1000,
            shape: RandomRouteShape.Loop,
            random: new Random(42),
            name: "隨機測試");

        Assert.Equal(20, model.Waypoints.Count);
        Assert.Equal("隨機測試", model.Name);
        Assert.False(model.Loop);
        Assert.Empty(model.Outages);
        Assert.False(model.UseGpxTiming);
        Assert.Equal(Taipei, model.Waypoints[0].Coordinates);
    }

    [Fact]
    public void ToRouteRequiresANameAndAtLeastTwoDistinctPoints()
    {
        var model = new RouteEditorModel();
        Assert.Throws<ArgumentException>(() => model.ToRoute());

        model.AddWaypoint(Taipei);
        Assert.Throws<ArgumentException>(() => model.ToRoute());

        model.AddWaypoint(NearTaipei);
        model.Name = "";
        Assert.Throws<ArgumentException>(() => model.ToRoute());

        model.Name = "測試路線";
        model.SetSpeed(12);
        model.Loop = true;
        model.NaturalSpeed = true;
        model.AddOutage(5, 10);
        var route = model.ToRoute();

        Assert.Equal("測試路線", route.Name);
        Assert.Equal(12, route.SpeedKph);
        Assert.True(route.Loop);
        Assert.True(route.NaturalSpeed);
        Assert.Single(route.Outages);
        Assert.Equal(2, route.Waypoints.Count);
    }

    [Fact]
    public void ReplaceRouteCopiesEverySettingAndResetCreatesANewRoute()
    {
        var original = new SimulationRoute(
            name: "匯入路線",
            waypoints: new List<RouteWaypoint>
            {
                new("A", Taipei, timeOffsetSeconds: 0),
                new("B", NearTaipei, stopDurationSeconds: 5, timeOffsetSeconds: 20),
            },
            speedKph: 18,
            loop: true,
            useGpxTiming: true,
            outages: new[] { new RouteOutage(10, 30) },
            naturalSpeed: true,
            createdAt: 123456789,
            id: "route-id");
        var model = new RouteEditorModel();

        model.ReplaceRoute(original);

        Assert.Equal("route-id", model.Id);
        Assert.Equal(123456789, model.CreatedAt);
        Assert.Equal("匯入路線", model.Name);
        Assert.Equal(18, model.SpeedKph);
        Assert.True(model.Loop);
        Assert.True(model.NaturalSpeed);
        Assert.True(model.UseGpxTiming);
        Assert.Single(model.Outages);
        Assert.Equal(2, model.Waypoints.Count);

        model.Reset();
        Assert.Empty(model.Waypoints);
        Assert.Empty(model.Outages);
        Assert.Equal("新路線", model.Name);
        Assert.Equal(5.0, model.SpeedKph);
        Assert.False(model.Loop);
        Assert.False(model.NaturalSpeed);
        Assert.False(model.UseGpxTiming);
        Assert.NotEqual("route-id", model.Id);
    }
}
