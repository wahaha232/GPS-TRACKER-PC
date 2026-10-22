using System.Globalization;
using System.Text.RegularExpressions;

namespace GpsTrackerPc.Core.Gpx;

/// <summary>
/// 把外部 GPX 轉成網站紀錄格式。規則和 gpstracker-web 的 gt-gpx.js 相同：
/// 沒有時間的點略過、暫停與 GPS 跳點不計入距離、最多 20000 個點、防重複匯入。
/// </summary>
public static class GpxTrackBuilder
{
    public const int MaxPoints = 20_000;
    public const int ActiveGapSeconds = 120;
    public const double MaxSpeedKmh = 400.0;

    private const double EarthRadiusMeters = 6_371_000.0;

    private static readonly Regex TrackNameRegex = new(
        @"<trk\b[\s\S]*?<name\b[^>]*>([^<]*)</name>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrackPointRegex = new(
        @"<trkpt\b([^>]*?)(?:/>|>([\s\S]*?)</trkpt>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LatAttributeRegex = new(
        @"\blat\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.Compiled);

    private static readonly Regex LonAttributeRegex = new(
        @"\blon\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.Compiled);

    private static readonly Regex ElevationRegex = new(
        @"<ele(?:\s[^>]*)?>([^<]*)</ele>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimeRegex = new(
        @"<time(?:\s[^>]*)?>([^<]*)</time>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FloatPrefixRegex = new(
        @"^\s*[+-]?(?:(?:\d+\.?\d*)|(?:\.\d+))(?:[eE][+-]?\d+)?",
        RegexOptions.Compiled);

    /// <summary>解析 GPX 文字；多個 trk／trkseg 會依檔案順序合併。</summary>
    public static GpxDocument Parse(string? xml)
    {
        var text = xml ?? string.Empty;
        string? name = null;
        var nameMatch = TrackNameRegex.Match(text);
        if (nameMatch.Success)
        {
            var decoded = Decode(nameMatch.Groups[1].Value.Trim());
            if (decoded.Length > 0)
            {
                name = decoded;
            }
        }

        var points = new List<GpxPoint>();
        foreach (Match match in TrackPointRegex.Matches(text))
        {
            var attributes = match.Groups[1].Value;
            var lat = ParseFloat(AttributeValue(LatAttributeRegex, attributes));
            var lon = ParseFloat(AttributeValue(LonAttributeRegex, attributes));
            if (!double.IsFinite(lat) || !double.IsFinite(lon) ||
                Math.Abs(lat) > 90.0 || Math.Abs(lon) > 180.0)
            {
                continue;
            }

            var body = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            var elevationText = TagText(ElevationRegex, body);
            var timeText = TagText(TimeRegex, body);
            var elevation = elevationText == null ? double.NaN : ParseFloat(elevationText);
            var timeMs = string.IsNullOrEmpty(timeText) ? double.NaN : ParseDateMs(timeText);
            points.Add(new GpxPoint(
                Lat: lat,
                Lon: lon,
                Elevation: double.IsFinite(elevation) ? elevation : null,
                TimeMs: timeMs));
        }

        return new GpxDocument(name, points);
    }

    /// <summary>轉成網站紀錄格式；失敗時回傳 Error 代碼。</summary>
    public static GpxBuildResult Build(GpxDocument? parsed, GpxBuildOptions? options = null)
    {
        options ??= new GpxBuildOptions();
        var all = parsed?.Points ?? Array.Empty<GpxPoint>();
        if (all.Count < 2)
        {
            return GpxBuildResult.Fail("no-points");
        }

        var timed = all
            .Where(point => double.IsFinite(point.TimeMs))
            .OrderBy(point => point.TimeMs)
            .ToList();

        // 時間必須嚴格遞增；同一秒的重複點只留第一個。
        var points = new List<GpxPoint>(timed.Count);
        foreach (var point in timed)
        {
            if (points.Count == 0 || point.TimeMs > points[^1].TimeMs)
            {
                points.Add(point);
            }
        }

        if (points.Count < 2)
        {
            return GpxBuildResult.Fail(timed.Count > 0 ? "too-few" : "no-time");
        }

        if (points.Count > MaxPoints)
        {
            var step = (points.Count - 1) / (double)(MaxPoints - 1);
            var kept = new List<GpxPoint>(MaxPoints);
            for (var index = 0; index < MaxPoints; index++)
            {
                // 和 JS Math.round 一樣：正數遇到 .5 往上進位。
                kept.Add(points[(int)Math.Floor(index * step + 0.5)]);
            }

            points = kept;
        }

        var count = points.Count;
        var startTimeMs = points[0].TimeMs;
        var distanceMeters = 0.0;
        var activeSeconds = 0.0;
        var maxKmh = 0.0;
        var speeds = new double[count];

        for (var index = 1; index < count; index++)
        {
            var deltaSeconds = (points[index].TimeMs - points[index - 1].TimeMs) / 1000.0;
            var distance = Haversine(
                points[index - 1].Lon,
                points[index - 1].Lat,
                points[index].Lon,
                points[index].Lat);
            var kmh = distance / deltaSeconds * 3.6;

            if (kmh > MaxSpeedKmh)
            {
                continue; // GPS 跳點：不算距離，速度記 0。
            }

            if (deltaSeconds > ActiveGapSeconds)
            {
                continue; // 暫停／GPS 遺失／搭捷運：整段跳躍不算距離。
            }

            distanceMeters += distance;
            speeds[index] = kmh;
            activeSeconds += deltaSeconds;
            if (kmh > maxKmh)
            {
                maxKmh = kmh;
            }
        }

        speeds[0] = speeds[1];
        var elapsedSeconds = (points[^1].TimeMs - startTimeMs) / 1000.0;
        if (!(activeSeconds > 0))
        {
            activeSeconds = elapsedSeconds;
        }

        // 海拔：缺值以前一個有效值補，5 點移動平均後用 2 m 門檻累計。
        var elevations = points.Select(point => point.Elevation).ToList();
        var validElevations = elevations.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        var ascent = 0.0;
        var descent = 0.0;
        double? maxAltitude = null;
        double? minAltitude = null;

        if (validElevations.Count >= 2)
        {
            var last = validElevations[0];
            var filled = new double[elevations.Count];
            for (var index = 0; index < elevations.Count; index++)
            {
                if (elevations[index].HasValue)
                {
                    last = elevations[index]!.Value;
                }

                filled[index] = last;
            }

            var smoothed = MovingAverage(filled, 5);
            var reference = smoothed[0];
            foreach (var value in smoothed)
            {
                if (value - reference >= 2.0)
                {
                    ascent += value - reference;
                    reference = value;
                }
                else if (reference - value >= 2.0)
                {
                    descent += reference - value;
                    reference = value;
                }
            }

            maxAltitude = validElevations.Max();
            minAltitude = validElevations.Min();
        }

        var trackId = string.IsNullOrWhiteSpace(options.Id) ? Guid.NewGuid().ToString() : options.Id!;
        var mode = string.IsNullOrWhiteSpace(options.Mode) ? "UNKNOWN" : options.Mode!;
        var endSeconds = RoundToLong(elapsedSeconds);
        var startMs = RoundToLong(startTimeMs);
        var endMs = RoundToLong(points[^1].TimeMs);

        var pointRows = new List<double[]>(count);
        for (var index = 0; index < count; index++)
        {
            var point = points[index];
            pointRows.Add(new[]
            {
                Round(point.Lon, 6),
                Round(point.Lat, 6),
                Round(point.Elevation ?? 0.0, 1),
                Round(speeds[index], 1),
                (double)RoundToLong((point.TimeMs - startTimeMs) / 1000.0),
            });
        }

        var track = new GpxTrack
        {
            TrackId = trackId,
            StartTimeMs = startMs,
            EndTimeMs = endMs,
            DurationMs = RoundToLong(activeSeconds * 1000.0),
            DistanceMeters = Round(distanceMeters, 2),
            AvgSpeedMps = Round(distanceMeters / activeSeconds, 3),
            MaxSpeedMps = Round(maxKmh / 3.6, 3),
            StepCount = 0,
            StepSource = "NONE",
            DominantMode = mode,
            GpsPointCount = count,
            PhotoCount = 0,
            TotalAscentMeters = Round(ascent, 1),
            TotalDescentMeters = Round(descent, 1),
            MaxAltitudeMeters = maxAltitude.HasValue ? Round(maxAltitude.Value, 1) : null,
            MinAltitudeMeters = minAltitude.HasValue ? Round(minAltitude.Value, 1) : null,
            Points = pointRows,
            Photos = new List<object[]>(),
            Segments = new List<object[]> { new object[] { mode, 0L, endSeconds } },
            Source = "gpx-import",
        };

        var entry = new GpxTrackEntry
        {
            TrackId = trackId,
            StartTimeMs = track.StartTimeMs,
            DistanceMeters = track.DistanceMeters,
            DurationMs = track.DurationMs,
            AvgSpeedMps = track.AvgSpeedMps,
            MaxSpeedMps = track.MaxSpeedMps,
            StepCount = 0,
            DominantMode = mode,
            TotalAscentMeters = track.TotalAscentMeters,
            TotalDescentMeters = track.TotalDescentMeters,
            PhotoCount = 0,
            File = trackId + ".json",
        };

        var summary = new GpxImportSummary(
            parsed?.Name,
            count,
            all.Count - timed.Count,
            track.StartTimeMs,
            track.DistanceMeters,
            track.DurationMs,
            validElevations.Count >= 2);

        return new GpxBuildResult(track, entry, summary, null);
    }

    /// <summary>開始時間相差 5 秒內、距離相差 1% 或 50 m 內，視為重複匯入。</summary>
    public static bool IsDuplicate(IEnumerable<ITrackIdentity>? manifestTracks, ITrackIdentity entry)
    {
        if (manifestTracks == null)
        {
            return false;
        }

        foreach (var track in manifestTracks)
        {
            var startDifference = Math.Abs((double)track.StartTimeMs - entry.StartTimeMs);
            var distanceDifference = Math.Abs(track.DistanceMeters - entry.DistanceMeters);
            if (startDifference <= 5000.0 &&
                distanceDifference <= Math.Max(50.0, entry.DistanceMeters * 0.01))
            {
                return true;
            }
        }

        return false;
    }

    private static double Haversine(double aLon, double aLat, double bLon, double bLat)
    {
        var latitudeDelta = (bLat - aLat) * Math.PI / 180.0;
        var longitudeDelta = (bLon - aLon) * Math.PI / 180.0;
        var a = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2) +
            Math.Cos(aLat * Math.PI / 180.0) * Math.Cos(bLat * Math.PI / 180.0) *
            Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static double[] MovingAverage(double[] values, int window)
    {
        var half = window / 2;
        var result = new double[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var sum = 0.0;
            var count = 0;
            for (var other = Math.Max(0, index - half); other <= Math.Min(values.Length - 1, index + half); other++)
            {
                sum += values[other];
                count++;
            }

            result[index] = sum / count;
        }

        return result;
    }

    private static double Round(double value, int digits)
    {
        var factor = Math.Pow(10, digits);
        return Math.Floor(value * factor + 0.5) / factor;
    }

    private static long RoundToLong(double value) => (long)Math.Floor(value + 0.5);

    private static string? AttributeValue(Regex regex, string attributes)
    {
        var match = regex.Match(attributes);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string? TagText(Regex regex, string body)
    {
        var match = regex.Match(body);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static double ParseFloat(string? value)
    {
        if (value == null)
        {
            return double.NaN;
        }

        var match = FloatPrefixRegex.Match(value);
        if (!match.Success)
        {
            return double.NaN;
        }

        return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.NaN;
    }

    private static double ParseDateMs(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : double.NaN;
    }

    private static string Decode(string value) => value
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&apos;", "'", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);
}
