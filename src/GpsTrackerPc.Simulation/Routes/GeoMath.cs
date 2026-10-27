using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>球面地理計算（和 Kotlin 版相同的公式與地球半徑）。</summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000.0;

    public static double DistanceMeters(Coordinates start, Coordinates end)
    {
        var startLatitude = start.Latitude * Math.PI / 180.0;
        var endLatitude = end.Latitude * Math.PI / 180.0;
        var latitudeDelta = endLatitude - startLatitude;
        var longitudeDelta = (end.Longitude - start.Longitude) * Math.PI / 180.0;
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
            Math.Cos(startLatitude) * Math.Cos(endLatitude) *
            Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        // 接近對蹠點時浮點誤差可能讓 a 略大於 1，Sqrt(1 - a) 會變成 NaN。
        a = Math.Clamp(a, 0.0, 1.0);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static float InitialBearingDegrees(Coordinates start, Coordinates end)
    {
        var startLatitude = start.Latitude * Math.PI / 180.0;
        var endLatitude = end.Latitude * Math.PI / 180.0;
        var longitudeDelta = (end.Longitude - start.Longitude) * Math.PI / 180.0;
        var y = Math.Sin(longitudeDelta) * Math.Cos(endLatitude);
        var x = Math.Cos(startLatitude) * Math.Sin(endLatitude) -
            Math.Sin(startLatitude) * Math.Cos(endLatitude) * Math.Cos(longitudeDelta);
        return (float)((Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0);
    }

    public static Coordinates Interpolate(Coordinates start, Coordinates end, double fraction)
    {
        var clampedFraction = Math.Clamp(fraction, 0.0, 1.0);
        if (clampedFraction == 0.0)
        {
            return start;
        }

        if (clampedFraction == 1.0)
        {
            return end;
        }

        var angularDistance = DistanceMeters(start, end) / EarthRadiusMeters;
        if (angularDistance < 1e-12)
        {
            return start;
        }

        var startLatitude = start.Latitude * Math.PI / 180.0;
        var startLongitude = start.Longitude * Math.PI / 180.0;
        var endLatitude = end.Latitude * Math.PI / 180.0;
        var endLongitude = end.Longitude * Math.PI / 180.0;
        var denominator = Math.Sin(angularDistance);
        var startWeight = Math.Sin((1 - clampedFraction) * angularDistance) / denominator;
        var endWeight = Math.Sin(clampedFraction * angularDistance) / denominator;
        var x = startWeight * Math.Cos(startLatitude) * Math.Cos(startLongitude) +
            endWeight * Math.Cos(endLatitude) * Math.Cos(endLongitude);
        var y = startWeight * Math.Cos(startLatitude) * Math.Sin(startLongitude) +
            endWeight * Math.Cos(endLatitude) * Math.Sin(endLongitude);
        var z = startWeight * Math.Sin(startLatitude) + endWeight * Math.Sin(endLatitude);
        var latitude = Math.Atan2(z, Math.Sqrt(x * x + y * y));
        var longitude = Math.Atan2(y, x);
        return new Coordinates(latitude * 180.0 / Math.PI, longitude * 180.0 / Math.PI);
    }

    public static Coordinates Destination(Coordinates start, double distanceMeters, double bearingDegrees)
    {
        if (!double.IsFinite(distanceMeters) || distanceMeters < 0)
        {
            throw new ArgumentException("距離必須是有限且不小於 0 的數值", nameof(distanceMeters));
        }

        if (!double.IsFinite(bearingDegrees))
        {
            throw new ArgumentException("方位必須是有限數值", nameof(bearingDegrees));
        }

        var angularDistance = distanceMeters / EarthRadiusMeters;
        var bearing = bearingDegrees * Math.PI / 180.0;
        var latitude = start.Latitude * Math.PI / 180.0;
        var longitude = start.Longitude * Math.PI / 180.0;
        var destinationLatitude = Math.Asin(
            Math.Sin(latitude) * Math.Cos(angularDistance) +
            Math.Cos(latitude) * Math.Sin(angularDistance) * Math.Cos(bearing));
        var destinationLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude),
            Math.Cos(angularDistance) - Math.Sin(latitude) * Math.Sin(destinationLatitude));
        var normalizedLongitude = (destinationLongitude * 180.0 / Math.PI + 540.0) % 360.0 - 180.0;
        return new Coordinates(destinationLatitude * 180.0 / Math.PI, normalizedLongitude);
    }
}
