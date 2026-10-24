namespace GpsTrackerPc.Core.Simulator;

/// <summary>manifest 裡一台裝置的識別資訊。</summary>
public sealed record ManifestDevice(string DeviceName, string? Source);

public sealed record DeviceNameValidation(
    bool IsValid,
    bool HasSimulatedConflict,
    string? ErrorMessage,
    string? WarningMessage,
    bool HasNonSimulatedConflict = false);

/// <summary>
/// 裝置名稱規則：不能空白；可以用網站上已存在的名稱（含手機等非模擬紀錄），
/// 但會警告紀錄將合併成同一台裝置。沿用自己目前的名稱時不警告。
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

        // 同一台電腦沿用目前名稱時，manifest 裡的同名紀錄就是自己（不論來源），不用警告。
        var isCurrentName = string.Equals(
            (currentName ?? string.Empty).Trim(),
            normalized,
            StringComparison.Ordinal);
        if (isCurrentName)
        {
            return new DeviceNameValidation(true, false, null, null);
        }

        var sameName = list
            .Where(device => string.Equals(device.DeviceName, normalized, StringComparison.Ordinal))
            .ToList();

        // 名稱可能來自手機或舊版程式（沒有 source 欄位）。沿用時允許合併，
        // 但要提醒使用者模擬紀錄會掛在這個既有裝置底下。
        if (sameName.Any(device => !IsSimulated(device.Source)))
        {
            return new DeviceNameValidation(
                true,
                false,
                null,
                $"「{normalized}」已是網站上的裝置名稱（非模擬紀錄）。沿用後模擬紀錄會與它合併成同一台裝置。",
                HasNonSimulatedConflict: true);
        }

        if (sameName.Count > 0)
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
