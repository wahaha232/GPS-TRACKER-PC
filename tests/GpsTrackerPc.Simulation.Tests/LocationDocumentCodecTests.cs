using System.Text;
using System.Text.RegularExpressions;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class LocationDocumentCodecTests
{
    private static readonly SimulationRoute RouteData = new(
        name: "Delivery & QA",
        waypoints: new List<RouteWaypoint>
        {
            new(name: "Start", coordinates: new Coordinates(31.2304, 121.4737), stopDurationSeconds: 5),
            new(name: "Finish", coordinates: new Coordinates(31.2404, 121.4837), stopDurationSeconds: 30),
        },
        speedKph: 42.0,
        loop: true);

    private static readonly SimulationRoute TimedRoute = new(
        name: "Timed",
        waypoints: new List<RouteWaypoint>
        {
            new(name: "A", coordinates: new Coordinates(25.0, 121.0), timeOffsetSeconds: 0),
            new(name: "B", coordinates: new Coordinates(25.001, 121.001), timeOffsetSeconds: 30),
            new(name: "C", coordinates: new Coordinates(25.002, 121.002), timeOffsetSeconds: 100),
        },
        speedKph: 5.0,
        loop: false,
        createdAt: 1_700_000_000_000L,
        useGpxTiming: true,
        outages: new[] { new RouteOutage(10, 20), new RouteOutage(60, 5) },
        naturalSpeed: true);

    [Fact]
    public void GpxRoundTripPreservesRouteSettings()
    {
        var imported = LocationDocumentCodec.Import(
            LocationDocumentCodec.Export(RouteData, LocationDocumentFormat.Gpx));

        AssertRouteMatches(imported);
    }

    [Fact]
    public void KmlRoundTripPreservesRouteSettings()
    {
        var imported = LocationDocumentCodec.Import(
            LocationDocumentCodec.Export(RouteData, LocationDocumentFormat.Kml));

        AssertRouteMatches(imported);
    }

    [Fact]
    public void GpxExportWritesTimeAndKeepsPlaybackSettings()
    {
        var gpx = LocationDocumentCodec.Export(TimedRoute, LocationDocumentFormat.Gpx);
        Assert.Equal(3, Regex.Matches(gpx, "<time>").Count);

        var imported = LocationDocumentCodec.Import(gpx);
        Assert.Equal(new long?[] { 0L, 30L, 100L }, imported.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));
        Assert.True(imported.UseGpxTiming);
        Assert.True(imported.NaturalSpeed);
        Assert.Equal(TimedRoute.Outages, imported.Outages);
    }

    [Fact]
    public void GpxExportRemembersThatTimingWasTurnedOff()
    {
        var constantSpeed = TimedRoute with
        {
            UseGpxTiming = false,
            NaturalSpeed = false,
            Outages = Array.Empty<RouteOutage>(),
        };
        var imported = LocationDocumentCodec.Import(
            LocationDocumentCodec.Export(constantSpeed, LocationDocumentFormat.Gpx));
        Assert.False(imported.UseGpxTiming);
        Assert.Equal(new long?[] { 0L, 30L, 100L }, imported.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));
    }

    [Fact]
    public void KmlExportKeepsTimingAndPlaybackSettings()
    {
        var imported = LocationDocumentCodec.Import(
            LocationDocumentCodec.Export(TimedRoute, LocationDocumentFormat.Kml));
        Assert.Equal(new long?[] { 0L, 30L, 100L }, imported.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));
        Assert.True(imported.UseGpxTiming);
        Assert.True(imported.NaturalSpeed);
        Assert.Equal(TimedRoute.Outages, imported.Outages);
    }

    [Fact]
    public void GpxWithLongLeadingCommentIsStillRecognised()
    {
        var comment = "<!-- " + new string('x', 20_000) + " -->";
        var gpx = $"""<?xml version="1.0"?>{comment}<gpx version="1.1"><trk><trkseg><trkpt lat="1" lon="2"/><trkpt lat="1.1" lon="2.1"/></trkseg></trk></gpx>""";
        var imported = LocationDocumentCodec.ImportStream(new MemoryStream(Encoding.UTF8.GetBytes(gpx))).Route;
        Assert.Equal(2, imported.Waypoints.Count);
        Assert.Equal(2, LocationDocumentCodec.Import(gpx).Waypoints.Count);
    }

    [Fact]
    public void ImportsGenericKmlLineString()
    {
        var imported = LocationDocumentCodec.Import(
            """<kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Line</name><Placemark><LineString><coordinates>10,20,0 11,21,0</coordinates></LineString></Placemark></Document></kml>""");

        Assert.Equal(2, imported.Waypoints.Count);
        Assert.Equal(new Coordinates(20.0, 10.0), imported.Waypoints[0].Coordinates);
    }

    [Fact]
    public void ImportRejectsDocumentsWithExternalEntities()
    {
        const string document = """<?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY xxe SYSTEM="file:///etc/passwd">]><gpx version="1.1"><wpt lat="1" lon="2"><name>&xxe;</name></wpt><wpt lat="2" lon="3"/></gpx>""";

        Assert.Throws<ArgumentException>(() => LocationDocumentCodec.Import(document));
    }

    private static void AssertRouteMatches(SimulationRoute imported)
    {
        Assert.Equal(RouteData.Name, imported.Name);
        TestAssert.Close(RouteData.SpeedKph, imported.SpeedKph, 0.001);
        Assert.Equal(RouteData.Loop, imported.Loop);
        Assert.Equal(
            RouteData.Waypoints.Select(waypoint => waypoint.Name),
            imported.Waypoints.Select(waypoint => waypoint.Name));
        Assert.Equal(
            RouteData.Waypoints.Select(waypoint => waypoint.StopDurationSeconds),
            imported.Waypoints.Select(waypoint => waypoint.StopDurationSeconds));
        Assert.True(
            imported.Waypoints
                .Zip(RouteData.Waypoints)
                .All(pair => GeoMath.DistanceMeters(pair.First.Coordinates, pair.Second.Coordinates) < 0.1));
    }
}
