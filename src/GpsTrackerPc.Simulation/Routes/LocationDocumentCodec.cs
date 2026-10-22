using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Util;

namespace GpsTrackerPc.Simulation.Routes;

public enum LocationDocumentFormat
{
    Gpx,
    Kml,
}

/// <summary>GPX／KML 匯出匯入，以及本 App JSON 的串流入口。</summary>
public static class LocationDocumentCodec
{
    private const int SniffBytes = 64 * 1024;
    private const double DefaultSpeedKph = 30.0;
    private const int MaxImportedPoints = GpxStreamImporter.MaxPoints;

    public static string Export(SimulationRoute route, LocationDocumentFormat format) => format switch
    {
        LocationDocumentFormat.Gpx => ExportGpx(route),
        LocationDocumentFormat.Kml => ExportKml(route),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static SimulationRoute Import(string value, string fallbackName = "Imported route")
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("DOCTYPE is not allowed", nameof(value));
        }

        if (value.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Entities are not allowed", nameof(value));
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (GpxStreamImporter.LooksLikeGpx(bytes, Math.Min(bytes.Length, SniffBytes)))
        {
            return GpxStreamImporter.Import(new MemoryStream(bytes), fallbackName).Route;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new InvalidOperationException("Unsupported location document");
        return root.Name.LocalName.ToLowerInvariant() switch
        {
            "kml" => ImportKml(root, fallbackName),
            _ => throw new InvalidOperationException("Unsupported location document"),
        };
    }

    /// <summary>
    /// 從串流匯入路線。GPX 走串流解析，大小上限 <see cref="BoundedIo.GpxStreamMaxBytes"/>（300 MB），
    /// 最後留下最多 <see cref="GpxStreamImporter.MaxPoints"/> 個點。其他格式（KML、本 App 的 JSON）
    /// 仍整份讀進來處理，大小上限 <see cref="BoundedIo.RouteDocumentMaxBytes"/>。
    /// </summary>
    public static GpxImportResult ImportStream(
        Stream stream,
        string fallbackName = "Imported route",
        Func<bool>? isCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var (head, read) = GpxStreamImporter.ReadHead(stream, SniffBytes);
        var input = new PrefixedStream(head, read, stream);
        if (GpxStreamImporter.LooksLikeGpx(head, read))
        {
            return GpxStreamImporter.Import(
                BoundedIo.Limit(input, BoundedIo.GpxStreamMaxBytes),
                fallbackName,
                isCancelled: isCancelled);
        }

        var text = BoundedIo.ReadText(input, BoundedIo.RouteDocumentMaxBytes);
        var route = RouteJsonCodec.Decode(text) ?? Import(text, fallbackName);
        return new GpxImportResult(route, route.Waypoints.Count);
    }

    private static string ExportGpx(SimulationRoute route)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<gpx version=\"1.1\" creator=\"GPS Locator\" xmlns=\"http://www.topografix.com/GPX/1/1\" xmlns:gpsupdater=\"https://gpsupdater.app/schema/1\">\n");
        builder.Append("  <trk><name>").Append(Escape(route.Name)).Append("</name><extensions>");
        builder.Append("<gpsupdater:speedKph>").Append(FormatDouble(route.SpeedKph)).Append("</gpsupdater:speedKph>");
        builder.Append("<gpsupdater:loop>").Append(FormatBoolean(route.Loop)).Append("</gpsupdater:loop>");
        builder.Append("<gpsupdater:useGpxTiming>").Append(FormatBoolean(route.UseGpxTiming)).Append("</gpsupdater:useGpxTiming>");
        builder.Append("<gpsupdater:naturalSpeed>").Append(FormatBoolean(route.NaturalSpeed)).Append("</gpsupdater:naturalSpeed>");
        foreach (var outage in route.Outages)
        {
            builder.Append("<gpsupdater:outage start=\"")
                .Append(outage.StartSeconds.ToString(CultureInfo.InvariantCulture))
                .Append("\" duration=\"")
                .Append(outage.DurationSeconds.ToString(CultureInfo.InvariantCulture))
                .Append("\"/>");
        }

        builder.Append("</extensions><trkseg>\n");

        // 每個點都有時間才寫 <time>；匯入時只看相對時間，所以起點用路線建立時間。
        var baseEpoch = route.CreatedAt / 1000;
        var hasTiming = SimulationRoute.HasTiming(route.Waypoints);
        foreach (var waypoint in route.Waypoints)
        {
            builder.Append("    <trkpt lat=\"")
                .Append(FormatDouble(waypoint.Coordinates.Latitude))
                .Append("\" lon=\"")
                .Append(FormatDouble(waypoint.Coordinates.Longitude))
                .Append("\">");
            builder.Append("<name>").Append(Escape(waypoint.Name)).Append("</name>");
            if (hasTiming)
            {
                builder.Append("<time>")
                    .Append(FormatInstant(baseEpoch + waypoint.TimeOffsetSeconds!.Value))
                    .Append("</time>");
            }

            builder.Append("<extensions><gpsupdater:stopSeconds>")
                .Append(waypoint.StopDurationSeconds.ToString(CultureInfo.InvariantCulture))
                .Append("</gpsupdater:stopSeconds></extensions>");
            builder.Append("</trkpt>\n");
        }

