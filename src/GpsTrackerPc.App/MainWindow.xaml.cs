using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Data;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Settings;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;
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
    private const string WebView2DownloadPage =
        "https://developer.microsoft.com/microsoft-edge/webview2/";

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
    private readonly DeviceNameStore _deviceNameStore;
    private readonly HttpClient _httpClient;
    private readonly DataSyncService _syncService;
    private readonly HttpClient _updateClient;
    private readonly UpdateService _updateService;
    private readonly UpdateCheckStore _updateCheckStore;
    private readonly TraySettingsStore _traySettingsStore;
    private readonly TrayIconService _trayIcon;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _syncStateLock = new();

    private bool _startupUpdateCheckAttempted;
    private bool _pageLoaded;
    private bool _navigationStarted;
    private bool _pendingReload;
    private bool _syncRunning;
    private bool _closing;
    private bool _minimizeToTray;
    private bool _trayHintShown;
    private SimulatorView? _simulatorView;

    /// <summary>主視窗正在關閉（給子視窗判斷是否跟著結束，避免關閉途中跳提醒）。</summary>
    public bool IsClosing => _closing;

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(_dataDirectory);
        _tokenStore = new TokenStore(AppPaths.GitHubTokenFile);
        _deviceNameStore = new DeviceNameStore(AppPaths.SettingsFile);
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _syncService = new DataSyncService(_httpClient, _dataDirectory, AppPaths.SyncCacheFile);
        _updateClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _updateService = new UpdateService(_updateClient);
        _updateCheckStore = new UpdateCheckStore(AppPaths.UpdateCheckFile);
        _traySettingsStore = new TraySettingsStore(AppPaths.TraySettingsFile);
        _minimizeToTray = _traySettingsStore.Load().MinimizeToTray;
        _trayIcon = new TrayIconService(
            ShowFromTray,
            () =>
            {
                ShowFromTray();
                ShowSimulatorView();
            },
            Close);
        _trayIcon.Visible = _minimizeToTray;
        Title = $"GPS Tracker PC v{AppVersion.Current}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 模擬路線編輯改成內嵌在這個視窗，小螢幕時整個視窗也不能超出可用區域。
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(640, workArea.Width - 32));
        MinHeight = Math.Min(MinHeight, Math.Max(480, workArea.Height - 32));
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
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

            if (!IsWebView2RuntimeAvailable())
            {
                ShowWebView2Missing();
                return;
            }

            Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x12, 0x20);

            await Browser.EnsureCoreWebView2Async();
            if (_closing)
            {
                return;
            }

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
            if (_closing)
            {
                return;
            }

            core.NavigationCompleted += Core_NavigationCompleted;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.ProcessFailed += Core_ProcessFailed;

            _navigationStarted = true;
            Browser.Source = new Uri(StartPage);

            // 啟動時一天最多檢查一次；有新版只提示，不會自動安裝。
            _ = RunStartupUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            if (_closing)
            {
                return;
            }

            if (ex is WebView2RuntimeNotFoundException or DllNotFoundException)
            {
                ShowWebView2Missing();
            }
            else
            {
                ShowError("初始化 WebView2 失敗：\n\n" + ex.Message);
            }
        }
    }

    private static bool IsWebView2RuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ShowWebView2Missing()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        WebView2DownloadButton.Visibility = Visibility.Visible;
        ErrorText.Text =
            "找不到 Microsoft Edge WebView2 Runtime。\n\n" +
            "這個程式需要 WebView2 Runtime 才能顯示介面。請下載並安裝「Evergreen Runtime」，" +
            "安裝完成後重新啟動這個程式。\n\n" +
            "下載頁面：" + WebView2DownloadPage;
    }

    private void OpenWebView2Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebView2DownloadPage) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "無法開啟瀏覽器，請手動到以下網址下載：\n\n" + WebView2DownloadPage + "\n\n" + ex.Message,
                "無法開啟下載頁面",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RunSyncAsync(bool automatic)
    {
        if (_closing)
        {
            return;
        }

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
            if (_closing)
            {
                return;
            }

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
            if (!_closing)
            {
                SyncStatusText.Text = "同步失敗：" + ex.Message;
            }
        }
        finally
        {
            lock (_syncStateLock)
            {
                _syncRunning = false;
            }

            if (!_closing)
            {
                SyncButton.IsEnabled = true;
                SyncButton.Content = "立即同步";
            }
        }
    }

    private void UpdateSyncStatus(SyncResult result)
    {
        if (_closing)
        {
            return;
        }

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
        if (_closing)
        {
            return;
        }

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
        if (_closing)
        {
            e.Handled = true;
            return;
        }

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
        if (_closing)
        {
            return;
        }

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            ShowError("WebView2 瀏覽器程序已結束。請重新啟動 App。");
        }
    }

    private void ShowError(string message)
    {
        if (_closing)
        {
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        WebView2DownloadButton.Visibility = Visibility.Collapsed;
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
        var window = new ImportWindow(_tokenStore, _deviceNameStore, uploader)
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

    private void Simulator_Click(object sender, RoutedEventArgs e) => ShowSimulatorView();

    /// <summary>在主視窗內容區顯示模擬路線編輯；第一次使用才建立，之後保留狀態。</summary>
    private void ShowSimulatorView()
    {
        if (_simulatorView is null)
        {
            _simulatorView = new SimulatorView(_tokenStore, _deviceNameStore);
            _simulatorView.BackToHomeRequested += (_, _) => ShowHomePage();
            SimulatorHost.Children.Add(_simulatorView);
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        SimulatorHost.Visibility = Visibility.Visible;
        _simulatorView.Focus();
    }

    /// <summary>從工具列圖示區回到主視窗。</summary>
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_closing || !_minimizeToTray || WindowState != WindowState.Minimized)
        {
            return;
        }

        // 縮小到系統匣：視窗隱藏但程式、同步與模擬照常運作。
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloon("程式已縮到工具列圖示區，模擬會繼續在背景執行。");
        }
    }

    /// <summary>切回紀錄網站首頁，模擬路線編輯保留在背景。</summary>
    public void ShowHomePage()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        SimulatorHost.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;
        if (Browser.CoreWebView2 != null)
        {
            Browser.CoreWebView2.Navigate(StartPage);
        }
        else
        {
            Browser.Source = new Uri(StartPage);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var window = new SettingsWindow(
            _tokenStore,
            _deviceNameStore,
            _updateCheckStore,
            () =>
            {
                ShowUpdateWindow(initialUpdate: null);
                return Task.CompletedTask;
            })
        {
            Owner = this,
        };
        window.ShowDialog();

        // 設定畫面可能改了「縮小到系統匣」，回來後重新套用。
        _minimizeToTray = _traySettingsStore.Load().MinimizeToTray;
        _trayIcon.Visible = _minimizeToTray;
    }

    private void CheckCloudUpdates_Click(object sender, RoutedEventArgs e) =>
        ShowUpdateWindow(initialUpdate: null);

    private void ShowUpdateWindow(UpdateInfo? initialUpdate)
    {
        var window = new UpdateWindow(
            _updateService,
            _updateCheckStore,
            AppVersion.Current,
            initialUpdate)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        if (_startupUpdateCheckAttempted)
        {
            return;
        }

        _startupUpdateCheckAttempted = true;
        var now = DateTimeOffset.Now;
        if (!_updateCheckStore.ShouldAutoCheck(now))
        {
            return;
        }

        UpdateInfo? update = null;
        try
        {
            update = await _updateService.CheckAsync(AppVersion.Current, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // 啟動檢查失敗不跳錯誤視窗，避免打擾。
        }
        finally
        {
            try
            {
                _updateCheckStore.MarkChecked(now);
            }
            catch (Exception)
            {
                // 記錄時間失敗不影響 App。
            }
        }

        if (update == null)
        {
            return;
        }

        if (_closing)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"有新版本 v{update.Version}（目前 v{AppVersion.Current}）。\n\n要開啟更新畫面嗎？",
            "發現新版本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            ShowUpdateWindow(update);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_simulatorView is { HasUnsavedChanges: true })
        {
            var answer = MessageBox.Show(
                this,
                "模擬路線尚未儲存，結束程式後變更會消失。\n\n確定要結束嗎？",
                "尚未儲存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _closing = true;
        _simulatorView?.Shutdown();

        // 先取消背景工作，並解除 WebView2 事件，避免同步／導覽回來時再碰已關閉的視窗。
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception)
        {
            // 取消失敗不影響關閉。
        }

        try
        {
            if (Browser.CoreWebView2 is { } core)
            {
                core.NavigationCompleted -= Core_NavigationCompleted;
                core.NewWindowRequested -= Core_NewWindowRequested;
                core.ProcessFailed -= Core_ProcessFailed;
            }
        }
        catch (Exception)
        {
            // WebView2 可能還沒初始化完成。
        }

        try
        {
            _httpClient.Dispose();
        }
        catch (Exception)
        {
            // 網路資源可能已被背景工作釋放。
        }

        try
        {
            _updateClient.Dispose();
        }
        catch (Exception)
        {
        }

        _trayIcon.Dispose();

        // 不在 Closing 呼叫 Browser.Dispose()：WebView2 初始化中或關閉訊息迴圈時，
        // 同步釋放可能卡住 UI 執行緒，讓程式看起來「無回應」；程式結束時會一併釋放。
    }
}
