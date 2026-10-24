using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Social;

/// <summary>
/// 記憶體中的位置快取：記錄每個好友／群組收到的訊息與最後一則訊息 ID，
/// 讓輪詢可以只用 since= 取得新訊息，不會每次重讀整個主題。
/// </summary>
public sealed class SocialDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<SocialSnapshot>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastMessageIds = new(StringComparer.Ordinal);

    public void Merge(string targetId, IEnumerable<SocialSnapshot> snapshots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        lock (_gate)
        {
            if (!_snapshots.TryGetValue(targetId, out var list))
            {
                list = new List<SocialSnapshot>();
                _snapshots[targetId] = list;
            }

            foreach (var snapshot in snapshots)
            {
                if (snapshot != null)
                {
                    list.Add(snapshot);
                }
            }

            if (list.Count > SocialLimits.MaxSnapshotsPerTarget)
            {
                list.RemoveRange(0, list.Count - SocialLimits.MaxSnapshotsPerTarget);
            }
        }
    }

    public void SetLastMessageId(string targetId, string? messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        lock (_gate)
        {
            _lastMessageIds[targetId] = messageId;
        }
    }

    public string? GetLastMessageId(string targetId)
    {
        lock (_gate)
        {
            return _lastMessageIds.TryGetValue(targetId, out var id) ? id : null;
        }
    }

    public void Remove(string targetId)
    {
        lock (_gate)
        {
            _snapshots.Remove(targetId);
            _lastMessageIds.Remove(targetId);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<SocialSnapshot>> CreateSnapshotView()
    {
        lock (_gate)
        {
            return _snapshots.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<SocialSnapshot>)pair.Value.ToArray(),
                StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<SocialPeer> BuildPeers(
        IEnumerable<SocialTarget> targets,
        string selfDeviceId,
        Coordinates? selfCoordinates,
        DateTimeOffset now,
        TimeSpan? onlineWindow = null) =>
        SocialPresence.Build(
            targets,
            CreateSnapshotView(),
            selfDeviceId,
            selfCoordinates,
            now,
            onlineWindow);

    /// <summary>移除已刪除的好友／群組快取，避免越積越多。</summary>
    public void Retain(IEnumerable<string> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        var keep = new HashSet<string>(targetIds, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var key in _snapshots.Keys.Where(key => !keep.Contains(key)).ToList())
            {
                _snapshots.Remove(key);
            }

            foreach (var key in _lastMessageIds.Keys.Where(key => !keep.Contains(key)).ToList())
            {
                _lastMessageIds.Remove(key);
            }
        }
    }
}
