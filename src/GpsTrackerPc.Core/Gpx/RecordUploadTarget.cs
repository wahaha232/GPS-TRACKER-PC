namespace GpsTrackerPc.Core.Gpx;

/// <summary>
/// 紀錄上傳的目的地：公開的 GitHub 倉庫，網站儀表板會讀取這裡的資料。
/// 路線、時間與裝置名稱任何人都看得到。
/// </summary>
public static class RecordUploadTarget
{
    public const string Owner = "wahaha232";
    public const string Repo = "ANDROID-APP-DEV";
    public const string Branch = "main";
    public const string DataPath = "gpstracker-web/data";

    public static string DisplayName => $"{Owner}/{Repo}";
}
