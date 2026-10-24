using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.Core.Social;

/// <summary>
/// 把每個好友／群組收到的最新快照合併成畫面用的成員清單。
/// 只保留每台裝置最新一筆；停止分享或超過保留期限的資料不會顯示成線上。
/// </summary>
public static class SocialPresence
{
    public static IReadOnlyList<SocialPeer> Build(
        IEnumerable<SocialTarget> targets,
        IReadOnlyDictionary<string, IReadOnlyList<SocialSnapshot>> snapshotsByTarget,
        string selfDeviceId,
        Coordinates? selfCoordinates,
        DateTimeOffset now,
        TimeSpan? onlineWindow = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(snapshotsByTarget);

        var window = onlineWindow ?? SocialLimits.OnlineWindow;
        var peers = new List<SocialPeer>();

        foreach (var target in targets)
        {
            if (!SocialValidation.IsValidTarget(target))
            {
                continue;
            }

            if (!snapshotsByTarget.TryGetValue(target.Id, out var snapshots) || snapshots == null)
            {
                continue;
            }

            var latestByDevice = new Dictionary<string, SocialSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                if (snapshot == null ||
                    string.Equals(snapshot.DeviceId, selfDeviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.SentAtMs);
                if (now - updatedAt > SocialLimits.SnapshotRetention || updatedAt - now > TimeSpan.FromMinutes(10))
                {
                    continue;
                }

                if (!latestByDevice.TryGetValue(snapshot.DeviceId, out var existing) ||
                    snapshot.SentAtMs >= existing.SentAtMs)
                {
                    latestByDevice[snapshot.DeviceId] = snapshot;
                }
            }

            foreach (var snapshot in latestByDevice.Values)
            {
                var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.SentAtMs);
                var online = snapshot.Sharing && now - updatedAt <= window;
                double? distance = null;
                if (selfCoordinates != null)
                {
                    try
                    {
                        distance = GeoMath.DistanceMeters(selfCoordinates, snapshot.ToCoordinates());
                    }
                    catch (ArgumentException)
                    {
                        // 壞掉的座標已在解密時擋掉；這裡再保險一次。
                        continue;
                    }
                }

                peers.Add(new SocialPeer(
                    target.Kind,
                    target.Id,
                    target.Name,
                    snapshot.DeviceId,
                    snapshot.DeviceName,
                    snapshot.Latitude,
                    snapshot.Longitude,
                    snapshot.AccuracyMeters,
                    updatedAt,
                    snapshot.Sharing,
                    online,
                    distance));
            }
        }

        return peers
            .OrderBy(peer => peer.Kind)
            .ThenBy(peer => peer.TargetName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
