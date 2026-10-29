using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class PlaybackMapStreamTests
{
    [Fact]
    public void UpdateIntervalIsWithinTheRequestedRange()
    {
        Assert.InRange(PlaybackMapPolicy.UpdateIntervalMilliseconds, 250, 500);
        Assert.Equal(1000, PlaybackMapPolicy.TrailSetDataIntervalMilliseconds);
    }

    [Fact]
    public void EightTimesSpeedThreeHoursStaysWithinFrameAndPointLimits()
    {
        // 8 倍速、每 0.5 秒更新一次 => 每次更新 4 個模擬秒；3 小時共 10800 個取樣。
        const int sampleCount = 10_800;
        const int samplesPerFrame = 4;
        var samples = Enumerable.Range(0, sampleCount)
            .Select(index => new PlaybackSample(
                new Coordinates(25.0, 121.0 + index * 0.000001),
                index,
                1f,
                IsInOutage: false))
            .ToList();
        var stream = new PlaybackMapStream();

        var frames = 0;
        var maxPointsInFrame = 0;
        for (var index = 0; index < samples.Count; index += samplesPerFrame)
        {
            for (var offset = 0; offset < samplesPerFrame && index + offset < samples.Count; offset++)
            {
                stream.Append(samples[index + offset]);
            }

            var frame = stream.BuildFrame();
            frames++;
            var points = frame.ReplaceAll
                ? frame.Segments.Sum(segment => segment.Points.Count)
                : frame.Samples.Count;
            maxPointsInFrame = Math.Max(maxPointsInFrame, points);
            Assert.True(
                stream.DisplayPointCount <= PlaybackMapPolicy.MaxDisplayPoints,
                $"第 {frames} 次更新後顯示點數 {stream.DisplayPointCount}");
        }

        Assert.Equal(2_700, frames);
        Assert.True(maxPointsInFrame <= PlaybackMapPolicy.MaxDisplayPoints);
        Assert.Equal(sampleCount, samples.Count); // 原始取樣資料沒有被抽稀
    }

    [Fact]
    public void DecimationOnlyChangesTheDisplayTrail()
    {
        var samples = Enumerable.Range(0, 3_000)
            .Select(index => new PlaybackSample(
                new Coordinates(25.0, 121.0 + index * 0.000001),
                index,
                1f,
                IsInOutage: false))
            .ToList();
        var stream = new PlaybackMapStream();
        foreach (var sample in samples)
        {
            stream.Append(sample);
        }

        var frame = stream.BuildFrame(forceReplace: true);

        Assert.True(frame.ReplaceAll);
        var displayed = frame.Segments.SelectMany(segment => segment.Points).ToList();
        Assert.InRange(displayed.Count, 2, PlaybackMapPolicy.MaxDisplayPoints);
        Assert.Equal(0.0, displayed[0].ElapsedSeconds);
        Assert.Equal(2_999.0, displayed[^1].ElapsedSeconds);
        Assert.Equal(3_000, samples.Count);
    }

    [Fact]
    public void SamplesFromOneUpdateAreMergedIntoOneFrame()
    {
        var stream = new PlaybackMapStream();
        stream.Append(Sample(0));
        stream.Append(Sample(1));
        stream.Append(Sample(2));

        var first = stream.BuildFrame();

        Assert.False(first.ReplaceAll);
        Assert.Equal(3, first.Samples.Count);
        Assert.Empty(first.Segments);

        stream.Append(Sample(3));
        var second = stream.BuildFrame();
        Assert.Single(second.Samples);
    }

    [Fact]
    public void StayingAtTheSamePositionDoesNotAddRepeatedTrailPoints()
    {
        var stream = new PlaybackMapStream();
        var position = new Coordinates(25.0, 121.5);

        stream.Append(new PlaybackSample(position, 0, 1f, IsInOutage: false));
        stream.Append(new PlaybackSample(position, 1, 1f, IsInOutage: false));
        stream.Append(new PlaybackSample(position, 2, 1f, IsInOutage: false));

        var frame = stream.BuildFrame();

        Assert.Single(frame.Samples);
        Assert.Equal(1, stream.DisplayPointCount);
    }

    [Fact]
    public void GapsBecomeSeparateDisplaySegments()
    {
        var stream = new PlaybackMapStream();
        stream.Append(Sample(0));
        stream.Append(Sample(1));
        stream.Append(Sample(20));
        stream.Append(Sample(21));

        var frame = stream.BuildFrame(forceReplace: true);

        Assert.Equal(2, frame.Segments.Count);
        Assert.Equal(2, frame.Segments[0].Points.Count);
        Assert.Equal(2, frame.Segments[1].Points.Count);
    }

    [Fact]
    public void LongCurvedPlaybackNeverDrawsShortcutLines()
    {
        // 半徑 2 公里的圓、每秒約 3 公尺，播放 2 小時。舊版每次抽稀都拿上一次的結果再減半，
        // 最舊的段落最後只剩幾個點，畫出近 3 公里的直線（橫跨整個圓）。
        var center = new Coordinates(24.93, 121.39);
        var stream = new PlaybackMapStream();
        var compactions = 0;
        for (var second = 0; second < 7_200; second++)
        {
            stream.Append(new PlaybackSample(
                GeoMath.Destination(center, 2_000, second * 0.09),
                second,
                3f,
                IsInOutage: false));
            var frame = stream.BuildFrame();
            if (!frame.ReplaceAll)
            {
                continue;
            }

            compactions++;
            foreach (var segment in frame.Segments)
            {
                for (var index = 1; index < segment.Points.Count; index++)
                {
                    var gap = GeoMath.DistanceMeters(
                        segment.Points[index - 1].Coordinates,
                        segment.Points[index].Coordinates);
                    // 7000 點抽到 600 點，間隔最多 12 點 ≈ 38 公尺。
                    Assert.True(gap < 60, $"第 {second} 秒抽稀後出現 {gap:0} 公尺的直線");
                }
            }

            Assert.InRange(stream.DisplayPointCount, 2, PlaybackMapPolicy.MaxDisplayPoints);
        }

        Assert.True(compactions > 5);
    }

    [Fact]
    public void CompactionKeepsTheTrailConnectedToTheRecentPart()
    {
        var stream = new PlaybackMapStream();
        for (var second = 0; second <= PlaybackMapPolicy.MaxDisplayPoints; second++)
        {
            stream.Append(Sample(second));
        }

        var frame = stream.BuildFrame();

        // 同一段連續播放，抽稀後仍是一條線，最新的點完整保留。
        Assert.True(frame.ReplaceAll);
        var segment = Assert.Single(frame.Segments);
        Assert.Equal(0.0, segment.Points[0].ElapsedSeconds);
        Assert.Equal(PlaybackMapPolicy.MaxDisplayPoints, segment.Points[^1].ElapsedSeconds);
        var recent = segment.Points.TakeLast(PlaybackMapPolicy.RecentPointsToKeep).ToList();
        Assert.All(
            recent.Zip(recent.Skip(1)),
            pair => Assert.Equal(pair.First.ElapsedSeconds + 1, pair.Second.ElapsedSeconds));
    }

    [Fact]
    public void ResentEarlierSamplesDoNotDrawALineBackToTheStart()
    {
        // 重啟續傳時若把已顯示的取樣再送一次，舊版會從暫停點畫直線回到起點。
        var stream = new PlaybackMapStream();
        for (var second = 0; second < 10; second++)
        {
            stream.Append(Sample(second));
        }

        stream.BuildFrame(forceReplace: true);
        for (var second = 0; second < 10; second++)
        {
            stream.Append(Sample(second));
        }

        stream.Append(Sample(10));
        var frame = stream.BuildFrame();

        Assert.False(frame.ReplaceAll);
        var appended = Assert.Single(frame.Samples);
        Assert.Equal(10.0, appended.ElapsedSeconds);
        Assert.Equal(11, stream.DisplayPointCount);
    }

    private static PlaybackSample Sample(int second) =>
        new(
            new Coordinates(25.0, 121.0 + second * 0.000001),
            second,
            1f,
            IsInOutage: false);
}
