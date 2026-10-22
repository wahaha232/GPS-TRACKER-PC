using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Gpx;

/// <summary>
/// 把第 6 階段的播放取樣轉成網站紀錄。統計仍交給 GpxTrackBuilder，
/// 所以距離、時間、速度、爬升與 GPX 匯入規則完全一致；中斷中的取樣不寫入紀錄。
/// </summary>
public static class SimulatedRecordBuilder
{
    public static GpxBuildResult Build(
        PlaybackResult playback,
        DateTimeOffset startTime,
        string deviceName,
        string? id = null,
        string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(playback);
        var normalizedDeviceName = DeviceNameStore.Validate(deviceName);

        var samples = playback.Samples
            .Where(sample =>
                !sample.IsInOutage &&
                double.IsFinite(sample.Coordinates.Latitude) &&
                double.IsFinite(sample.Coordinates.Longitude))
            .ToList();
        if (samples.Count < 2)
        {
            return GpxBuildResult.Fail(samples.Count == 0 ? "no-points" : "too-few");
        }

        var startMs = startTime.ToUnixTimeMilliseconds();
        var firstElapsed = samples[0].ElapsedSeconds;
        var document = new GpxDocument(
            playback.Route.Name,
            samples
                .Select(sample => new GpxPoint(
                    sample.Coordinates.Latitude,
                    sample.Coordinates.Longitude,
                    Elevation: null,
                    TimeMs: startMs + sample.ElapsedSeconds * 1000.0))
                .ToList());

        var built = GpxTrackBuilder.Build(
            document,
            new GpxBuildOptions
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id,
                Mode = string.IsNullOrWhiteSpace(mode) ? "UNKNOWN" : mode,
            });
        if (!built.IsSuccess)
        {
            return built;
        }

        var track = built.Track!;
        var entry = built.Entry!;

        // 如果中斷從第 0 秒開始，第一個紀錄點會在幾秒後才出現；
        // 取樣點的時間仍要相對「使用者選的開始時間」計算，所以把偏移補回去。
        var shiftSeconds = (long)Math.Round(firstElapsed);
        if (shiftSeconds > 0)
        {
            foreach (var point in track.Points)
            {
                if (point.Length > 4)
                {
                    point[4] += shiftSeconds;
                }
            }

            foreach (var segment in track.Segments)
            {
                if (segment.Length >= 3)
                {
                    segment[1] = Convert.ToInt64(segment[1]) + shiftSeconds;
                    segment[2] = Convert.ToInt64(segment[2]) + shiftSeconds;
                }
            }
        }

        track.StartTimeMs = startMs;
        track.DeviceName = normalizedDeviceName;
        track.Source = "simulated";
        entry.StartTimeMs = startMs;
        entry.DeviceName = normalizedDeviceName;
        entry.Source = "simulated";

        var summary = new GpxImportSummary(
            playback.Route.Name,
            track.GpsPointCount,
            SkippedNoTime: 0,
            StartTimeMs: startMs,
            track.DistanceMeters,
            track.DurationMs,
            HasAltitude: false);

        return new GpxBuildResult(track, entry, summary, null);
    }
}
