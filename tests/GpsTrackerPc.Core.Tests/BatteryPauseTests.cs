using GpsTrackerPc.Core.Settings;
using GpsTrackerPc.Core.Simulator;
using Xunit;

namespace GpsTrackerPc.Core.Tests;

public class BatteryPausePolicyTests
{
    private static readonly PowerSettings Manual = PowerSettings.Default;
    private static readonly PowerSettings Automatic = PowerSettings.Default with { ResumeMode = BatteryResumeMode.Automatic };

    private static PowerReading OnBattery(int percent) => new(HasBattery: true, OnAcPower: false, percent);

    private static PowerReading Charging(int percent) => new(HasBattery: true, OnAcPower: true, percent);

    [Fact]
    public void PausesWhenBatteryDropsToThresholdWhileSimulating()
    {
        var policy = new BatteryPausePolicy();

        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(11), Manual, isSimulating: true, pausedByBattery: false));
        Assert.Equal(BatteryPauseAction.Pause, policy.Evaluate(OnBattery(10), Manual, isSimulating: true, pausedByBattery: false));
    }

    [Fact]
    public void DoesNotPauseWhenNotSimulatingOnAcPowerOrWithoutBattery()
    {
        var policy = new BatteryPausePolicy();

        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(5), Manual, isSimulating: false, pausedByBattery: false));
        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(Charging(5), Manual, isSimulating: true, pausedByBattery: false));
        Assert.Equal(
            BatteryPauseAction.None,
            policy.Evaluate(new PowerReading(HasBattery: false, OnAcPower: false, null), Manual, isSimulating: true, pausedByBattery: false));
        Assert.Equal(
            BatteryPauseAction.None,
            policy.Evaluate(OnBattery(5), Manual with { PauseOnLowBattery = false }, isSimulating: true, pausedByBattery: false));
    }

    [Fact]
    public void ManualResumeAtLowBatteryIsNotPausedAgainUntilRecharged()
    {
        var policy = new BatteryPausePolicy();
        Assert.Equal(BatteryPauseAction.Pause, policy.Evaluate(OnBattery(9), Manual, isSimulating: true, pausedByBattery: false));

        // 使用者在低電量時自己按「繼續」：不再一直被暫停。
        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(8), Manual, isSimulating: true, pausedByBattery: false));

        // 充過電、又降到門檻以下，才會再暫停一次。
        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(Charging(30), Manual, isSimulating: true, pausedByBattery: false));
        Assert.Equal(BatteryPauseAction.Pause, policy.Evaluate(OnBattery(10), Manual, isSimulating: true, pausedByBattery: false));
    }

    [Fact]
    public void ManualModeNotifiesOncePerPlugIn()
    {
        var policy = new BatteryPausePolicy();
        policy.Evaluate(OnBattery(10), Manual, isSimulating: true, pausedByBattery: false);

        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(9), Manual, isSimulating: false, pausedByBattery: true));
        Assert.Equal(BatteryPauseAction.NotifyCharging, policy.Evaluate(Charging(9), Manual, isSimulating: false, pausedByBattery: true));
        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(Charging(12), Manual, isSimulating: false, pausedByBattery: true));

        // 拔掉再接上，要再提醒一次。
        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(12), Manual, isSimulating: false, pausedByBattery: true));
        Assert.Equal(BatteryPauseAction.NotifyCharging, policy.Evaluate(Charging(12), Manual, isSimulating: false, pausedByBattery: true));
    }

    [Fact]
    public void AutomaticModeResumesWhenPluggedIn()
    {
        var policy = new BatteryPausePolicy();
        Assert.Equal(BatteryPauseAction.Pause, policy.Evaluate(OnBattery(10), Automatic, isSimulating: true, pausedByBattery: false));

        Assert.Equal(BatteryPauseAction.None, policy.Evaluate(OnBattery(9), Automatic, isSimulating: false, pausedByBattery: true));
        Assert.Equal(BatteryPauseAction.Resume, policy.Evaluate(Charging(9), Automatic, isSimulating: false, pausedByBattery: true));
    }
}

public class PowerSettingsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public PowerSettingsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GpsTrackerPcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "power.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 測試暫存資料夾清不掉不影響結果。
        }
    }

    [Fact]
    public void DefaultsToPauseAtTenPercentWithManualResume()
    {
        var settings = new PowerSettingsStore(_path).Load();

        Assert.True(settings.PauseOnLowBattery);
        Assert.Equal(10, settings.ThresholdPercent);
        Assert.Equal(BatteryResumeMode.Manual, settings.ResumeMode);
    }

    [Fact]
    public void SavesAndLoadsSettings()
    {
        var store = new PowerSettingsStore(_path);

        store.Save(new PowerSettings(PauseOnLowBattery: false, 20, BatteryResumeMode.Automatic));

        var loaded = new PowerSettingsStore(_path).Load();
        Assert.False(loaded.PauseOnLowBattery);
        Assert.Equal(20, loaded.ThresholdPercent);
        Assert.Equal(BatteryResumeMode.Automatic, loaded.ResumeMode);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public void ThresholdIsClampedAndCorruptFileUsesDefaults()
    {
        File.WriteAllText(_path, """{"pauseOnLowBattery":true,"thresholdPercent":95,"resumeMode":"bogus"}""");
        var loaded = new PowerSettingsStore(_path).Load();
        Assert.Equal(PowerSettings.MaxThresholdPercent, loaded.ThresholdPercent);
        Assert.Equal(BatteryResumeMode.Manual, loaded.ResumeMode);

        File.WriteAllText(_path, "{ not json");
        Assert.Equal(PowerSettings.Default, new PowerSettingsStore(_path).Load());
    }
}

public class PlaybackProgressFormatterTests
{
    [Fact]
    public void CompletedAndRemainingAddUpToOneHundred()
    {
        Assert.Equal("完成 42.3%", PlaybackProgressFormatter.Format(0.42345, PlaybackProgressDisplay.Completed));
        Assert.Equal("剩餘 57.7%", PlaybackProgressFormatter.Format(0.42345, PlaybackProgressDisplay.Remaining));
    }

    [Fact]
    public void DoesNotShowOneHundredPercentBeforeTheEnd()
    {
        Assert.Equal("完成 99.9%", PlaybackProgressFormatter.Format(0.99999, PlaybackProgressDisplay.Completed));
        Assert.Equal("剩餘 0.1%", PlaybackProgressFormatter.Format(0.99999, PlaybackProgressDisplay.Remaining));
        Assert.Equal("完成 100.0%", PlaybackProgressFormatter.Format(1.0, PlaybackProgressDisplay.Completed));
        Assert.Equal("剩餘 0.0%", PlaybackProgressFormatter.Format(1.0, PlaybackProgressDisplay.Remaining));
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        Assert.Equal("完成 0.0%", PlaybackProgressFormatter.Format(-0.5, PlaybackProgressDisplay.Completed));
        Assert.Equal("剩餘 100.0%", PlaybackProgressFormatter.Format(double.NaN, PlaybackProgressDisplay.Remaining));
        Assert.Equal("完成 100.0%", PlaybackProgressFormatter.Format(3.0, PlaybackProgressDisplay.Completed));
    }
}
