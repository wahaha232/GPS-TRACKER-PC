using System.Globalization;
using System.Text;

namespace GpsTrackerPc.Core.Gpx;

/// <summary>
/// 把網站紀錄 JSON 轉成 GPX 1.1。
/// 格式採用 records.js 的 downloadGpx：沒有 &lt;metadata&gt;，時間固定三位毫秒。
/// （gt-core.js 的 gpx() 另外會加 metadata 並跳脫名稱；桌面版統一採用 downloadGpx 這一種。）
/// </summary>
public static class GpxExporter
{
    public static string Export(GpxTrackDocument track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var builder = new StringBuilder();
        var baseMs = track.StartTimeMs;
        var name = FormatLocalDate(baseMs);
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<gpx version=\"1.1\" creator=\"GPS Tracker\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n");
        builder.Append("<trk><name>").Append(name).Append("</name><trkseg>\n");

        foreach (var point in track.Points ?? new List<double[]>())
        {
            var longitude = point.Length > 0 ? point[0] : 0.0;
            var latitude = point.Length > 1 ? point[1] : 0.0;
            var elevation = point.Length > 2 ? point[2] : 0.0;
            var offsetSeconds = point.Length > 4 ? point[4] : 0.0;

            if (!double.IsFinite(longitude))
            {
                longitude = 0.0;
            }

            if (!double.IsFinite(latitude))
            {
                latitude = 0.0;
            }

            if (!double.IsFinite(elevation) || elevation == 0.0)
            {
                elevation = 0.0;
            }

            if (!double.IsFinite(offsetSeconds))
            {
                offsetSeconds = 0.0;
            }

            var time = FormatUtcInstant((long)Math.Truncate(baseMs + offsetSeconds * 1000.0));
            builder.Append("<trkpt lat=\"").Append(FormatNumber(latitude))
                .Append("\" lon=\"").Append(FormatNumber(longitude))
                .Append("\"><ele>").Append(FormatNumber(elevation))
                .Append("</ele><time>").Append(time)
                .Append("</time></trkpt>\n");
        }

        builder.Append("</trkseg></trk></gpx>\n");
        return builder.ToString();
    }

    // JS 的 Number.toString() 對整數不會補 .0；R 格式在一般座標與海拔上結果相同。
    private static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            return "0";
        }

        if (value == 0.0)
        {
            return "0";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatLocalDate(long epochMs)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            .ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatUtcInstant(long epochMs)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
