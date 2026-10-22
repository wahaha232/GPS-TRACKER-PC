namespace GpsTrackerPc.Core.Updates;

public sealed record UpdateApplyResult(bool Success, string? ErrorMessage);

/// <summary>
/// 以「目前 EXE 改名成 .old → 放入新檔 → 重新啟動」替換。
/// 任何一步失敗都會把 .old 搬回原位；下次啟動再清掉 .old。
/// </summary>
public static class UpdateInstaller
{
    public static UpdateApplyResult Apply(
        string downloadedFilePath,
        string currentExePath,
        Action restart)
    {
        if (!File.Exists(downloadedFilePath))
        {
            return new UpdateApplyResult(false, "找不到下載的更新檔");
        }

        if (!File.Exists(currentExePath))
        {
            return new UpdateApplyResult(false, "找不到目前的執行檔");
        }

        var backupPath = currentExePath + ".old";
        try
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            File.Move(currentExePath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateApplyResult(false, "無法備份目前版本：" + ex.Message);
        }

        try
        {
            File.Copy(downloadedFilePath, currentExePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Restore(backupPath, currentExePath);
            return new UpdateApplyResult(false, "無法寫入新版本：" + ex.Message);
        }

        try
        {
            restart();
        }
        catch (Exception ex)
        {
            Restore(backupPath, currentExePath);
            return new UpdateApplyResult(false, "無法重新啟動：" + ex.Message);
        }

        return new UpdateApplyResult(true, null);
    }

    /// <summary>下次啟動時刪掉上次留下的 .old；刪不掉就算了。</summary>
    public static void CleanupOldVersion(string currentExePath)
    {
        try
        {
            var backupPath = currentExePath + ".old";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 舊檔清不掉不影響本次啟動。
        }
    }

    private static void Restore(string backupPath, string currentExePath)
    {
        try
        {
            if (File.Exists(currentExePath))
            {
                File.Delete(currentExePath);
            }

            if (File.Exists(backupPath))
            {
                File.Move(backupPath, currentExePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 還原失敗時保留 .old，讓使用者至少還能手動處理。
        }
    }
}
