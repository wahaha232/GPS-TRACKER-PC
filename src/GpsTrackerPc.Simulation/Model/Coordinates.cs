using System.Globalization;

namespace GpsTrackerPc.Simulation.Model;

/// <summary>經緯度座標；以 record 保留依值比較相等。</summary>
public sealed record Coordinates
{
    public Coordinates(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90.0 or > 90.0)
        {
            throw new ArgumentException("緯度必須是 -90 到 90 之間的有限數值", nameof(latitude));
        }

        if (!double.IsFinite(longitude) || longitude is < -180.0 or > 180.0)
        {
            throw new ArgumentException("經度必須是 -180 到 180 之間的有限數值", nameof(longitude));
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    /// <summary>第一次啟動（還沒有任何已儲存位置）時的預設位置：台北 101 一帶。</summary>
    public static Coordinates Default { get; } = new(25.033964, 121.564472);

    public string FormattedLatitude() => Latitude.ToString("F6", CultureInfo.InvariantCulture);

    public string FormattedLongitude() => Longitude.ToString("F6", CultureInfo.InvariantCulture);

    public static Coordinates? Parse(string latitude, string longitude)
    {
        var latitudeText = latitude.Trim();
        var longitudeText = longitude.Trim();

        if (!double.TryParse(latitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLatitude))
        {
            return null;
        }

        if (!double.TryParse(longitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLongitude))
        {
            return null;
        }

        if (!double.IsFinite(parsedLatitude) || parsedLatitude is < -90.0 or > 90.0)
        {
            return null;
        }

        if (!double.IsFinite(parsedLongitude) || parsedLongitude is < -180.0 or > 180.0)
        {
            return null;
        }

        return new Coordinates(parsedLatitude, parsedLongitude);
    }
}
