using System.Globalization;
using GpsTrackerPc.Core.Gpx;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

/// <summary>對照 gpstracker-web/tests/gpx.test.js 的案例（期望值維持不變）。</summary>
public class GpxTrackBuilderTests
{
    private const double Mpd = 111_194.93;

    [Fact]
    public void ParseHandlesAttributeOrderQuotesSelfClosingNamespacesAndSegments()
    {
        const string xml = """
            <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><name>晨跑 &amp; 散步</name>
            <trkseg><trkpt lon='121.5' lat='25.0'><ele>10.5</ele><time>2026-10-10T08:00:00Z</time></trkpt><trkpt lat="25.001" lon="121.5"/></trkseg>
            <trkseg><trkpt lat="25.002" lon="121.5"><time>2026-10-10T08:00:20Z</time></trkpt></trkseg></trk></gpx>
            """;

        var parsed = GpxTrackBuilder.Parse(xml);

        Assert.Equal("晨跑 & 散步", parsed.Name);
        Assert.Equal(3, parsed.Points.Count);
        Assert.Equal(25.0, parsed.Points[0].Lat);
        Assert.Equal(121.5, parsed.Points[0].Lon);
        Assert.Equal(10.5, parsed.Points[0].Elevation!.Value);
        Assert.True(double.IsNaN(parsed.Points[1].TimeMs));
        Assert.Null(parsed.Points[1].Elevation);
        Assert.Equal(
            (double)new DateTimeOffset(2026, 10, 10, 8, 0, 20, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            parsed.Points[2].TimeMs);
        Assert.Empty(GpxTrackBuilder.Parse("").Points);
        Assert.Empty(GpxTrackBuilder.Parse("""<gpx><trkpt lat="999" lon="0"></trkpt></gpx>""").Points);
    }

    [Fact]
    public void BuildCalculatesDistanceTimeSpeedElevationAndPointFormat()
    {
        var parsed = GpxTrackBuilder.Parse(Gpx(Walk(300, 3, elevation: seconds => 20 + seconds * 0.1)));
        var result = GpxTrackBuilder.Build(parsed, new GpxBuildOptions { Id = "abc", Mode = "BICYCLE" });

        Assert.True(result.IsSuccess);
        var track = result.Track!;
        var entry = result.Entry!;
        Assert.Equal("abc", track.TrackId);
        Assert.Equal("BICYCLE", track.DominantMode);
        Assert.Equal("gpx-import", track.Source);
        TestAssert.Close(900, track.DistanceMeters, 1);
        Assert.Equal(300_000L, track.DurationMs);
        TestAssert.Close(3, track.AvgSpeedMps, 0.01);
        TestAssert.Close(3, track.MaxSpeedMps, 0.05);
        TestAssert.Close(30, track.TotalAscentMeters, 3);
        Assert.Equal(0.0, track.TotalDescentMeters);
        Assert.Equal(50.0, track.MaxAltitudeMeters!.Value);
        Assert.Equal(20.0, track.MinAltitudeMeters!.Value);
        Assert.Equal(301, track.Points.Count);
        var point = track.Points[100];
        Assert.Equal(5, point.Length);
        Assert.Equal(100.0, point[4]);
        TestAssert.Close(10.8, point[3], 0.2);
        Assert.Equal("BICYCLE", (string)track.Segments[0][0]);
        Assert.Equal(0L, Convert.ToInt64(track.Segments[0][1], CultureInfo.InvariantCulture));
        Assert.Equal(300L, Convert.ToInt64(track.Segments[0][2], CultureInfo.InvariantCulture));
        Assert.Equal("abc.json", entry.File);
        Assert.Equal("abc", entry.TrackId);
        Assert.Equal("BICYCLE", entry.DominantMode);
        Assert.Equal(301, track.GpsPointCount);
        Assert.Equal("UNKNOWN", GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(Walk(60, 2))),
            new GpxBuildOptions()).Track!.DominantMode);
    }

    [Fact]
    public void MissingElevationIsNotInvented()
    {
        var track = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(Walk(60, 2))),
            new GpxBuildOptions { Id = "n" }).Track!;

        Assert.Null(track.MaxAltitudeMeters);
        Assert.Null(track.MinAltitudeMeters);
        Assert.Equal(0.0, track.TotalAscentMeters);
    }

    [Fact]
    public void PausesAndGpsJumpsAreExcludedFromDistanceAndTime()
    {
        var start = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
        var first = Walk(100, 3);
        var second = Walk(100, 3)
            .Select((point, seconds) => (
                Lat: point.Lat + 500 / Mpd,
                point.Lon,
                point.Ele,
                T: (string?)start.AddSeconds(100 + 600 + seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))
            .ToArray();

        var track = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(first.Concat(second))),
            new GpxBuildOptions { Id = "g" }).Track!;

        Assert.InRange(track.DurationMs, 195_000L, 205_000L);
        Assert.Equal(800L, Convert.ToInt64(track.Points[^1][4], CultureInfo.InvariantCulture));

        var jumpFirst = Walk(50, 3);
        var jumpSecond = Walk(50, 3)
            .Select((point, seconds) => (
                Lat: point.Lat + 200 / Mpd * 1000,
                point.Lon,
                point.Ele,
                T: (string?)start.AddSeconds(51 + seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))
            .ToArray();
        var jump = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(jumpFirst.Concat(jumpSecond))),
            new GpxBuildOptions { Id = "j" }).Track!;

        Assert.True(jump.DistanceMeters < 400, "200 km 的跳點沒有被算進距離");
        Assert.True(jump.MaxSpeedMps * 3.6 < 400);
    }

    [Fact]
    public void ErrorsHaveChineseMessages()
    {
        Assert.Equal("no-points", GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse("<gpx></gpx>"),
            new GpxBuildOptions()).Error);

        var noTime = Walk(10, 2)
            .Select(point => (point.Lat, point.Lon, point.Ele, T: (string?)null))
            .ToArray();
        Assert.Equal("no-time", GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(noTime)),
            new GpxBuildOptions()).Error);

        var oneTimed = noTime
            .Select((point, index) => index == 0
                ? (point.Lat, point.Lon, point.Ele, T: (string?)"2026-10-10T08:00:00Z")
                : point)
            .ToArray();
        Assert.Equal("too-few", GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(oneTimed)),
            new GpxBuildOptions()).Error);

        foreach (var code in new[] { "no-points", "no-time", "too-few" })
        {
            Assert.True(GpxErrors.Get(code).Length > 5);
        }
    }

    [Fact]
    public void OutOfOrderAndDuplicateTimesAreSortedAndDeduplicated()
    {
        var points = Walk(30, 2);
        var shuffled = new[] { points[5], points[0], points[30], points[10], points[10], points[20] };

        var track = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(shuffled)),
            new GpxBuildOptions { Id = "s" }).Track!;

        Assert.Equal(new double[] { 0, 5, 10, 20, 30 }, track.Points.Select(point => point[4]));
        Assert.True(track.DistanceMeters > 0);
    }

    [Fact]
    public void MoreThanMaxPointsAreEvenlySampledKeepingTheEnds()
    {
        var track = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(Walk(25_000, 0.5))),
            new GpxBuildOptions { Id = "b" }).Track!;

        Assert.Equal(GpxTrackBuilder.MaxPoints, track.Points.Count);
        Assert.Equal(0.0, track.Points[0][4]);
        Assert.Equal(25_000.0, track.Points[^1][4]);
    }

    [Fact]
    public void DuplicateDetectionNeedsCloseStartTimeAndDistance()
    {
        var entry = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(Walk(300, 3))),
            new GpxBuildOptions { Id = "d" }).Entry!;

        Assert.True(GpxTrackBuilder.IsDuplicate(
            new[] { new TrackIdentity(entry.StartTimeMs + 2000, entry.DistanceMeters + 5) },
            entry));
        Assert.False(GpxTrackBuilder.IsDuplicate(
            new[] { new TrackIdentity(entry.StartTimeMs + 60_000, entry.DistanceMeters) },
            entry));
        Assert.False(GpxTrackBuilder.IsDuplicate(
            new[] { new TrackIdentity(entry.StartTimeMs, entry.DistanceMeters * 2) },
            entry));
        Assert.False(GpxTrackBuilder.IsDuplicate(Array.Empty<TrackIdentity>(), entry));
        Assert.False(GpxTrackBuilder.IsDuplicate(null, entry));
    }

    [Fact]
    public void ExportedGpxCanBeImportedBack()
    {
        // 網頁版用實際資料檔；桌面版測試不依賴外部資料夾，改用同樣規則產生的軌跡。
        var source = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(Gpx(Walk(1000, 1))),
            new GpxBuildOptions { Id = "rt" }).Track!;
        var gpx = GpxExporter.Export(new GpxTrackDocument
        {
            StartTimeMs = source.StartTimeMs,
            Points = source.Points,
        });

        var back = GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(gpx),
            new GpxBuildOptions { Id = "rt2" });

        Assert.True(back.IsSuccess);
        Assert.True(back.Track!.Points.Count <= source.Points.Count);
        Assert.True(back.Track.Points.Count >= source.Points.Count * 0.9);
        var sourceSpan = (source.Points[^1][4] - source.Points[0][4]) * 1000;
        var backSpan = back.Track.EndTimeMs - back.Track.StartTimeMs;
        Assert.True(Math.Abs(backSpan - sourceSpan) < 2000);
        Assert.True(back.Track.DistanceMeters > 0);
        Assert.True(back.Track.DistanceMeters < source.DistanceMeters * 1.5 + 100);
    }

    private static (double Lat, double Lon, double? Ele, string? T)[] Walk(
        int seconds,
        double metersPerSecond,
        Func<int, double?>? elevation = null)
    {
        var start = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, seconds + 1)
            .Select(second => (
                Lat: 25 + second * metersPerSecond / Mpd,
                Lon: 121.5,
                Ele: elevation?.Invoke(second),
                T: (string?)start.AddSeconds(second).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))
            .ToArray();
    }

    private static string Gpx(
        IEnumerable<(double Lat, double Lon, double? Ele, string? T)> points,
        string? name = null)
    {
        var body = string.Join(
            "\n",
            points.Select(point =>
                $"<trkpt lat=\"{point.Lat.ToString("R", CultureInfo.InvariantCulture)}\" " +
                $"lon=\"{point.Lon.ToString("R", CultureInfo.InvariantCulture)}\">" +
                (point.Ele.HasValue
                    ? $"<ele>{point.Ele.Value.ToString("R", CultureInfo.InvariantCulture)}</ele>"
                    : string.Empty) +
                (point.T != null ? $"<time>{point.T}</time>" : string.Empty) +
                "</trkpt>"));
        var nameTag = name == null ? string.Empty : $"<name>{name}</name>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><gpx version=\"1.1\" creator=\"x\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk>{nameTag}<trkseg>{body}</trkseg></trk></gpx>";
    }
}
