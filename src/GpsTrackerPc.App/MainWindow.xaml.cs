using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Data;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace GpsTrackerPc.App;

/// <summary>
/// 桌面版外殼：用 WebView2 載入輸出資料夾裡的 gpstracker-web，
/// 資料改成讀 %LOCALAPPDATA%\GpsTrackerPc\data，並在啟動時自動同步一次。
/// </summary>
public partial class MainWindow : Window
{
    private const string VirtualHost = "gpstracker.local";
    private const string DataHost = "gpstracker-data.local";
    private const string StartPage = "https://gpstracker.local/index.html";

    // 必須在頁面任何 script 執行前注入。
    // 1. 關閉桌面版沒有的功能（拍照／步數／分享）。
    // 2. 把網頁的 data/ 導到另一個虛擬主機，直接讀本機同步下來的資料，不改網頁原始碼。
    private const string DesktopBootstrapScript = """
        window.GT_FEATURES = Object.freeze({ photos: false, steps: false, share: false });
        (function () {
          var dataBase = "https://gpstracker-data.local/";
          var current;
          function wrap(value) {
            if (!value || typeof value.resolveConfig !== "function" || value.__desktopDataBase) return value;
            var original = value.resolveConfig;
            value.resolveConfig = function (search) {
              var config = original.call(this, search);
              config.dataBase = dataBase;
              return config;
            };
            value.__desktopDataBase = true;
            return value;
          }
          Object.defineProperty(window, "GPST", {
            configurable: true,
            get: function () { return current; },
            set: function (value) { current = wrap(value); }
          });
        })();
        """;

    private readonly string _dataDirectory = AppPaths.DataDirectory;
    private readonly TokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly DataSyncService _syncService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _syncStateLock = new();

    private bool _pageLoaded;
    private bool _navigationStarted;
    private bool _pendingReload;
    private bool _syncRunning;

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(_dataDirectory);
        _tokenStore = new TokenStore(AppPaths.GitHubTokenFile);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _syncService = new DataSyncService(_httpClient, _dataDirectory, AppPaths.SyncCacheFile);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 先開始同步，讓 WebView2 初始化與資料下載並行；完成後如果有變更會重新載入頁面。
        _ = RunSyncAsync(automatic: true);

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

            // 網頁本體與資料分開對應；網頁的相對 data/ 由注入的 script 導到 DataHost。
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping(
                DataHost,
                _dataDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            await core.AddScriptToExecuteOnDocumentCreatedAsync(DesktopBootstrapScript);

            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.ProcessFailed += Core_ProcessFailed;

            _navigationStarted = true;
            Browser.Source = new Uri(StartPage);
        }
        catch (Exception ex)
        {
            ShowError("初始化 WebView2 失敗：\n\n" + ex.Message);
        }
    }

    private async Task RunSyncAsync(bool automatic)
    {
        lock (_syncStateLock)
        {
            if (_syncRunning)
            {
                return;
            }

            _syncRunning = true;
        }

        SyncButton.IsEnabled = false;
        SyncButton.Content = "同步中…";
        SyncStatusText.Text = automatic ? "啟動同步中…" : "同步中…";
        SyncStatusText.ToolTip = null;

        try
        {
            var result = await _syncService.SyncAsync(_lifetime.Token);
            UpdateSyncStatus(result);

            if (result.HasChanges && _navigationStarted)
            {
                // 頁面已經載入就立刻重新整理；還在下載中就等載入完成再重整，避免讀到一半新一半舊的資料。
                if (_pageLoaded)
                {
                    Browser.Reload();
                }
                else
                {
                    _pendingReload = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // App 正在關閉。
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = "同步失敗：" + ex.Message;
        }
        finally
        {
            lock (_syncStateLock)
            {
                _syncRunning = false;
            }

            SyncButton.IsEnabled = true;
            SyncButton.Content = "立即同步";
        }
    }

    private void UpdateSyncStatus(SyncResult result)
    {
        var time = result.CompletedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
        if (result.Success)
        {
            SyncStatusText.Text =
                $"上次同步：{time}・成功（下載 {result.DownloadedCount}、更新 {result.UpdatedCount}、略過 {result.SkippedCount}）";
            SyncStatusText.ToolTip = null;
        }
        else
        {
            var detail = result.Errors.Count > 0
                ? string.Join("；", result.Errors.Take(2))
                : "未知錯誤";
            SyncStatusText.Text = $"上次同步：{time}・失敗（{detail}）";
            SyncStatusText.ToolTip = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors)
                : null;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _pageLoaded = true;
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (_pendingReload)
            {
                _pendingReload = false;
                Dispatcher.BeginInvoke(new Action(() => Browser.Reload()));
            }

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

    private async void SyncButton_Click(object sender, RoutedEventArgs e) => await RunSyncAsync(automatic: false);

    private void ExportGpx_Click(object sender, RoutedEventArgs e)
    {
        var window = new ExportWindow(_dataDirectory)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private async void ImportGpx_Click(object sender, RoutedEventArgs e)
    {
        if (!_tokenStore.HasToken)
        {
            var answer = MessageBox.Show(
                this,
                "匯入 GPX 需要 GitHub Token，請先到設定畫面輸入。\n\n要現在開啟設定嗎？",
                "尚未設定 Token",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                OpenSettings();
            }

            return;
        }

        var uploader = new GitHubRecordUploader(_httpClient, _tokenStore);
        var window = new ImportWindow(_tokenStore, uploader)
        {
            Owner = this,
        };
        window.ShowDialog();

        if (window.UploadedCount > 0)
        {
            // 上傳後重新下載 manifest／新紀錄，讓本機資料與網站一致。
            await RunSyncAsync(automatic: false);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new SettingsWindow(_tokenStore)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _lifetime.Cancel();
        _httpClient.Dispose();
        Browser.Dispose();
    }
}
