namespace GpsTrackerPc.Core.Simulator;

/// <summary>manifest 裡一台裝置的識別資訊。</summary>
public sealed record ManifestDevice(string DeviceName, string? Source);

public sealed record DeviceNameValidation(
    bool IsValid,
    bool HasSimulatedConflict,
    string? ErrorMessage,
    string? WarningMessage);

/// <summary>
/// 裝置名稱規則：不能空白；不能和手機（非 simulated）同名；
/// 和另一台模擬裝置同名時只警告，因為紀錄會合併成同一台。
/// </summary>
public static class DeviceNamePolicy
{
    public static DeviceNameValidation Validate(
        string? name,
        IEnumerable<ManifestDevice>? devices,
        string? currentName = null)
    {
        string normalized;
        try
        {
            normalized = DeviceNameStore.Validate(name);
        }
        catch (ArgumentException ex)
        {
            return new DeviceNameValidation(false, false, ex.Message, null);
        }

        var list = (devices ?? Array.Empty<ManifestDevice>())
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceName))
            .Select(device => new ManifestDevice(device.DeviceName.Trim(), device.Source))
            .ToList();

        if (list.Any(device =>
                string.Equals(device.DeviceName, normalized, StringComparison.Ordinal) &&
                !IsSimulated(device.Source)))
        {
            return new DeviceNameValidation(
                false,
                false,
                $"「{normalized}」已是網站上的非模擬裝置名稱，請換一個名稱。",
                null);
        }

        // 同一台電腦沿用目前名稱時，manifest 裡的同名模擬紀錄就是自己，不用警告。
        var isCurrentName = string.Equals(
            (currentName ?? string.Empty).Trim(),
            normalized,
            StringComparison.Ordinal);
        if (!isCurrentName &&
            list.Any(device =>
                string.Equals(device.DeviceName, normalized, StringComparison.Ordinal) &&
                IsSimulated(device.Source)))
        {
            return new DeviceNameValidation(
                true,
                true,
                null,
                $"「{normalized}」已被另一台模擬裝置使用；上傳後會合併成同一台裝置。");
        }

        return new DeviceNameValidation(true, false, null, null);
    }

    public static bool IsSimulated(string? source) =>
        string.Equals(source, "simulated", StringComparison.Ordinal);
}
