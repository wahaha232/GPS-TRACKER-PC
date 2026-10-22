using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Core.Simulator;

public sealed record ViewportBounds(double West, double South, double East, double North);

/// <summary>
/// 跟隨模式：只有目前位置離開畫面中央安全區（外圍 15%）才需要平移。
/// 平移只改中心，不改縮放層級。
/// </summary>
public static class ViewportFollowPolicy
{
    public const double EdgeFraction = 0.15;

    public static bool ShouldPan(
        ViewportBounds viewport,
        Coordinates position,
        bool followEnabled,
        double edgeFraction = EdgeFraction)
    {
        if (!followEnabled)
        {
            return false;
        }

        var width = viewport.East - viewport.West;
        var height = viewport.North - viewport.South;
        if (!(width > 0) || !(height > 0) ||
            !double.IsFinite(viewport.West) || !double.IsFinite(viewport.East) ||
            !double.IsFinite(viewport.South) || !double.IsFinite(viewport.North))
        {
            return false;
        }

        var fraction = Math.Clamp(edgeFraction, 0.0, 0.49);
        var safeWest = viewport.West + width * fraction;
        var safeEast = viewport.East - width * fraction;
        var safeSouth = viewport.South + height * fraction;
        var safeNorth = viewport.North - height * fraction;

        return position.Longitude < safeWest ||
               position.Longitude > safeEast ||
               position.Latitude < safeSouth ||
               position.Latitude > safeNorth;
    }
}
