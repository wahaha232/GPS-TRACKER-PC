using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Appearance;
using GpsTrackerPc.Core.Settings;

namespace GpsTrackerPc.App;

public partial class App : Application
{
    private bool _crashReported;

    /// <summary>設定的本機資料夾無法使用時，這裡會有提示文字，主視窗載入後顯示。</summary>
    public static string? StartupDataDirectoryWarning { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 先註冊例外攔截，任何視窗開啟前的錯誤也要留下記錄。
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        ThemeManager.Apply(new ThemeSettingsStore(AppPaths.ThemeFile).Load());

        // 本機資料夾可在設定畫面指定；必須在主視窗建立前套用，同步與網頁對應才會用新路徑。
        var storage = new StorageSettingsStore(AppPaths.StorageSettingsFile).Load();
        if (!string.IsNullOrWhiteSpace(storage.DataDirectory))
        {
            if (!AppPaths.TryApplyDataDirectory(storage.DataDirectory, out var error))
            {
                StartupDataDirectoryWarning =
                    $"設定的本機資料夾無法使用，已改用預設資料夾：\n{AppPaths.DefaultDataDirectory}\n\n" +
                    $"原因：{error}\n\n紀錄清單可能是空的，請到設定畫面重新選擇資料夾。";
            }
        }

        // 上次更新留下的 .old 不在這裡刪：要等新版本真的啟動成功，
        // 由主視窗在網頁載入完成後呼叫 UpdateInstaller.CleanupOldVersion。

        base.OnStartup(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("UI 執行緒未處理例外", e.Exception);

        // 不要讓程式直接消失：記錄後攔下來，並提示使用者記錄檔位置。
        e.Handled = true;

        if (_crashReported)
        {
            return;
        }

        _crashReported = true;
        try
        {
            MessageBox.Show(
                "程式發生未預期的錯誤，已寫入記錄檔：\n\n" +
                AppPaths.CrashLogFile +
                "\n\n畫面可能不太正常，建議關閉程式後重新啟動。",
                "發生錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception)
        {
            // 連提示都失敗時至少已經留下記錄。
        }
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog("非 UI 執行緒未處理例外", exception);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("未觀察的工作例外", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var entry =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}{Environment.NewLine}" +
                exception + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(AppPaths.CrashLogFile, entry, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 寫記錄失敗時不要再丟出新的例外。
        }
    }
}
