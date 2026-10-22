using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>建立繞著地圖中心的範例路線。</summary>
public static class SampleRouteFactory
{
    private const double PreviewRadiusMeters = 250.0;

    public static IReadOnlyList<RouteWaypoint> CreateWaypoints(Coordinates center, IReadOnlyList<string> names)
    {
        if (names.Count != 4)
        {
            throw new ArgumentException("需要四個名稱", nameof(names));
        }

        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("名稱不能是空白", nameof(names));
        }

        return new List<RouteWaypoint>
        {
            new(name: names[0], coordinates: center),
            new(name: names[1], coordinates: GeoMath.Destination(center, PreviewRadiusMeters, 45.0)),
            new(name: names[2], coordinates: GeoMath.Destination(center, PreviewRadiusMeters, 135.0)),
            new(name: names[3], coordinates: center),
        };
    }
}
