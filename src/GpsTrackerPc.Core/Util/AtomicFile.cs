namespace GpsTrackerPc.Core.Util;

/// <summary>
/// 先寫同一個資料夾的暫存檔，再改名覆蓋目標檔。
/// 寫入或改名失敗時保留原本的檔案，不會留下半寫入的內容。
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("檔案路徑沒有資料夾", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(fullPath) + ".tmp-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // 清不掉暫存檔不影響已完成的目標檔；下次同步會再嘗試。
                }
            }
        }
    }
}
