using GpsTrackerPc.Core.Settings;

namespace GpsTrackerPc.Core.Simulator;

/// <summary>一次讀到的電源狀態；沒有電池（桌機）或讀不到電量時 <see cref="BatteryPercent"/> 為 null。</summary>
public sealed record PowerReading(bool HasBattery, bool OnAcPower, int? BatteryPercent);

public enum BatteryPauseAction
{
    None,

    /// <summary>電量降到門檻以下且沒有接電源：暫停模擬。</summary>
    Pause,

    /// <summary>低電量暫停中接上電源，設定為自動繼續。</summary>
    Resume,

    /// <summary>低電量暫停中接上電源，設定為手動繼續：提醒使用者可以按「繼續」。</summary>
    NotifyCharging,
}

/// <summary>
/// 判斷低電量時要不要暫停、充電時要不要繼續。
/// 暫停後只有在充電或電量回到門檻以上才會重新啟用，使用者在低電量時手動按「繼續」不會馬上又被暫停。
/// </summary>
public sealed class BatteryPausePolicy
{
    private bool _armed = true;
    private bool _chargingNotified;

    public BatteryPauseAction Evaluate(
        PowerReading reading,
        PowerSettings settings,
        bool isSimulating,
        bool pausedByBattery)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(settings);

        var isLow = reading.HasBattery &&
            !reading.OnAcPower &&
            reading.BatteryPercent is { } percent &&
            percent <= settings.ThresholdPercent;
        if (!isLow)
        {
            _armed = true;
        }

        if (pausedByBattery)
        {
            if (!reading.OnAcPower)
            {
                // 拔掉電源後再接上，要再提醒一次。
                _chargingNotified = false;
                return BatteryPauseAction.None;
            }

            if (settings.ResumeMode == BatteryResumeMode.Automatic)
            {
                return BatteryPauseAction.Resume;
            }

            if (_chargingNotified)
            {
                return BatteryPauseAction.None;
            }

            _chargingNotified = true;
            return BatteryPauseAction.NotifyCharging;
        }

        if (settings.PauseOnLowBattery && isSimulating && isLow && _armed)
        {
            _armed = false;
            _chargingNotified = false;
            return BatteryPauseAction.Pause;
        }

        return BatteryPauseAction.None;
    }
}
