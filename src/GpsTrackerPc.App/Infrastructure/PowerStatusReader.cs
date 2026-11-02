using System.Windows.Forms;
using GpsTrackerPc.Core.Simulator;

namespace GpsTrackerPc.App.Infrastructure;

/// <summary>讀取筆電電池電量與是否接上電源。</summary>
internal static class PowerStatusReader
{
    public static PowerReading Read()
    {
        try
        {
            var status = SystemInformation.PowerStatus;
            var chargeStatus = status.BatteryChargeStatus;
            var hasBattery = !chargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) &&
                chargeStatus != BatteryChargeStatus.Unknown;
            // BatteryLifePercent 讀不到時是 255（換算後大於 1）。
            int? percent = hasBattery && status.BatteryLifePercent is >= 0f and <= 1f
                ? (int)Math.Round(status.BatteryLifePercent * 100f)
                : null;
            return new PowerReading(hasBattery, status.PowerLineStatus == PowerLineStatus.Online, percent);
        }
        catch (Exception)
        {
            // 讀不到電源狀態時當作接著電源，不會誤暫停。
            return new PowerReading(HasBattery: false, OnAcPower: true, BatteryPercent: null);
        }
    }
}
