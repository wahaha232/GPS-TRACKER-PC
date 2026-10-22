using System.Globalization;
using System.Text;
using GpsTrackerPc.Simulation.Routes;
using GpsTrackerPc.Simulation.Util;
using Xunit;

namespace GpsTrackerPc.Simulation.Tests;

public class GpxStreamImporterTests
{
    private static string Gpx(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">{body}</gpx>""";

    private static GpxImportResult Import(string xml, Func<bool>? cancelled = null) =>
        GpxStreamImporter.Import(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)),
            "fallback",
            isCancelled: cancelled);

    /// <summary>沿著東西向一直線、每秒一點的軌跡（含海拔與擴充資料，讓每點接近真實檔案的大小）。</summary>
    private static string BigTrack(int points, bool withTime = true, int skipTimeAt = -1)
    {
        var start = DateTimeOffset.Parse(
            "2026-10-15T00:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();
        var builder = new StringBuilder(points * 150);
        builder.Append("""<?xml version="1.0" encoding="UTF-8"?><gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">""");
        builder.Append("<trk><name>big</name><trkseg>");
        for (var index = 0; index < points; index++)
        {
            var lon = 121.0 + index * 0.00001;
            builder.Append("<trkpt lat=\"25.000000\" lon=\"")
                .Append(lon.ToString("F6", CultureInfo.InvariantCulture))
                .Append("\">");
            builder.Append("<ele>12.5</ele>");
            if (withTime && index != skipTimeAt)
            {
                builder.Append("<time>")
                    .Append(DateTimeOffset.FromUnixTimeSeconds(start + index)
                        .UtcDateTime
                        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    .Append("</time>");
            }

            builder.Append("<extensions><speed>1.4</speed></extensions></trkpt>\n");
        }

        builder.Append("</trkseg></trk></gpx>");
        return builder.ToString();
    }

    [Fact]
    public void SmallGpxIsImportedUntouched()
    {
        var result = Import(
            Gpx(
                """
                <trk><name>t</name><trkseg>
                <trkpt lat="25.0" lon="121.0"><time>2026-10-15T06:00:00Z</time></trkpt>
                <trkpt lat="25.0" lon="121.001"><time>2026-10-15T06:01:40Z</time></trkpt>
                <trkpt lat="25.0" lon="121.002"><time>2026-10-15T06:02:30.500+00:00</time></trkpt>
                </trkseg></trk>
                """));

        Assert.Equal(3, (int)result.OriginalPointCount);
        Assert.False(result.WasSimplified);
        Assert.Equal("t", result.Route.Name);
        Assert.True(result.Route.UseGpxTiming);
        Assert.Equal(new long?[] { 0L, 100L, 150L }, result.Route.Waypoints.Select(waypoint => waypoint.TimeOffsetSeconds));
    }

    [Fact]
    public void AGpxOver10MbWith150000PointsImportsAndIsSimplified()
    {
        var xml = BigTrack(150_000);
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.True(bytes.Length > BoundedIo.RouteDocumentMaxBytes, $"測試檔案應該超過 10 MB：{bytes.Length}");
        var result = GpxStreamImporter.Import(new MemoryStream(bytes), "fallback");
        Assert.Equal(150_000L, result.OriginalPointCount);
        Assert.True(result.WasSimplified);
        Assert.True(
            result.KeptPointCount is >= 2 and <= GpxStreamImporter.MaxPoints,
            $"留下的點數：{result.KeptPointCount}");
        var waypoints = result.Route.Waypoints;
        // 首尾一定保留
        TestAssert.Close(121.0, waypoints[0].Coordinates.Longitude, 1e-9);
        TestAssert.Close(121.0 + 149_999 * 0.00001, waypoints[^1].Coordinates.Longitude, 2e-6);
        // 時間保留：依時間播放、不倒退、最後一點是原本的最後一秒
        Assert.True(result.Route.UseGpxTiming);
        Assert.Equal(0L, waypoints[0].TimeOffsetSeconds);
        Assert.Equal(149_999L, waypoints[^1].TimeOffsetSeconds);
        Assert.All(
            waypoints.Zip(waypoints.Skip(1), (first, second) => (First: first, Second: second)),
            pair => Assert.True(pair.Second.TimeOffsetSeconds!.Value >= pair.First.TimeOffsetSeconds!.Value));
        // 抽稀是均勻的：相鄰兩點的時間差不會差太多（最大不超過平均的 2 倍多一點）
        var gaps = waypoints
            .Zip(waypoints.Skip(1), (first, second) => second.TimeOffsetSeconds!.Value - first.TimeOffsetSeconds!.Value)
            .ToArray();
        var average = 149_999.0 / (waypoints.Count - 1);
        Assert.True(gaps.Max() <= average * 2 + 1, $"最大間隔 {gaps.Max()}，平均 {average}");
    }

    [Fact]
    public void AMissingTimeStampAnywhereTurnsTimingOff()
    {
        // 第 12345 個點沒有時間：就算它被抽稀掉，也不能假裝整條路線都有時間
        var result = Import(BigTrack(20_000, skipTimeAt: 12_345));
        Assert.False(result.Route.UseGpxTiming);
        Assert.All(result.Route.Waypoints, waypoint => Assert.Null(waypoint.TimeOffsetSeconds));
        Assert.True(result.KeptPointCount <= GpxStreamImporter.MaxPoints);
    }

    [Fact]
    public void GpxWithoutAnyTimeStampsStaysFixedSpeed()
    {
        var result = Import(BigTrack(5_000, withTime: false));
        Assert.False(result.Route.UseGpxTiming);
        Assert.Equal(5_000L, result.OriginalPointCount);
    }

    [Fact]
    public void OnlyTheFirstTrackIsUsed()
    {
        var result = Import(
            Gpx(
                """
                <trk><name>one</name><trkseg><trkpt lat="1" lon="1"/><trkpt lat="2" lon="2"/></trkseg></trk>
                <trk><name>two</name><trkseg><trkpt lat="5" lon="5"/><trkpt lat="6" lon="6"/><trkpt lat="7" lon="7"/></trkseg></trk>
                """));

        Assert.Equal("one", result.Route.Name);
        Assert.Equal(2, result.Route.Waypoints.Count);
    }

    [Fact]
    public void SegmentsOfOneTrackAreMerged()
    {
        var result = Import(
            Gpx(
                """
                <trk><trkseg><trkpt lat="1" lon="1"/><trkpt lat="2" lon="2"/></trkseg>
                <trkseg><trkpt lat="3" lon="3"/><trkpt lat="4" lon="4"/></trkseg></trk>
                """));

        Assert.Equal(4, result.Route.Waypoints.Count);
        Assert.Equal("fallback", result.Route.Name);
    }

    [Fact]
    public void RouteAndWaypointFallBacks()
    {
        var route = Import(Gpx("""<rte><name>r</name><rtept lat="1" lon="1"/><rtept lat="2" lon="2"/></rte>"""));
        Assert.Equal("r", route.Route.Name);
        Assert.Equal(2, route.Route.Waypoints.Count);
        var waypoints = Import(Gpx("""<wpt lat="1" lon="1"><name>a</name></wpt><wpt lat="2" lon="2"><name>b</name></wpt>"""));
        Assert.Equal(new[] { "a", "b" }, waypoints.Route.Waypoints.Select(waypoint => waypoint.Name));
    }

    [Fact]
    public void PointNamesStopSecondsAndExtensionsAreRead()
    {
        var result = Import(
            Gpx(
                """
                <trk><name>x</name><extensions><speedKph>12.5</speedKph><loop>true</loop></extensions><trkseg>
                <trkpt lat="1" lon="1"><name>start</name><extensions><stopSeconds>30</stopSeconds></extensions></trkpt>
                <trkpt lat="2" lon="2"/></trkseg></trk>
                """));

        TestAssert.Close(12.5, result.Route.SpeedKph, 1e-9);
        Assert.True(result.Route.Loop);
        Assert.Equal("start", result.Route.Waypoints[0].Name);
        Assert.Equal(30L, result.Route.Waypoints[0].StopDurationSeconds);
        Assert.Equal("Point 2", result.Route.Waypoints[1].Name);
    }

    [Fact]
    public void DoctypeAndEntitiesAreRejected()
    {
        const string evil = """
            <?xml version="1.0"?><!DOCTYPE gpx [<!ENTITY x "boom">]><gpx><trk><name>&x;</name>
            <trkseg><trkpt lat="1" lon="1"/><trkpt lat="2" lon="2"/></trkseg></trk></gpx>
            """;
        var error = Record.Exception(() => Import(evil));
        Assert.NotNull(error);
    }

    [Fact]
    public void InvalidInputIsRejected()
    {
        Assert.NotNull(Record.Exception(() => Import("not xml at all")));
        Assert.NotNull(Record.Exception(() =>
            Import(Gpx("""<trk><trkseg><trkpt lat="99" lon="1"/><trkpt lat="2" lon="2"/></trkseg></trk>"""))));
        Assert.NotNull(Record.Exception(() =>
            Import(Gpx("""<trk><trkseg><trkpt lat="1" lon="1"/></trkseg></trk>"""))));
    }

    [Fact]
    public void ImportCanBeCancelled()
    {
        var checks = 0;
        var error = Record.Exception(() => Import(BigTrack(50_000), () => ++checks > 3));
        Assert.IsType<ImportCancelledException>(error);
    }

    [Fact]
    public void ImportStreamPicksTheGpxPathAndFallsBackToDocumentsForOtherFormats()
    {
        var gpxBytes = Encoding.UTF8.GetBytes(BigTrack(4_000));
        var fromStream = LocationDocumentCodec.ImportStream(new MemoryStream(gpxBytes), "fallback");
        Assert.Equal(4_000L, fromStream.OriginalPointCount);

        const string kml = """
            <?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>k</name><Placemark>
            <LineString><coordinates>121.0,25.0,0 121.1,25.1,0</coordinates></LineString></Placemark></Document></kml>
            """;
        var kmlResult = LocationDocumentCodec.ImportStream(
            new MemoryStream(Encoding.UTF8.GetBytes(kml)),
            "fallback");
        Assert.Equal(2, kmlResult.KeptPointCount);
    }

    [Fact]
    public void HugeKmlIsSimplifiedToo()
    {
        var coordinates = string.Join(
            " ",
            Enumerable.Range(0, 12_000)
                .Select(index => $"{(121.0 + index * 0.0001).ToString("R", CultureInfo.InvariantCulture)},25.0,0"));
        var kml = $"""
            <?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
            <LineString><coordinates>{coordinates}</coordinates></LineString></Placemark></Document></kml>
            """;
        var route = LocationDocumentCodec.Import(kml);
        Assert.True(route.Waypoints.Count <= GpxStreamImporter.MaxPoints);
        TestAssert.Close(121.0, route.Waypoints[0].Coordinates.Longitude, 1e-9);
        TestAssert.Close(121.0 + 11_999 * 0.0001, route.Waypoints[^1].Coordinates.Longitude, 1e-9);
    }

    [Fact]
    public void StreamSizeCapStillProtectsAgainstAbsurdFiles()
    {
        var endless = BoundedIo.Limit(new EndlessStream(), 1_000_000L);
        var error = Record.Exception(() => endless.CopyTo(Stream.Null));
        Assert.IsType<IOException>(error);
    }

    // ---- 抽樣器 ----
    [Fact]
    public void ReservoirRespectsItsCapacityKeepsTheEndsAndStaysUniform()
    {
        foreach (var size in new[] { 1, 2, 3, 2_999, 3_000, 3_001, 5_999, 6_000, 6_001, 100_000 })
        {
            var reservoir = new DecimatingReservoir<int>(3_000);
            for (var index = 0; index < size; index++)
            {
                reservoir.Add(index);
            }

            var result = reservoir.Result();
            Assert.Equal((long)size, reservoir.Count);
            Assert.True(result.Count is <= 3_000 and > 0, $"size={size} 結果 {result.Count}");
            Assert.Equal(0, result[0]);
            Assert.Equal(size - 1, result[^1]);
            Assert.Equal(result.OrderBy(value => value).ToList(), result);
            Assert.Equal(result.Distinct().ToList(), result);
        }
    }

    [Fact]
    public void ReservoirMemoryStaysBoundedWhileStreaming()
    {
        var reservoir = new DecimatingReservoir<int>(100);
        for (var index = 0; index < 1_000_000; index++)
        {
            reservoir.Add(index);
        }

        var result = reservoir.Result();
        Assert.True(result.Count <= 100);
        var gaps = result
            .Zip(result.Skip(1), (first, second) => second - first)
            .ToArray();
        Assert.True(gaps.Max() <= 999_999 / 99 * 2 + 2, $"最大間隔 {gaps.Max()}");
    }

    private sealed class EndlessStream : Stream
    {
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
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }

        public override int Read(Span<byte> buffer)
        {
            buffer.Fill((byte)'x');
            return buffer.Length;
        }

        public override int ReadByte() => 'x';

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
