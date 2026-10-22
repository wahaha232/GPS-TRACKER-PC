using GpsTrackerPc.Core.Simulator;

namespace GpsTrackerPc.Core.Gpx;

/// <summary>把設定畫面選定的裝置名稱套到 GPX 匯入的紀錄與 manifest 項目。</summary>
public static class RecordDeviceNaming
{
    public static void ApplyDeviceName(GpxBuildResult build, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(build);
        var normalized = DeviceNameStore.Validate(deviceName);
        if (build.Track != null)
        {
            build.Track.DeviceName = normalized;
        }

        if (build.Entry != null)
        {
            build.Entry.DeviceName = normalized;
        }
    }
}
