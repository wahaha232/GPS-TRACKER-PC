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

        // 只接受 EXE；舊版 Release 若只提供 ZIP，直接覆蓋 EXE 會讓程式再也開不起來。
        if (!string.Equals(Path.GetExtension(downloadedFilePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateApplyResult(false, "更新檔不是 EXE，已停止安裝：" + Path.GetFileName(downloadedFilePath));
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

    /// <summary>保留上一個版本 7 天供回復；超過時間才刪掉。</summary>
    public static void CleanupOldVersion(string currentExePath)
    {
        try
        {
            var backupPath = currentExePath + ".old";
            if (File.Exists(backupPath) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(backupPath) > TimeSpan.FromDays(7))
            {
                File.Delete(backupPath);
            }

            // 回復版本時被換下來的檔案；下次啟動已不在使用中，可以刪。
            var failedPath = currentExePath + ".failed";
            if (File.Exists(failedPath))
            {
                File.Delete(failedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 舊檔清不掉不影響本次啟動。
        }
    }

    /// <summary>把上一個版本（.old）換回來，並要求呼叫端重啟。</summary>
    public static UpdateApplyResult Rollback(string currentExePath, Action restart)
    {
        var backupPath = currentExePath + ".old";
        if (!File.Exists(backupPath))
        {
            return new UpdateApplyResult(false, "找不到上一個版本（.old）");
        }

        var failedPath = currentExePath + ".failed";
        try
        {
            if (File.Exists(failedPath))
            {
                File.Delete(failedPath);
            }

            File.Move(currentExePath, failedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateApplyResult(false, "無法暫存目前版本：" + ex.Message);
        }

        try
        {
            File.Copy(backupPath, currentExePath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Restore(failedPath, currentExePath);
            return new UpdateApplyResult(false, "無法還原上一個版本：" + ex.Message);
        }

        try
        {
            restart();
        }
        catch (Exception ex)
        {
            Restore(failedPath, currentExePath);
            return new UpdateApplyResult(false, "無法重新啟動：" + ex.Message);
        }

        try
        {
            File.Delete(failedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 下次啟動再清。
        }

        return new UpdateApplyResult(true, null);
    }

    /// <summary>
    /// 清掉已安裝或更舊版本的背景下載資料夾，以及放了 7 天以上的 .part；
    /// 比目前版本新的下載會保留，讓使用者重啟後還能續傳／安裝。
    /// </summary>
    public static void CleanupDownloads(string directory, string currentVersion)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var part in Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(part) > TimeSpan.FromDays(7))
                    {
                        File.Delete(part);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                var folderVersion = Path.GetFileName(subdirectory);
                if (!string.IsNullOrWhiteSpace(folderVersion) &&
                    UpdateVersion.VersionOf(folderVersion) != null &&
                    !UpdateVersion.IsNewer(currentVersion, folderVersion))
                {
                    try
                    {
                        Directory.Delete(subdirectory, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉不影響程式。
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
