using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Data;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Settings;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;
using GpsTrackerPc.Simulation.Routes;
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
    private readonly SimulationResumeStore _resumeStore;
    private readonly ResumePreferenceStore _resumePreference;
    private readonly PendingRecordStore _pendingRecords;
    private readonly UploadQuotaStore _uploadQuota;
    private readonly RouteLibraryStore _routeLibrary;
    private readonly DispatcherTimer _pendingUploadTimer;
    private readonly DispatcherTimer _networkTimer;
    private readonly TraySettingsStore _traySettingsStore;
    private readonly TrayIconService _trayIcon;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _syncStateLock = new();

    private bool _startupUpdateCheckAttempted;
    private bool _oldVersionCleaned;
    private bool _pageLoaded;
    private bool _navigationStarted;
    private bool _pendingReload;
    private bool _syncRunning;
    private bool _closing;
    private bool _minimizeToTray;
    private bool _trayHintShown;
    private bool _isOnline;
    private bool _pendingUploadRunning;
    private bool _backgroundUpdateRunning;
    private bool _backgroundDownloadPaused;
    private bool _restartPending;
    private bool _restartPromptShown;
    private int _backgroundDownloadAttempt;
    private UpdateInfo? _downloadedUpdate;
    private UpdateInfo? _backgroundUpdateInfo;
    private string? _downloadedUpdatePath;
    private string? _installedUpdateVersion;
    private CancellationTokenSource? _backgroundDownloadCts;
    private DispatcherTimer? _downloadRetryTimer;
    private SimulatorView? _simulatorView;
    private FriendsView? _friendsView;

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
        // 逾時由 UpdateService 分開控制（查詢 60 秒、下載 30 分鐘），
        // 不能讓 HttpClient 的整體逾時把 200 MB 的下載中途砍掉。
        _updateClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        // 公開 repo 不需要 Token；但已設定的 Token 可以避免未登入每小時 60 次的查詢上限。
        _updateService = new UpdateService(_updateClient, tokenProvider: () => _tokenStore.Load());
        _updateCheckStore = new UpdateCheckStore(AppPaths.UpdateCheckFile);
        _resumeStore = new SimulationResumeStore(AppPaths.RestartSessionFile);
        _resumePreference = new ResumePreferenceStore(AppPaths.ResumePreferenceFile);
        _pendingRecords = new PendingRecordStore(AppPaths.PendingRecordsDirectory);
        _uploadQuota = new UploadQuotaStore(AppPaths.UploadQuotaFile);
        _routeLibrary = new RouteLibraryStore(AppPaths.RoutesDirectory);
        _pendingUploadTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _pendingUploadTimer.Tick += async (_, _) => await TryUploadPendingRecordsAsync();
        _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _networkTimer.Tick += (_, _) => UpdateNetworkState();
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailability_Changed;
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

    private void Window_Activated(object? sender, EventArgs e) => RefreshPendingButton();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (App.StartupDataDirectoryWarning is { } dataWarning)
        {
            MessageBox.Show(
                this,
                dataWarning,
                "資料夾無法使用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

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

            // 啟動時一天最多檢查一次；有新版可依設定背景下載或自動安裝，重啟仍由使用者決定。
            _ = RunStartupUpdateCheckAsync();
            _pendingUploadTimer.Start();
            _networkTimer.Start();
            _ = HandleRestartPromptAsync();
            _ = TryUploadPendingRecordsAsync();
            RefreshPendingButton();
            if (!_isOnline)
            {
                OfflineBanner.Visibility = Visibility.Visible;
                SyncStatusText.Text = "網路中斷：背景補傳與紀錄同步已暫停，恢復連線後會自動繼續。";
            }
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
            CleanupOldVersionOnce();
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

    /// <summary>新版本真正把首頁載入成功後才刪掉上一個版本，啟動失敗才留有還原機會。</summary>
    private void CleanupOldVersionOnce()
    {
        if (_oldVersionCleaned)
        {
            return;
        }

        _oldVersionCleaned = true;
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            UpdateInstaller.CleanupOldVersion(executable);
        }

        // 新版本已成功載入頁面，背景下載的暫存檔可以清掉；.old 會保留 7 天供回復。
        UpdateInstaller.CleanupDownloads(AppPaths.UpdateDownloadDirectory, AppVersion.Current);
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

    private void Friends_Click(object sender, RoutedEventArgs e) => ShowFriendsView();

    /// <summary>在主視窗內容區顯示模擬路線編輯；第一次使用才建立，之後保留狀態。</summary>
    private void ShowSimulatorView()
    {
        if (_simulatorView is null)
        {
            _simulatorView = new SimulatorView(_tokenStore, _deviceNameStore);
            _simulatorView.BackToHomeRequested += (_, _) => ShowHomePage();
            SimulatorHost.Children.Add(_simulatorView);
        }

        _friendsView?.SetActive(false);
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        FriendsHost.Visibility = Visibility.Collapsed;
        SimulatorHost.Visibility = Visibility.Visible;
        _simulatorView.Focus();
    }

    /// <summary>好友／群組是獨立分頁；第一次開啟才建立，之後保留清單與地圖狀態。</summary>
    private void ShowFriendsView()
    {
        if (_friendsView is null)
        {
            _friendsView = new FriendsView(_deviceNameStore);
            _friendsView.BackToHomeRequested += (_, _) => ShowHomePage();
            FriendsHost.Children.Add(_friendsView);
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        SimulatorHost.Visibility = Visibility.Collapsed;
        FriendsHost.Visibility = Visibility.Visible;
        _friendsView.SetActive(true);
        _friendsView.Focus();
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
        _friendsView?.SetActive(false);
        SimulatorHost.Visibility = Visibility.Collapsed;
        FriendsHost.Visibility = Visibility.Collapsed;
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
            },
            OpenPendingRecords,
            RollbackToPreviousVersion)
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
            initialUpdate,
            _updateCheckStore.Load().Channel,
            RestartForUpdate)
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
            var startupSettings = _updateCheckStore.Load();
            update = await _updateService.CheckAsync(AppVersion.Current, startupSettings.Channel, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // 啟動檢查失敗不跳錯誤視窗；也不記錄檢查時間，下次啟動會再試。
            return;
        }

        try
        {
            _updateCheckStore.MarkChecked(now);
        }
        catch (Exception)
        {
            // 記錄時間失敗不影響 App。
        }

        if (update == null)
        {
            return;
        }

        if (_closing)
        {
            return;
        }

        var settings = _updateCheckStore.Load();
        if (settings.BackgroundDownloadEnabled)
        {
            await DownloadBackgroundUpdateAsync(update);
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

    /// <summary>背景下載到本機 updates 資料夾；依設定直接安裝或等使用者按「安裝更新」。</summary>
    private async Task DownloadBackgroundUpdateAsync(UpdateInfo update, bool isRetry = false)
    {
        if (_backgroundUpdateRunning || _installedUpdateVersion != null)
        {
            return;
        }

        if (!isRetry)
        {
            _backgroundDownloadAttempt = 0;
        }

        if (!string.Equals(Path.GetExtension(update.AssetName), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowUpdateBanner(
                $"v{update.Version} 只提供 ZIP，無法背景下載安裝；請按「檢查雲端更新」手動處理。",
                showInstall: false,
                showRestart: false);
            return;
        }

        var settings = _updateCheckStore.Load();
        if (settings.DownloadOnlyOnUnmetered && NetworkCostHeuristic.IsLikelyMetered())
        {
            ShowUpdateBanner(
                "目前是行動網路（可能計量），已依設定暫停背景下載；可改用「檢查雲端更新」手動下載。",
                showInstall: false,
                showRestart: false);
            return;
        }

        var destination = Path.Combine(
            AppPaths.UpdateDownloadDirectory,
            SafeVersionFolder(update.Version),
            "GpsTrackerPc.exe");
        var requiredBytes = update.Size > 0
            ? (long)(update.Size * 1.2) + 10 * 1024 * 1024
            : 250L * 1024 * 1024;
        if (!UpdateInstallChecks.HasFreeSpace(
                Path.GetDirectoryName(destination) ?? AppPaths.Root,
                requiredBytes,
                out var availableBytes))
        {
            ShowUpdateBanner(
                $"磁碟空間不足：需要約 {FormatBytes(requiredBytes)}，可用 {FormatBytes(availableBytes)}。",
                showInstall: false,
                showRestart: false);
            return;
        }

        _backgroundUpdateRunning = true;
        _backgroundUpdateInfo = update;
        _backgroundDownloadPaused = false;
        _backgroundDownloadCts?.Dispose();
        _backgroundDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            if (!File.Exists(destination))
            {
                ShowUpdateBanner($"正在背景下載更新 v{update.Version}… 0%", false, false, showPause: true);
                var downloadFinished = false;
                var progress = new Progress<UpdateDownloadProgress>(value =>
                {
                    if (downloadFinished || _closing || _backgroundDownloadPaused)
                    {
                        return;
                    }

                    ShowUpdateBanner(
                        $"正在背景下載更新 v{update.Version}… " +
                        (value.Fraction is { } fraction ? fraction.ToString("P0") : $"{FormatBytes(value.BytesReceived)}") +
                        $"・{FormatBytes(value.BytesPerSecond)}/s",
                        showInstall: false,
                        showRestart: false,
                        showPause: true);
                });
                try
                {
                    await _updateService.DownloadWithResumeAsync(
                        update,
                        destination,
                        progress,
                        _backgroundDownloadCts.Token);
                }
                finally
                {
                    downloadFinished = true;
                }
            }

            if (_closing)
            {
                return;
            }

            _backgroundDownloadAttempt = 0;
            _downloadRetryTimer?.Stop();
            _downloadedUpdate = update;
            _downloadedUpdatePath = destination;
            if (_updateCheckStore.Load().InstallMode == UpdateInstallMode.Automatic)
            {
                InstallDownloadedUpdate(confirm: false);
            }
            else
            {
                ShowUpdateBanner(
                    $"更新 v{update.Version} 已下載（目前 v{AppVersion.Current}）。",
                    showInstall: true,
                    showRestart: false);
                NotifyTray($"更新 v{update.Version} 已下載，可在工具列按「安裝更新」。");
            }
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            _backgroundDownloadPaused = true;
            var partPath = destination + ".part";
            var downloaded = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            ShowUpdateBanner(
                $"已暫停下載更新 v{update.Version}（已下載 {FormatBytes(downloaded)}，可繼續）。",
                showInstall: false,
                showRestart: false,
                showPause: true);
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ScheduleDownloadRetry(update, ex.Message);
            }
        }
        finally
        {
            _backgroundUpdateRunning = false;
        }
    }

    private void PauseDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_backgroundDownloadPaused && _backgroundUpdateInfo != null)
        {
            _backgroundDownloadPaused = false;
            _ = DownloadBackgroundUpdateAsync(_backgroundUpdateInfo, isRetry: true);
            return;
        }

        if (_backgroundUpdateRunning)
        {
            _backgroundDownloadPaused = true;
            _backgroundDownloadCts?.Cancel();
            PauseDownloadButton.Content = "繼續下載";
        }
    }

    private void ScheduleDownloadRetry(UpdateInfo update, string error)
    {
        _backgroundDownloadAttempt++;
        if (_backgroundDownloadAttempt > 3)
        {
            ShowUpdateBanner(
                $"背景下載更新失敗：{error}\n已停止自動重試，可用「檢查雲端更新」手動下載。",
                showInstall: false,
                showRestart: false);
            NotifyTray("背景下載更新失敗，已停止自動重試。");
            return;
        }

        var delay = _backgroundDownloadAttempt switch
        {
            1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(10),
        };
        ShowUpdateBanner(
            $"背景下載更新失敗：{error}\n{delay.TotalMinutes:0.#} 分鐘後自動重試（第 {_backgroundDownloadAttempt}/3 次）。",
            showInstall: false,
            showRestart: false);
        _downloadRetryTimer?.Stop();
        _downloadRetryTimer = new DispatcherTimer { Interval = delay };
        _downloadRetryTimer.Tick += (_, _) =>
        {
            _downloadRetryTimer?.Stop();
            if (!_closing)
            {
                _ = DownloadBackgroundUpdateAsync(update, isRetry: true);
            }
        };
        _downloadRetryTimer.Start();
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024 * 1024):0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024:0.0} KB";
        }

        return $"{bytes:0} B";
    }

    private void NotifyTray(string message)
    {
        if (_trayIcon.Visible)
        {
            _trayIcon.ShowBalloon(message);
        }
    }

    private static string SafeVersionFolder(string version) =>
        new(version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_').ToArray());

    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => InstallDownloadedUpdate(confirm: true);

    /// <summary>替換目前 EXE；不會立刻重啟，工具列會顯示重啟提示。</summary>
    private void InstallDownloadedUpdate(bool confirm)
    {
        var update = _downloadedUpdate;
        var path = _downloadedUpdatePath;
        if (update == null || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ShowUpdateBanner("找不到已下載的更新檔，請重新檢查更新。", false, false);
            return;
        }

        var signature = UpdateSignature.Inspect(path);
        if (signature.Status == UpdateSignatureStatus.Invalid)
        {
            ShowUpdateBanner(
                "更新檔數位簽章不合法，已停止安裝：" + (signature.Error ?? "簽章驗證失敗"),
                showInstall: false,
                showRestart: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "GpsTrackerPc.exe");
        var requiredBytes = new FileInfo(path).Length * 2 + 50L * 1024 * 1024;
        if (!UpdateInstallChecks.HasFreeSpace(
                Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                requiredBytes,
                out var availableBytes))
        {
            ShowUpdateBanner(
                $"安裝空間不足：需要約 {FormatBytes(requiredBytes)}，可用 {FormatBytes(availableBytes)}。",
                showInstall: false,
                showRestart: false);
            return;
        }

        if (confirm)
        {
            var unsignedNote = signature.Status == UpdateSignatureStatus.Unsigned
                ? "\n\n注意：此更新檔沒有數位簽章，僅以 SHA-256 檢查碼驗證。"
                : $"\n\n簽章：{signature.Subject}";
            var answer = MessageBox.Show(
                this,
                $"要安裝 v{update.Version} 嗎？\n\n安裝後模擬會在重啟時暫停，重啟後再選擇是否繼續。" +
                unsignedNote,
                "安裝更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var apply = UpdateInstaller.Apply(path, executable, restart: () => { });
        if (!apply.Success)
        {
            ShowUpdateBanner("安裝更新失敗，已保留舊版：" + apply.ErrorMessage, false, false);
            return;
        }

        _installedUpdateVersion = update.Version;
        _downloadedUpdate = null;
        _downloadedUpdatePath = null;
        _backgroundUpdateInfo = null;
        _backgroundDownloadPaused = false;
        ShowUpdateBanner(
            $"更新 v{update.Version} 已安裝。請重啟程式以套用新版本。" +
            (signature.Status == UpdateSignatureStatus.Unsigned ? "（未簽章，已用 SHA-256 驗證）" : string.Empty),
            showInstall: false,
            showRestart: true);
        NotifyTray($"更新 v{update.Version} 已安裝，請重啟程式。");

        if (!confirm)
        {
            MessageBox.Show(
                this,
                $"更新 v{update.Version} 已安裝，重啟程式後才會生效。\n\n" +
                "重啟前會先暫停目前的模擬路線，下次啟動可以選擇繼續或存檔。" +
                (signature.Status == UpdateSignatureStatus.Unsigned
                    ? "\n\n此版本沒有數位簽章，已用 SHA-256 檢查碼驗證。"
                    : string.Empty),
                "已安裝更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) => RestartForUpdate();

    private void RestartForUpdate()
    {
        if (_restartPending)
        {
            return;
        }

        var snapshot = _simulatorView?.PrepareForRestart();
        if (snapshot != null)
        {
            try
            {
                _resumeStore.Save(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    this,
                    "無法暫存模擬路線，已取消重啟：\n\n" + ex.Message,
                    "重啟失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _restartPending = true;
        try
        {
            var executable = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "GpsTrackerPc.exe");
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Arguments = "--restarted-for-update",
            });
        }
        catch (Exception ex)
        {
            _restartPending = false;
            MessageBox.Show(
                this,
                "無法重新啟動程式，請手動關閉後再開啟：\n\n" + ex.Message,
                "重啟失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _closing = true;
        _simulatorView?.Shutdown();
        _friendsView?.Shutdown();
        Application.Current.Shutdown();
    }

    /// <summary>設定畫面的「回復上一個版本」：換回 .old 後重啟。</summary>
    private void RollbackToPreviousVersion()
    {
        var executable = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "GpsTrackerPc.exe");
        if (!File.Exists(executable + ".old"))
        {
            MessageBox.Show(
                this,
                "找不到上一個版本。已經成功啟動過新版本一段時間，舊版備份可能已被清理。",
                "無法回復",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "要換回上一個版本並重新啟動嗎？\n\n目前的模擬路線會先暫停並暫存。",
            "回復上一個版本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var snapshot = _simulatorView?.PrepareForRestart();
        if (snapshot != null)
        {
            try
            {
                _resumeStore.Save(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    this,
                    "無法暫存模擬路線，已取消回復：\n\n" + ex.Message,
                    "回復失敗",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _restartPending = true;
        var result = UpdateInstaller.Rollback(executable, () =>
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Arguments = "--restarted-for-update",
            });
        });
        if (!result.Success)
        {
            _restartPending = false;
            MessageBox.Show(
                this,
                "無法回復上一個版本：\n\n" + result.ErrorMessage,
                "回復失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _closing = true;
        _simulatorView?.Shutdown();
        _friendsView?.Shutdown();
        Application.Current.Shutdown();
    }

    /// <summary>上次因為更新重啟時有暫停的模擬路線，詢問要繼續、存檔或放棄。</summary>
    private async Task HandleRestartPromptAsync()
    {
        if (_restartPromptShown)
        {
            return;
        }

        var snapshot = _resumeStore.Load();
        if (snapshot == null)
        {
            return;
        }

        _restartPromptShown = true;
        await Task.Delay(300);
        if (_closing)
        {
            return;
        }

        var preference = _resumePreference.Load();
        var choice = preference == RestartPromptChoice.AlwaysAsk
            ? ResumeSimulationWindow.ShowChoice(this, snapshot)
            : new ResumePromptResult(
                preference switch
                {
                    RestartPromptChoice.Continue => ResumeSimulationChoice.Continue,
                    RestartPromptChoice.SaveAndEnd => ResumeSimulationChoice.SaveAndEnd,
                    _ => ResumeSimulationChoice.Discard,
                },
                Remember: false);

        if (choice.Remember)
        {
            try
            {
                _resumePreference.Save(choice.Choice switch
                {
                    ResumeSimulationChoice.Continue => RestartPromptChoice.Continue,
                    ResumeSimulationChoice.SaveAndEnd => RestartPromptChoice.SaveAndEnd,
                    _ => RestartPromptChoice.Discard,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 記不住選擇不影響本次處理。
            }
        }

        switch (choice.Choice)
        {
            case ResumeSimulationChoice.Continue:
                try
                {
                    ShowSimulatorView();
                    if (_simulatorView != null)
                    {
                        await _simulatorView.RestoreRestartSnapshotAsync(snapshot);
                        _resumeStore.Clear();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "無法繼續上次的模擬路線：\n\n" + ex.Message + "\n\n暫存資料已保留，下次啟動還會再詢問。",
                        "無法繼續模擬",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                break;
            case ResumeSimulationChoice.SaveAndEnd:
                SaveRestartSnapshot(snapshot);
                _resumeStore.Clear();
                break;
            default:
                _resumeStore.Clear();
                break;
        }
    }

    /// <summary>有取樣點就存成待上傳紀錄；只有路線草稿就存到本機路線庫。</summary>
    private void SaveRestartSnapshot(SimulationResumeSnapshot snapshot)
    {
        if (snapshot.Samples.Count < 2)
        {
            TrySaveDraftRoute(snapshot);
            return;
        }

        SaveSnapshotAsPendingRecord(snapshot);
    }

    private void TrySaveDraftRoute(SimulationResumeSnapshot snapshot)
    {
        try
        {
            var route = RouteJsonCodec.Decode(snapshot.RouteJson);
            if (route == null)
            {
                ShowUpdateBanner("路線草稿格式不正確，無法存檔。", false, false);
                return;
            }

            _routeLibrary.Save(route);
            SyncStatusText.Text = $"路線草稿「{route.Name}」已存到本機路線庫。";
        }
        catch (Exception ex)
        {
            ShowUpdateBanner("路線草稿存檔失敗：" + ex.Message, false, false);
        }
    }

    private void SaveSnapshotAsPendingRecord(SimulationResumeSnapshot snapshot)
    {
        try
        {
            var startTime = DateTimeOffset.Now.AddSeconds(-Math.Max(0, snapshot.ElapsedSeconds));
            var build = SimulatedRecordBuilder.BuildFromResumeSnapshot(
                snapshot,
                startTime,
                _deviceNameStore.Load());
            if (!build.IsSuccess)
            {
                ShowUpdateBanner($"存檔失敗：{GpxErrors.Get(build.Error!)}", false, false);
                return;
            }

            _pendingRecords.Save(build);
            SyncStatusText.Text = "模擬路線已存到本機待上傳清單，稍後會自動上傳。";
            RefreshPendingButton();
            _ = TryUploadPendingRecordsAsync();
        }
        catch (Exception ex)
        {
            ShowUpdateBanner("存檔失敗：" + ex.Message, false, false);
        }
    }

    /// <summary>定期比對並補傳本機待上傳的模擬紀錄；離線或沒有 Token 時略過。</summary>
    private async Task TryUploadPendingRecordsAsync()
    {
        if (_pendingUploadRunning || _closing || !_isOnline || !_tokenStore.HasToken)
        {
            RefreshPendingButton();
            return;
        }

        var settings = _updateCheckStore.Load();
        if (!settings.AutoUploadPendingRecords)
        {
            RefreshPendingButton();
            return;
        }

        var pending = _pendingRecords.LoadAll();
        if (pending.Count == 0)
        {
            RefreshPendingButton();
            return;
        }

        var remaining = _uploadQuota.Remaining(settings.AutoUploadDailyLimit, DateTimeOffset.Now);
        if (remaining <= 0)
        {
            SyncStatusText.Text =
                $"今日自動補傳已達上限（{settings.AutoUploadDailyLimit} 筆），可在「待上傳」手動補傳。";
            RefreshPendingButton();
            return;
        }

        _pendingUploadRunning = true;
        try
        {
            var uploader = new PendingRecordUploader(
                _pendingRecords,
                new GitHubRecordUploader(_httpClient, _tokenStore));
            var report = await uploader.UploadAllAsync(remaining, _lifetime.Token);
            if (report.Uploaded > 0)
            {
                _uploadQuota.Consume(report.Uploaded, DateTimeOffset.Now);
            }

            if (report.Failed > 0 && !_closing)
            {
                SyncStatusText.Text = "自動補傳失敗：" + (report.LastError ?? "未知錯誤") +
                    "。可在「待上傳」查看並手動重試。";
                NotifyTray("自動補傳失敗，待上傳紀錄已保留。");
            }
            else if (report.Removed > 0 && !_closing)
            {
                SyncStatusText.Text = $"已自動補傳 {report.Removed} 筆模擬紀錄。";
            }
        }
        catch (OperationCanceledException)
        {
            // 關閉程式時取消。
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SyncStatusText.Text = "自動補傳失敗：" + ex.Message + "。可在「待上傳」手動重試。";
                NotifyTray("自動補傳失敗，待上傳紀錄已保留。");
            }
        }
        finally
        {
            _pendingUploadRunning = false;
            RefreshPendingButton();
        }
    }

    private void RefreshPendingButton()
    {
        try
        {
            var count = _pendingRecords.LoadAll().Count;
            PendingRecordsButton.Content = $"待上傳 ({count})";
            PendingRecordsButton.Foreground = count > 0
                ? (System.Windows.Media.Brush)FindResource("AppWarningText")
                : (System.Windows.Media.Brush)FindResource("AppText");
        }
        catch (Exception)
        {
            // 讀取失敗不影響程式。
        }
    }

    private void PendingRecords_Click(object sender, RoutedEventArgs e) => OpenPendingRecords();

    private void OpenPendingRecords()
    {
        var window = new PendingRecordsWindow(
            _pendingRecords,
            new GitHubRecordUploader(_httpClient, _tokenStore),
            RefreshPendingButton)
        {
            Owner = this,
        };
        window.ShowDialog();
        RefreshPendingButton();
    }

    private void NetworkAvailability_Changed(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateNetworkState));
    }

    private void UpdateNetworkState()
    {
        var online = NetworkInterface.GetIsNetworkAvailable();
        if (online == _isOnline)
        {
            return;
        }

        _isOnline = online;
        OfflineBanner.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        if (online)
        {
            SyncStatusText.Text = "網路已恢復，正在繼續模擬並補傳紀錄…";
            _simulatorView?.ResumeAfterNetworkOutage();
            if (_downloadRetryTimer != null)
            {
                _downloadRetryTimer.Stop();
                _downloadRetryTimer = null;
                if (_backgroundUpdateInfo != null && !_backgroundDownloadPaused)
                {
                    _ = DownloadBackgroundUpdateAsync(_backgroundUpdateInfo, isRetry: true);
                }
            }

            _ = TryUploadPendingRecordsAsync();
            _ = RunSyncAsync(automatic: true);
        }
        else
        {
            SyncStatusText.Text = "網路中斷：模擬已暫停，待上傳紀錄會保留。";
            OfflineBannerText.Text = _simulatorView != null
                ? "網路中斷：模擬與背景補傳已暫停，恢復連線後會自動繼續。"
                : "網路中斷：背景補傳與紀錄同步已暫停，恢復連線後會自動繼續。";
            _simulatorView?.PauseForNetworkOutage();
        }
    }

    private void ShowUpdateBanner(string text, bool showInstall, bool showRestart, bool showPause = false)
    {
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerText.Text = text;
        InstallUpdateButton.Visibility = showInstall ? Visibility.Visible : Visibility.Collapsed;
        RestartUpdateButton.Visibility = showRestart ? Visibility.Visible : Visibility.Collapsed;
        PauseDownloadButton.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
        PauseDownloadButton.Content = _backgroundDownloadPaused ? "繼續下載" : "暫停下載";
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (!_restartPending && _simulatorView is { HasUnsavedChanges: true })
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
        _pendingUploadTimer.Stop();
        _networkTimer.Stop();
        _downloadRetryTimer?.Stop();
        _backgroundDownloadCts?.Cancel();
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailability_Changed;
        _simulatorView?.Shutdown();
        _friendsView?.Shutdown();

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
