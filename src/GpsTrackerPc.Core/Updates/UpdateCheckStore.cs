using System.Text;
using System.Text.Json;
using GpsTrackerPc.Core.Util;

namespace GpsTrackerPc.Core.Updates;

public enum UpdateInstallMode
{
    /// <summary>背景下載完成後，等使用者按「安裝更新」。</summary>
    Manual,

    /// <summary>背景下載完成後直接安裝，之後只要重啟程式。</summary>
    Automatic,
}

public enum UpdateChannel
{
    Stable,
    Beta,
}

public sealed record UpdateCheckState(
    bool AutoCheckEnabled,
    long? LastCheckMs,
    bool BackgroundDownloadEnabled = true,
    UpdateInstallMode InstallMode = UpdateInstallMode.Manual,
    bool AutoUploadPendingRecords = true,
    UpdateChannel Channel = UpdateChannel.Stable,
    bool DownloadOnlyOnUnmetered = true,
    int AutoUploadDailyLimit = 20,
    int CheckIntervalMinutes = UpdateCheckStore.DefaultCheckIntervalMinutes);

/// <summary>
/// 記錄自動檢查更新、背景下載、安裝方式與上次檢查時間；
/// 啟動時與程式開著的期間，距離上次檢查超過設定的間隔（預設 1 天）才會再檢查。
/// </summary>
public sealed class UpdateCheckStore
{
    public const int DefaultCheckIntervalMinutes = 24 * 60;
    public const int MinCheckIntervalMinutes = 15;
    public const int MaxCheckIntervalMinutes = 7 * 24 * 60;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public UpdateCheckStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public UpdateCheckState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new UpdateCheckState(AutoCheckEnabled: true, LastCheckMs: null);
            }

            var stored = JsonSerializer.Deserialize<StoredUpdateCheckState>(
                File.ReadAllText(_filePath),
                SerializerOptions);
            return new UpdateCheckState(
                stored?.AutoCheckEnabled ?? true,
                stored?.LastCheckMs,
                stored?.BackgroundDownloadEnabled ?? true,
                stored?.InstallMode ?? UpdateInstallMode.Manual,
                stored?.AutoUploadPendingRecords ?? true,
                stored?.Channel ?? UpdateChannel.Stable,
                stored?.DownloadOnlyOnUnmetered ?? true,
                Math.Clamp(stored?.AutoUploadDailyLimit ?? 20, 1, 200),
                ClampCheckInterval(stored?.CheckIntervalMinutes ?? DefaultCheckIntervalMinutes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateCheckState(AutoCheckEnabled: true, LastCheckMs: null);
        }
    }

    public bool ShouldAutoCheck(DateTimeOffset now)
    {
        var state = Load();
        if (!state.AutoCheckEnabled)
        {
            return false;
        }

        if (state.LastCheckMs is not { } lastCheckMs)
        {
            return true;
        }

        var elapsed = now.ToUnixTimeMilliseconds() - lastCheckMs;
        return elapsed >= TimeSpan.FromMinutes(state.CheckIntervalMinutes).TotalMilliseconds;
    }

    public static int ClampCheckInterval(int minutes) =>
        Math.Clamp(minutes, MinCheckIntervalMinutes, MaxCheckIntervalMinutes);

    public void MarkChecked(DateTimeOffset now) =>
        Save(Load() with { LastCheckMs = now.ToUnixTimeMilliseconds() });

    public void SetAutoCheck(bool enabled) => Save(Load() with { AutoCheckEnabled = enabled });

    public void SetBackgroundDownload(bool enabled) =>
        Save(Load() with { BackgroundDownloadEnabled = enabled });

    public void SetInstallMode(UpdateInstallMode mode) => Save(Load() with { InstallMode = mode });

    public void SetAutoUploadPendingRecords(bool enabled) =>
        Save(Load() with { AutoUploadPendingRecords = enabled });

    public void SetChannel(UpdateChannel channel) => Save(Load() with { Channel = channel });

    public void SetDownloadOnlyOnUnmetered(bool enabled) =>
        Save(Load() with { DownloadOnlyOnUnmetered = enabled });

    public void SetAutoUploadDailyLimit(int limit) =>
        Save(Load() with { AutoUploadDailyLimit = Math.Clamp(limit, 1, 200) });

    public void SetCheckInterval(int minutes) =>
        Save(Load() with { CheckIntervalMinutes = ClampCheckInterval(minutes) });

    public void Save(UpdateCheckState state)
    {
        var json = JsonSerializer.Serialize(
            new StoredUpdateCheckState
            {
                AutoCheckEnabled = state.AutoCheckEnabled,
                LastCheckMs = state.LastCheckMs,
                BackgroundDownloadEnabled = state.BackgroundDownloadEnabled,
                InstallMode = state.InstallMode,
                AutoUploadPendingRecords = state.AutoUploadPendingRecords,
                Channel = state.Channel,
                DownloadOnlyOnUnmetered = state.DownloadOnlyOnUnmetered,
                AutoUploadDailyLimit = state.AutoUploadDailyLimit,
                CheckIntervalMinutes = ClampCheckInterval(state.CheckIntervalMinutes),
            },
            SerializerOptions);
        AtomicFile.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(json));
    }

    private sealed class StoredUpdateCheckState
    {
        public bool? AutoCheckEnabled { get; set; }

        public long? LastCheckMs { get; set; }

        public bool? BackgroundDownloadEnabled { get; set; }

        public UpdateInstallMode? InstallMode { get; set; }

        public bool? AutoUploadPendingRecords { get; set; }

        public UpdateChannel? Channel { get; set; }

        public bool? DownloadOnlyOnUnmetered { get; set; }

        public int? AutoUploadDailyLimit { get; set; }

        public int? CheckIntervalMinutes { get; set; }
    }
}
