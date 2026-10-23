namespace GpsTrackerPc.Core.Gpx;

public sealed record GpxImportCandidate(string FileName, GpxBuildResult Build);

public enum GpxImportItemStatus
{
    Uploaded,
    Duplicate,
    ParseError,
    Failed,
}

public sealed record GpxImportItemResult(
    string FileName,
    GpxImportItemStatus Status,
    string Message,
    string? TrackId = null);

public sealed record GpxImportProgress(int Index, GpxImportItemResult Result);

/// <summary>
/// 依序一筆一筆上傳；任何一筆失敗或重複都只記錄結果，不中斷後面的檔案。
/// </summary>
public sealed class GpxImportBatch
{
    private readonly IRecordUploader _uploader;

    public GpxImportBatch(IRecordUploader uploader)
    {
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
    }

    public Task<IReadOnlyList<GpxImportItemResult>> ImportAsync(
        IReadOnlyList<GpxImportCandidate> candidates,
        CancellationToken cancellationToken = default) =>
        ImportAsync(candidates, progress: null, cancellationToken);

    public async Task<IReadOnlyList<GpxImportItemResult>> ImportAsync(
        IReadOnlyList<GpxImportCandidate> candidates,
        IProgress<GpxImportProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var results = new List<GpxImportItemResult>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            cancellationToken.ThrowIfCancellationRequested();

            GpxImportItemResult result;
            if (candidate.Build.Error != null)
            {
                result = new GpxImportItemResult(
                    candidate.FileName,
                    GpxImportItemStatus.ParseError,
                    GpxErrors.Get(candidate.Build.Error));
            }
            else
            {
                try
                {
                    var upload = await _uploader.UploadAsync(candidate.Build, cancellationToken).ConfigureAwait(false);
                    result = upload.Status switch
                    {
                        UploadStatus.Uploaded => new GpxImportItemResult(
                            candidate.FileName,
                            GpxImportItemStatus.Uploaded,
                            string.IsNullOrWhiteSpace(upload.Warning)
                                ? "已匯入"
                                : "已匯入；" + upload.Warning,
                            upload.TrackId),
                        UploadStatus.Duplicate => new GpxImportItemResult(
                            candidate.FileName, GpxImportItemStatus.Duplicate, "已跳過：重複", upload.TrackId),
                        _ => new GpxImportItemResult(
                            candidate.FileName, GpxImportItemStatus.Failed,
                            upload.ErrorMessage ?? "上傳失敗", upload.TrackId),
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 單一檔案失敗不影響整批。
                    result = new GpxImportItemResult(
                        candidate.FileName, GpxImportItemStatus.Failed, ex.Message);
                }
            }

            results.Add(result);
            progress?.Report(new GpxImportProgress(index, result));
        }

        return results;
    }
}
