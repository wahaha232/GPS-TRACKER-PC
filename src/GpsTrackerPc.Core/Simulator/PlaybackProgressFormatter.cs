using System.Globalization;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>把播放進度轉成「完成／剩餘 百分比」文字。</summary>
public static class PlaybackProgressFormatter
{
    /// <summary>
    /// 完成百分比取到小數一位並無條件捨去，還沒走到終點不會先顯示 100%；
    /// 剩餘百分比用 100 減掉，兩種顯示加起來剛好 100%。
    /// </summary>
    public static double CompletedPercent(double fraction)
    {
        if (double.IsNaN(fraction))
        {
            return 0.0;
        }

        return Math.Floor(Math.Clamp(fraction, 0.0, 1.0) * 1000.0) / 10.0;
    }

    public static string Format(double fraction, PlaybackProgressDisplay display)
    {
        var completed = CompletedPercent(fraction);
        return display == PlaybackProgressDisplay.Remaining
            ? $"剩餘 {(100.0 - completed).ToString("0.0", CultureInfo.InvariantCulture)}%"
            : $"完成 {completed.ToString("0.0", CultureInfo.InvariantCulture)}%";
    }
}
