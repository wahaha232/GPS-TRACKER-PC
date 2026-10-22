using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace GpsTrackerPc.App;

/// <summary>
/// 桌面版外殼：用 WebView2 載入輸出資料夾裡的 gpstracker-web，
/// 並在每個頁面建立前注入桌面版功能開關（關閉拍照、步數、分享）。
/// </summary>
public partial class MainWindow : Window
{
    private const string VirtualHost = "gpstracker.local";
    private const string StartPage = "https://gpstracker.local/index.html";

    // 必須在頁面任何 script 執行前注入；沒有這個開關時（手機、GitHub Pages）網頁維持原樣。
    private const string FeatureFlagsScript = """
        window.GT_FEATURES = Object.freeze({ photos: false, steps: false, share: false });
        """;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            var indexFile = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexFile))
            {
                ShowError(
                    "找不到輸出資料夾裡的 web\\index.html。\n\n" +
                    $"預期位置：{indexFile}\n\n" +
                    "請重新建置專案；建置時會從 gpstracker-web 複製網頁到輸出資料夾。");
                return;
            }

            Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x12, 0x20);

            await Browser.EnsureCoreWebView2Async();
            var core = Browser.CoreWebView2;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            // https://gpstracker.local/... 對應到輸出資料夾；網頁的相對路徑不必修改。
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            await core.AddScriptToExecuteOnDocumentCreatedAsync(FeatureFlagsScript);

            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.ProcessFailed += Core_ProcessFailed;

            Browser.Source = new Uri(StartPage);
        }
        catch (Exception ex)
        {
            ShowError("初始化 WebView2 失敗：\n\n" + ex.Message);
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError($"載入本機網頁失敗（{e.WebErrorStatus}）。\n\n請確認 WebView2 Runtime 已安裝，且輸出資料夾的 web\\ 內容完整。");
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 外部連結（例如地圖 attribution）交給預設瀏覽器，不在 App 內另開視窗。
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            ShowError("WebView2 瀏覽器程序已結束。請重新啟動 App。");
        }
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Browser.Dispose();
    }
}
