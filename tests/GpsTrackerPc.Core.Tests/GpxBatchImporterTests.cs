using GpsTrackerPc.Core.Gpx;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class GpxBatchImporterTests
{
    [Fact]
    public async Task FailuresAreIsolatedAndTheBatchContinues()
    {
        var a = Build("a");
        var b = Build("b");
        var c = Build("c");
        var uploader = new FakeUploader(
            build => UploadResult.Uploaded(build.Entry!.TrackId),
            _ => throw new HttpRequestException("模擬網路失敗"),
            build => UploadResult.Duplicate(build.Entry!.TrackId));
        var batch = new GpxImportBatch(uploader);
        var progress = new RecordingProgress();

        var results = await batch.ImportAsync(
            new[]
            {
                new GpxImportCandidate("a.gpx", a),
                new GpxImportCandidate("b.gpx", b),
                new GpxImportCandidate("c.gpx", c),
                new GpxImportCandidate("d.gpx", GpxBuildResult.Fail("no-time")),
            },
            progress);

        Assert.Equal(GpxImportItemStatus.Uploaded, results[0].Status);
        Assert.Equal(GpxImportItemStatus.Failed, results[1].Status);
        Assert.Contains("網路失敗", results[1].Message);
        Assert.Equal(GpxImportItemStatus.Duplicate, results[2].Status);
        Assert.Equal(GpxImportItemStatus.ParseError, results[3].Status);
        Assert.Contains("時間", results[3].Message);

        Assert.Equal(new[] { "a", "b", "c" }, uploader.Calls);
        Assert.Equal(new[] { 0, 1, 2, 3 }, progress.Indices);
    }

    [Fact]
    public async Task EmptyBatchReturnsNoResults()
    {
        var batch = new GpxImportBatch(new FakeUploader());

        var results = await batch.ImportAsync(Array.Empty<GpxImportCandidate>());

        Assert.Empty(results);
    }

    [Fact]
    public async Task UploadResultFailureIsReportedWithoutThrowing()
    {
        var batch = new GpxImportBatch(new FakeUploader(
            _ => UploadResult.Failed("GitHub API 500")));

        var results = await batch.ImportAsync(new[]
        {
            new GpxImportCandidate("a.gpx", Build("a")),
        });

        Assert.Single(results);
        Assert.Equal(GpxImportItemStatus.Failed, results[0].Status);
        Assert.Equal("GitHub API 500", results[0].Message);
    }

    [Fact]
    public async Task CancellationStopsFurtherUploads()
    {
        using var cancellation = new CancellationTokenSource();
        var uploader = new CancellingUploader(cancellation);
        var batch = new GpxImportBatch(uploader);
        var progress = new RecordingProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            batch.ImportAsync(
                new[]
                {
                    new GpxImportCandidate("a.gpx", Build("a")),
                    new GpxImportCandidate("b.gpx", Build("b")),
                },
                progress,
                cancellation.Token));

        Assert.Equal(new[] { "a" }, uploader.Calls);
        Assert.Equal(new[] { 0 }, progress.Indices);
    }

    private static GpxBuildResult Build(string id)
    {
        const string xml =
            "<gpx><trk><trkseg>" +
            "<trkpt lat=\"25.0\" lon=\"121.5\"><time>2026-10-10T08:00:00Z</time></trkpt>" +
            "<trkpt lat=\"25.0001\" lon=\"121.5\"><time>2026-10-10T08:00:10Z</time></trkpt>" +
            "</trkseg></trk></gpx>";
        return GpxTrackBuilder.Build(
            GpxTrackBuilder.Parse(xml),
            new GpxBuildOptions { Id = id, Mode = "UNKNOWN" });
    }

    private sealed class FakeUploader : IRecordUploader
    {
        private readonly Queue<Func<GpxBuildResult, UploadResult>> _responses;

        public FakeUploader(params Func<GpxBuildResult, UploadResult>[] responses)
        {
            _responses = new Queue<Func<GpxBuildResult, UploadResult>>(responses);
        }

        public List<string> Calls { get; } = new();

        public Task<UploadResult> UploadAsync(
            GpxBuildResult build,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(build.Entry!.TrackId);
            var response = _responses.Dequeue();
            return Task.FromResult(response(build));
        }
    }

    private sealed class CancellingUploader : IRecordUploader
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingUploader(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public List<string> Calls { get; } = new();

        public Task<UploadResult> UploadAsync(
            GpxBuildResult build,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(build.Entry!.TrackId);
            _cancellation.Cancel();
            return Task.FromResult(UploadResult.Uploaded(build.Entry.TrackId));
        }
    }

    private sealed class RecordingProgress : IProgress<GpxImportProgress>
    {
        public List<int> Indices { get; } = new();

        public void Report(GpxImportProgress value) => Indices.Add(value.Index);
    }
}