        builder.Append("  </trkseg></trk>\n</gpx>\n");
        return builder.ToString();
    }

    private static string ExportKml(SimulationRoute route)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>");
        builder.Append("<name>").Append(Escape(route.Name)).Append("</name>");
        builder.Append("<ExtendedData><Data name=\"speedKph\"><value>")
            .Append(FormatDouble(route.SpeedKph))
            .Append("</value></Data>");
        builder.Append("<Data name=\"loop\"><value>").Append(FormatBoolean(route.Loop)).Append("</value></Data>");
        builder.Append("<Data name=\"useGpxTiming\"><value>")
            .Append(FormatBoolean(route.UseGpxTiming))
            .Append("</value></Data>");
        builder.Append("<Data name=\"naturalSpeed\"><value>")
            .Append(FormatBoolean(route.NaturalSpeed))
            .Append("</value></Data>");
        if (route.Outages.Count > 0)
        {
            builder.Append("<Data name=\"outages\"><value>")
                .Append(string.Join(",", route.Outages.Select(outage => $"{outage.StartSeconds}:{outage.DurationSeconds}")))
                .Append("</value></Data>");
        }

        builder.Append("</ExtendedData>\n");

        for (var index = 0; index < route.Waypoints.Count; index++)
        {
            var waypoint = route.Waypoints[index];
            builder.Append("<Placemark><name>").Append(Escape(waypoint.Name)).Append("</name>");
            builder.Append("<ExtendedData><Data name=\"order\"><value>")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append("</value></Data>");
            builder.Append("<Data name=\"stopSeconds\"><value>")
                .Append(waypoint.StopDurationSeconds.ToString(CultureInfo.InvariantCulture))
                .Append("</value></Data>");
            if (waypoint.TimeOffsetSeconds is { } timeOffset)
            {
                builder.Append("<Data name=\"timeOffsetSeconds\"><value>")
                    .Append(timeOffset.ToString(CultureInfo.InvariantCulture))
                    .Append("</value></Data>");
            }

            builder.Append("</ExtendedData>");
            builder.Append("<Point><coordinates>")
                .Append(FormatDouble(waypoint.Coordinates.Longitude))
                .Append(',')
                .Append(FormatDouble(waypoint.Coordinates.Latitude))
                .Append(",0</coordinates></Point></Placemark>\n");
        }

        builder.Append("<Placemark><name>").Append(Escape(route.Name)).Append("</name><LineString><coordinates>");
        builder.Append(string.Join(
            " ",
            route.Waypoints.Select(waypoint =>
                $"{FormatDouble(waypoint.Coordinates.Longitude)},{FormatDouble(waypoint.Coordinates.Latitude)},0")));
        builder.Append("</coordinates></LineString></Placemark>");
        builder.Append("</Document></kml>\n");
        return builder.ToString();
    }

    /// <summary>從 <paramref name="size"/> 個點中均勻挑出最多 <paramref name="max"/> 個索引，一定包含第一個與最後一個。</summary>
    public static IReadOnlyList<int> ThinIndices(int size, int max)
    {
        if (size <= max)
        {
            return Enumerable.Range(0, size).ToList();
        }

        var stepValue = (double)(size - 1) / (max - 1);
        return Enumerable.Range(0, max)
            .Select(index => index == max - 1 ? size - 1 : (int)(index * stepValue))
            .Distinct()
            .ToList();
    }

    private static SimulationRoute ImportKml(XElement root, string fallbackName)
    {
        var document = DescendantElements(root, "Document").FirstOrDefault() ?? root;
        var placemarks = DescendantElements(document, "Placemark").ToList();
        var pointPlacemarks = placemarks
            .Where(placemark => DescendantElements(placemark, "Point").Any())
            .ToList();

        List<RouteWaypoint> allWaypoints;
        if (pointPlacemarks.Count > 0)
        {
            allWaypoints = pointPlacemarks
                .OrderBy(placemark =>
                    int.TryParse(DataValue(placemark, "order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var order)
                        ? order
                        : int.MaxValue)
                .Select((placemark, index) =>
                {
                    var coordinate = ParseKmlCoordinate(
                        DescendantElements(placemark, "coordinates").First().Value);
                    var name = ChildText(placemark, "name");
                    return new RouteWaypoint(
                        name: string.IsNullOrWhiteSpace(name) ? $"Point {index + 1}" : name,
                        coordinates: coordinate,
                        stopDurationSeconds: ParseLong(DataValue(placemark, "stopSeconds")) ?? 0,
                        timeOffsetSeconds: ParseLong(DataValue(placemark, "timeOffsetSeconds")) is { } offset && offset >= 0
                            ? offset
                            : null);
                })
                .ToList();
        }
        else
        {
            var coordinatesText = DescendantElements(document, "LineString")
                .FirstOrDefault()
                ?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "coordinates")
                ?.Value
                .Trim();
            var coordinates = coordinatesText == null
                ? Array.Empty<string>()
                : Regex.Split(coordinatesText, "\\s+");
            allWaypoints = coordinates
                .Select((coordinate, index) => new RouteWaypoint(
                    name: $"Point {index + 1}",
                    coordinates: ParseKmlCoordinate(coordinate)))
                .ToList();
        }

        // 點太多就均勻抽稀（保留首尾），和 GPX 一致。
        var waypoints = ThinIndices(allWaypoints.Count, MaxImportedPoints)
            .Select(index => allWaypoints[index])
            .ToList();
        var speed = ParseDouble(DataValue(document, "speedKph")) ?? DefaultSpeedKph;
        var loop = ParseStrictBoolean(DataValue(document, "loop")) ?? false;
        var outages = new List<RouteOutage>();
        var outageText = DataValue(document, "outages");
        if (!string.IsNullOrEmpty(outageText))
        {
            foreach (var item in outageText.Split(','))
            {
                var parts = item.Split(':');
                if (parts.Length != 2)
                {
                    continue;
                }

                var start = ParseLong(parts[0].Trim());
                var duration = ParseLong(parts[1].Trim());
                if (start == null || duration == null)
                {
                    continue;
                }

                try
                {
                    outages.Add(new RouteOutage(start.Value, duration.Value));
                }
                catch (ArgumentException)
                {
                    // 不合法的中斷設定直接忽略，和 Kotlin 版的 runCatching 一樣。
                }
            }
        }

        var timingFlag = ParseStrictBoolean(DataValue(document, "useGpxTiming")) ?? true;
        var documentName = ChildText(document, "name");
        return new SimulationRoute(
            name: documentName.Length > 0 ? documentName : fallbackName,
            waypoints: waypoints,
            speedKph: Math.Clamp(speed, SimulationRoute.MinSpeedKph, SimulationRoute.MaxSpeedKph),
            loop: loop,
            useGpxTiming: timingFlag && SimulationRoute.HasTiming(waypoints),
            outages: outages,
            naturalSpeed: ParseStrictBoolean(DataValue(document, "naturalSpeed")) ?? false);
    }

    private static Coordinates ParseKmlCoordinate(string value)
    {
        var parts = value.Trim().Split(',');
        if (parts.Length < 2)
        {
            throw new ArgumentException("KML 座標格式不正確", nameof(value));
        }

        return new Coordinates(
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[0], CultureInfo.InvariantCulture));
    }

    private static string? DataValue(XElement element, string name) =>
        DescendantElements(element, "Data")
            .FirstOrDefault(data => data.Attribute("name")?.Value == name)
            ?.Descendants()
            .FirstOrDefault(descendant => descendant.Name.LocalName == "value")
            ?.Value
            .Trim();

    private static string ChildText(XElement element, string name) =>
        element.Elements()
            .FirstOrDefault(child => child.Name.LocalName == name)
            ?.Value
            .Trim() ?? string.Empty;

    private static IEnumerable<XElement> DescendantElements(XElement element, string name) =>
        element.Descendants().Where(descendant => descendant.Name.LocalName == name);

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? ParseStrictBoolean(string? value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    /// <summary>讓輸出和 Kotlin 的 Double.toString() 一致（整數值會帶 ".0"）。</summary>
    private static string FormatDouble(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E', StringComparison.Ordinal);
        if (exponentAt >= 0)
        {
            var mantissa = text[..exponentAt];
            if (!mantissa.Contains('.'))
            {
                mantissa += ".0";
            }

            var exponent = text[(exponentAt + 1)..];
            var sign = exponent.StartsWith('-') ? "-" : string.Empty;
            var digits = exponent.TrimStart('+', '-').TrimStart('0');
            if (digits.Length == 0)
            {
                digits = "0";
            }

            return mantissa + "E" + sign + digits;
        }

        return text.Contains('.') ? text : text + ".0";
    }

    private static string FormatInstant(long epochSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    /// <summary>先回放開頭讀過的位元組，再繼續讀原本的串流。</summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _head;
        private readonly int _headLength;
        private readonly Stream _inner;
        private int _headPosition;

        public PrefixedStream(byte[] head, int headLength, Stream inner)
        {
            _head = head;
            _headLength = headLength;
            _inner = inner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (_headPosition < _headLength)
            {
                var copied = Math.Min(count, _headLength - _headPosition);
                Array.Copy(_head, _headPosition, buffer, offset, copied);
                _headPosition += copied;
                return copied;
            }

            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_headPosition < _headLength)
            {
                var copied = Math.Min(buffer.Length, _headLength - _headPosition);
                _head.AsSpan(_headPosition, copied).CopyTo(buffer);
                _headPosition += copied;
                return copied;
            }

            return _inner.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
