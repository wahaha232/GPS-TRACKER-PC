using GpsTrackerPc.Simulation.Model;

namespace GpsTrackerPc.Simulation.Routes;

/// <summary>隨機路線的形狀。</summary>
public enum RandomRouteShape
{
    /// <summary>隨機漫步：從中心出發，一步一步稍微轉彎地走，超出範圍就被拉回來。</summary>
    Walk,

    /// <summary>環形迴路：從中心出發繞一圈（路線設成「循環」）。</summary>
    Loop,
}

/// <summary>
/// 隨機生成軌跡點，只用 <see cref="GeoMath"/>。
/// 傳入固定種子的 <see cref="Random"/> 就會得到完全相同的軌跡。
/// 第一個點一律就是 <paramref name="center"/>。
/// </summary>
public static class RandomRouteGenerator
{
    public const int MinPoints = 2;
    public const int MaxPoints = 500;
    public const int MinRadiusMeters = 50;
    public const int MaxRadiusMeters = 50_000;

    public static IReadOnlyList<Coordinates> Generate(
        Coordinates center,
        int pointCount,
        double radiusMeters,
        RandomRouteShape shape,
        Random? random = null)
    {
        if (pointCount is < MinPoints or > MaxPoints)
        {
            throw new ArgumentException("pointCount out of range", nameof(pointCount));
        }

        if (!(radiusMeters >= MinRadiusMeters && radiusMeters <= MaxRadiusMeters))
        {
            throw new ArgumentException("radius out of range", nameof(radiusMeters));
        }

        random ??= Random.Shared;
        return shape switch
        {
            RandomRouteShape.Walk => RandomWalk(center, pointCount, radiusMeters, random),
            RandomRouteShape.Loop => Loop(center, pointCount, radiusMeters, random),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    private static List<Coordinates> RandomWalk(Coordinates center, int count, double radius, Random random)
    {
        // 平均步長讓整條路線大約走出範圍直徑的 1.5 倍；點很少時限制單步不要超過半徑的一半。
        var meanStep = Math.Min(radius * 3.0 / count, radius * 0.4);
        var bearing = random.NextDouble() * 360.0;
        var current = center;
        var points = new List<Coordinates>(count) { center };
        for (var index = 1; index < count; index++)
        {
            // 大多數時候小幅轉彎，偶爾大轉彎（像真的在街上走）
            bearing += random.NextDouble() < 0.1
                ? NextDouble(random, -90.0, 90.0)
                : NextDouble(random, -35.0, 35.0);
            // 走到接近範圍邊緣就朝中心方向拉回來
            if (GeoMath.DistanceMeters(center, current) > radius * 0.9)
            {
                bearing = GeoMath.InitialBearingDegrees(current, center) +
                    NextDouble(random, -45.0, 45.0);
            }

            current = GeoMath.Destination(current, meanStep * NextDouble(random, 0.5, 1.5), Normalize(bearing));
            points.Add(current);
        }

        return points;
    }

    private static List<Coordinates> Loop(Coordinates center, int count, double radius, Random random)
    {
        // 圓周通過起點、直徑等於範圍半徑：離起點最遠的點大約就是 radius。
        var circleRadius = radius / 2.0;
        var startToCircleCenter = random.NextDouble() * 360.0;
        var circleCenter = GeoMath.Destination(center, circleRadius, startToCircleCenter);
        var centerToStart = Normalize(startToCircleCenter + 180.0);
        var angleStep = 360.0 / count;
        var points = new List<Coordinates>(count) { center };
        for (var index = 1; index < count; index++)
        {
            var angle = angleStep * index + NextDouble(random, -0.3, 0.3) * angleStep;
            var distance = circleRadius * (1.0 + NextDouble(random, -0.12, 0.12));
            points.Add(GeoMath.Destination(circleCenter, distance, Normalize(centerToStart + angle)));
        }

        return points;
    }

    private static double NextDouble(Random random, double from, double until) =>
        from + random.NextDouble() * (until - from);

    private static double Normalize(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;
}
