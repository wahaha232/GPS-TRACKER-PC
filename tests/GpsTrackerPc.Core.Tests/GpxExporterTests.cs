using System.Globalization;
using GpsTrackerPc.Core.Gpx;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class GpxExporterTests
{
    [Fact]
    public void ExportMatchesRecordsDownloadGpxFormat()
    {
        const long start = 1_700_000_000_000L;
        var track = new GpxTrackDocument
        {
            StartTimeMs = start,
            Points = new List<double[]>
            {
                new double[] { 121.4737, 31.2304, 10.5, 1.2, 0 },
                new double[] { 121.4747, 31.2314, 12.0, 1.3, 10 },
            },
        };

        var gpx = GpxExporter.Export(track);
        var name = DateTimeOffset.FromUnixTimeMilliseconds(start)
            .ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
        var firstTime = DateTimeOffset.FromUnixTimeMilliseconds(start)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var secondTime = DateTimeOffset.FromUnixTimeMilliseconds(start + 10_000)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var expected =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<gpx version=\"1.1\" creator=\"GPS Tracker\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n" +
            $"<trk><name>{name}</name><trkseg>\n" +
            $"<trkpt lat=\"31.2304\" lon=\"121.4737\"><ele>10.5</ele><time>{firstTime}</time></trkpt>\n" +
            $"<trkpt lat=\"31.2314\" lon=\"121.4747\"><ele>12</ele><time>{secondTime}</time></trkpt>\n" +
            "</trkseg></trk></gpx>\n";

        Assert.Equal(expected, gpx);
        Assert.DoesNotContain("<metadata>", gpx);
    }

    [Fact]
    public void MissingOrZeroElevationBecomesZero()
    {
        var track = new GpxTrackDocument
        {
            StartTimeMs = 0,
            Points = new List<double[]>
            {
                new double[] { 121.0, 25.0 },
                new double[] { 121.1, 25.1, 0, 0, 60 },
            },
        };

        var gpx = GpxExporter.Export(track);

        Assert.Contains("<trkpt lat=\"25\" lon=\"121\"><ele>0</ele>", gpx);
        Assert.Contains("<trkpt lat=\"25.1\" lon=\"121.1\"><ele>0</ele>", gpx);
    }

    [Fact]
    public void ExportedGpxCanBeParsedBack()
    {
        var track = new GpxTrackDocument
        {
            StartTimeMs = 1_700_000_000_000L,
            Points = new List<double[]>
            {
                new double[] { 121.5, 25.0, 10, 0, 0 },
                new double[] { 121.5001, 25.0001, 11, 1, 10 },
                new double[] { 121.5002, 25.0002, 12, 1, 20 },
            },
        };

        var parsed = GpxTrackBuilder.Parse(GpxExporter.Export(track));

        Assert.Equal(3, parsed.Points.Count);
        Assert.Equal(25.0, parsed.Points[0].Lat);
        Assert.Equal(121.5, parsed.Points[0].Lon);
        Assert.Equal(10.0, parsed.Points[0].Elevation!.Value);
        Assert.Equal(
            (double)1_700_000_000_000L,
            parsed.Points[0].TimeMs);
    }
}
