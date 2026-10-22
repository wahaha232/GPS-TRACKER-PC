using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
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

    private static PlaybackSample Sample(int second) =>
        new(
            new Coordinates(25.0, 121.0 + second * 0.000001),
            second,
            1f,
            IsInOutage: false);
}
