using System.IO;

namespace GpsTrackerPc.App.Infrastructure;

/// <summary>PC 版固定使用的本機路徑。</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GpsTrackerPc");

    public static string DefaultDataDirectory => Path.Combine(Root, "data");

    /// <summary>同步下來與網頁讀取的本機資料夾；啟動時可依設定改成其他磁碟或資料夾。</summary>
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;

    public static string GitHubTokenFile => Path.Combine(Root, "github-token.bin");

    public static string SyncCacheFile => Path.Combine(Root, "sync-cache.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string MapSettingsFile => Path.Combine(Root, "map-settings.json");

    public static string UpdateCheckFile => Path.Combine(Root, "update-check.json");

    public static string ThemeFile => Path.Combine(Root, "theme.json");

    public static string FavoritesFile => Path.Combine(Root, "favorites.json");

    /// <summary>用過的 GPX 檔路徑，供模擬頁重複載入。</summary>
    public static string GpxHistoryFile => Path.Combine(Root, "gpx-history.json");

    /// <summary>背景下載的更新檔；依版本放在子資料夾。</summary>
    public static string UpdateDownloadDirectory => Path.Combine(Root, "updates");

    /// <summary>下載完成、尚未上傳的模擬紀錄。</summary>
    public static string PendingRecordsDirectory => Path.Combine(Root, "pending-records");

    /// <summary>重啟前暫存的模擬路線狀態。</summary>
    public static string RestartSessionFile => Path.Combine(Root, "restart-session.json");

    public static string RoutesDirectory => Path.Combine(Root, "routes");

    public static string CrashLogFile => Path.Combine(Root, "crash.log");

    public static string TraySettingsFile => Path.Combine(Root, "tray.json");

    public static string StorageSettingsFile => Path.Combine(Root, "storage.json");

    /// <summary>好友／群組清單、分享開關與固定裝置識別碼。</summary>
    public static string SocialFile => Path.Combine(Root, "social.json");

    /// <summary>啟動時套用設定的資料夾；失敗時保留預設值。</summary>
    public static bool TryApplyDataDirectory(string? configured, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(configured))
        {
            DataDirectory = DefaultDataDirectory;
            return TryPrepare(DefaultDataDirectory, out error);
        }

        if (!TryPrepare(configured, out var normalized, out error))
        {
            DataDirectory = DefaultDataDirectory;
            return false;
        }

        DataDirectory = normalized;
        return true;
    }

    /// <summary>只檢查資料夾能不能建立與寫入，不會改變目前執行中的路徑。</summary>
    public static bool TryValidateDataDirectory(string path, out string? error) =>
        TryPrepare(path, out _, out error);

    private static bool TryPrepare(string path, out string? error) =>
        TryPrepare(path, out _, out error);

    private static bool TryPrepare(string path, out string normalized, out string? error)
    {
        normalized = path;
        error = null;
        try
        {
            normalized = Path.GetFullPath(path);
            Directory.CreateDirectory(normalized);

            // 實際寫一個暫存檔，確認不是只有讀取權限（例如受保護的系統資料夾）。
            var probe = Path.Combine(normalized, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
