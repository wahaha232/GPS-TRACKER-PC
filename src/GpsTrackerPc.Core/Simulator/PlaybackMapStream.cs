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
    /// <summary>抽稀後較舊段落保留的點數；留出空間，避免每加一點就重新抽稀一次。</summary>
    private const int OlderPointsAfterCompact = PlaybackMapPolicy.MaxDisplayPoints / 2;

    /// <summary>完整的顯示取樣（只做去重與分段），抽稀一律從這裡重新計算。</summary>
    private readonly List<List<PlaybackSample>> _raw = new();
    private readonly List<List<PlaybackSample>> _segments = new();
    private readonly List<PlaybackSample> _pending = new();
    private int _rawCount;
    private int _displayCount;

    public int DisplayPointCount => _displayCount;

    public void Append(PlaybackSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (_raw.Count > 0 && _raw[^1].Count > 0)
        {
            var last = _raw[^1][^1];

            // 時間沒有往前的取樣（例如同一批取樣被重送）不能接在後面，
            // 否則會從目前位置畫一條直線回到較早的位置。
            if (sample.ElapsedSeconds <= last.ElapsedSeconds)
            {
                return;
            }

            // 停留點或暫停時位置不變，不要一直往顯示軌跡加同一個點。
            if (last.Coordinates == sample.Coordinates)
            {
                return;
            }
        }

        if (_raw.Count == 0 ||
            sample.ElapsedSeconds - _raw[^1][^1].ElapsedSeconds > PlaybackMapPolicy.TrailGapSeconds)
        {
            _raw.Add(new List<PlaybackSample>());
            _segments.Add(new List<PlaybackSample>());
        }

        _raw[^1].Add(sample);
        _rawCount++;
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
    /// 保留最新 <see cref="PlaybackMapPolicy.RecentPointsToKeep"/> 點，較舊的部分從完整取樣用固定間隔重新抽稀。
    /// 不能拿上一次抽稀的結果再抽稀：那樣舊段落每次都會再減半，播放越久越稀，
    /// 最後只剩幾個點，地圖上就畫成橫跨整條路線的直線。只改顯示用資料，不動播放取樣。
    /// </summary>
    private void Compact()
    {
        var recentStart = Math.Max(0, _rawCount - PlaybackMapPolicy.RecentPointsToKeep);
        var stride = Math.Max(1, (int)Math.Ceiling(recentStart / (double)OlderPointsAfterCompact));

        List<List<PlaybackSample>> compacted;
        while (true)
        {
            compacted = Thin(recentStart, stride);
            var count = compacted.Sum(segment => segment.Count);
            if (count <= PlaybackMapPolicy.MaxDisplayPoints || stride >= recentStart)
            {
                break;
            }

            // 中斷段落很多時，每段的頭尾都要保留，可能超過上限；加大間隔再算一次。
            stride *= 2;
        }

        // 段落多到連頭尾都放不下：從最舊的段落開始捨棄，讓畫面點數維持在上限內。
        var total = compacted.Sum(segment => segment.Count);
        while (total > PlaybackMapPolicy.MaxDisplayPoints && compacted.Count > 1)
        {
            total -= compacted[0].Count;
            compacted.RemoveAt(0);
        }

        _segments.Clear();
        _segments.AddRange(compacted);
        _displayCount = total;
    }

    /// <summary>
    /// 依全域索引抽稀：索引在 <paramref name="recentStart"/> 之後的全部保留，
    /// 之前的每 <paramref name="stride"/> 點留一點，並保留每段頭尾與接到最新部分的那一點，線條才不會斷開或走捷徑。
    /// </summary>
    private List<List<PlaybackSample>> Thin(int recentStart, int stride)
    {
        var result = new List<List<PlaybackSample>>(_raw.Count);
        var globalIndex = 0;
        foreach (var segment in _raw)
        {
            var reduced = new List<PlaybackSample>();
            for (var index = 0; index < segment.Count; index++, globalIndex++)
            {
                if (globalIndex >= recentStart ||
                    globalIndex % stride == 0 ||
                    index == 0 ||
                    index == segment.Count - 1 ||
                    globalIndex == recentStart - 1)
                {
                    reduced.Add(segment[index]);
                }
            }

            if (reduced.Count > 0)
            {
                result.Add(reduced);
            }
        }

        return result;
    }
}
