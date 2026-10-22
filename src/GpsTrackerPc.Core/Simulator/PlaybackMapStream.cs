namespace GpsTrackerPc.Core.Simulator;

/// <summary>播放畫面更新與顯示軌跡的上限；和 PlaybackController 的取樣資料分開。</summary>
public static class PlaybackMapPolicy
{
    /// <summary>畫面更新間隔（毫秒）；模擬本身仍用實際經過時間推進。</summary>
    public const int UpdateIntervalMilliseconds = 500;

    /// <summary>畫面上最多保留的軌跡點；只影響顯示，不影響 PlaybackController 的取樣。</summary>
    public const int MaxDisplayPoints = 1200;

    /// <summary>抽稀時保留最新的點數，讓最近的路線仍然精細。</summary>
    public const int RecentPointsToKeep = 200;

    /// <summary>MapLibre 軌跡線最多每秒 setData 一次。</summary>
    public const int TrailSetDataIntervalMilliseconds = 1000;

    /// <summary>取樣時間差超過這個秒數就視為 GPS 中斷，顯示成另一段線。</summary>
    public const double TrailGapSeconds = 1.5;
}

public sealed record PlaybackTrackSegment(IReadOnlyList<PlaybackSample> Points);

/// <summary>一次送到地圖的資料；ReplaceAll=true 時用 Segments 取代整條顯示軌跡。</summary>
public sealed record PlaybackMapFrame(
    bool ReplaceAll,
    IReadOnlyList<PlaybackSample> Samples,
    IReadOnlyList<PlaybackTrackSegment> Segments);

/// <summary>
/// 把播放取樣合併成適合地圖的批次，並在顯示軌跡太長時抽稀舊段落。
/// 原始 <see cref="PlaybackResult.Samples"/> 不會被修改。
/// </summary>
public sealed class PlaybackMapStream
{
    private readonly List<List<PlaybackSample>> _segments = new();
    private readonly List<PlaybackSample> _pending = new();
    private int _displayCount;

    public int DisplayPointCount => _displayCount;

    public void Append(PlaybackSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // 停留點或暫停時位置不變，不要一直往顯示軌跡加同一個點。
        if (_segments.Count > 0 &&
            _segments[^1].Count > 0 &&
            _segments[^1][^1].Coordinates == sample.Coordinates)
        {
            return;
        }

        if (_segments.Count == 0 ||
            sample.ElapsedSeconds - _segments[^1][^1].ElapsedSeconds > PlaybackMapPolicy.TrailGapSeconds)
        {
            _segments.Add(new List<PlaybackSample>());
        }

        _segments[^1].Add(sample);
        _pending.Add(sample);
        _displayCount++;
    }

    public PlaybackMapFrame BuildFrame(bool forceReplace = false)
    {
        if (forceReplace || _displayCount > PlaybackMapPolicy.MaxDisplayPoints)
        {
            if (_displayCount > PlaybackMapPolicy.MaxDisplayPoints)
            {
                Compact();
            }

            _pending.Clear();
            var segments = _segments
                .Where(segment => segment.Count > 0)
                .Select(segment => new PlaybackTrackSegment(segment.ToList()))
                .ToList();
            return new PlaybackMapFrame(true, Array.Empty<PlaybackSample>(), segments);
        }

        var samples = _pending.ToList();
        _pending.Clear();
        return new PlaybackMapFrame(false, samples, Array.Empty<PlaybackTrackSegment>());
    }

    /// <summary>
    /// 保留最新 <see cref="PlaybackMapPolicy.RecentPointsToKeep"/> 點，較舊的段落用固定間隔抽稀。
    /// 只改顯示用資料，不動播放取樣。
    /// </summary>
    private void Compact()
    {
        var recent = new List<PlaybackSample>();
        while (_segments.Count > 0 && recent.Count < PlaybackMapPolicy.RecentPointsToKeep)
        {
            var last = _segments[^1];
            var take = Math.Min(PlaybackMapPolicy.RecentPointsToKeep - recent.Count, last.Count);
            recent.InsertRange(0, last.Skip(last.Count - take));
            if (take == last.Count)
            {
                _segments.RemoveAt(_segments.Count - 1);
            }
            else
            {
                last.RemoveRange(last.Count - take, take);
            }
        }

        var budget = Math.Max(1, PlaybackMapPolicy.MaxDisplayPoints - recent.Count);
        var olderTotal = _segments.Sum(segment => segment.Count);
        var stride = Math.Max(1, (int)Math.Ceiling(olderTotal / (double)budget));

        var compacted = new List<List<PlaybackSample>>();
        foreach (var segment in _segments)
        {
            if (segment.Count == 0)
            {
                continue;
            }

            var reduced = new List<PlaybackSample>();
            for (var index = 0; index < segment.Count; index += stride)
            {
                reduced.Add(segment[index]);
            }

            if (reduced.Count == 0 || !ReferenceEquals(reduced[^1], segment[^1]))
            {
                reduced.Add(segment[^1]);
            }

            compacted.Add(reduced);
        }

        if (recent.Count > 0)
        {
            compacted.Add(recent);
        }

        _segments.Clear();
        _segments.AddRange(compacted);
        _displayCount = _segments.Sum(segment => segment.Count);

        // 多個中斷段落各自補上尾點時，可能比上限多幾個；從最舊的段落移除多餘的點。
        while (_displayCount > PlaybackMapPolicy.MaxDisplayPoints && _segments.Count > 0)
        {
            var removed = false;
            for (var segmentIndex = 0; segmentIndex < _segments.Count; segmentIndex++)
            {
                var segment = _segments[segmentIndex];
                if (segment.Count > 2)
                {
                    segment.RemoveAt(1);
                    _displayCount--;
                    removed = true;
                    break;
                }

                if (segment.Count == 2 && _segments.Count > 1)
                {
                    _segments.RemoveAt(segmentIndex);
                    _displayCount -= 2;
                    removed = true;
                    break;
                }
            }

            if (!removed)
            {
                break;
            }
        }
    }
}
