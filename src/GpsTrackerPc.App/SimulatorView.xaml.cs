using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace GpsTrackerPc.App;

/// <summary>
/// 路線編輯與模擬播放。內嵌在主視窗的內容區塊，不另開視窗；
/// 地圖（simulator.html）只負責顯示與送出事件，路線與播放結果都由 C# 管理。
/// </summary>
public partial class SimulatorView : UserControl
{
    private const string SimulatorHost = "simulator.local";

    private readonly RouteEditorModel _route = new();
    private readonly FavoritesStore _favorites;
    private readonly RouteLibraryStore _library;
    private readonly GpxFileHistoryStore _gpxHistory;
    private readonly HttpClient _searchClient;
    private readonly HttpClient _uploadClient;
    private readonly TokenStore _tokenStore;
    private readonly DeviceNameStore _deviceNameStore;
    private readonly NominatimGeocoder _geocoder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackWatch = new();

    private bool _webReady;
    private bool _updatingUi;
    private bool _directGenerating;
    private string? _selectedWaypointId;
    private double _mapCenterLat = Coordinates.Default.Latitude;
    private double _mapCenterLon = Coordinates.Default.Longitude;
    private double _selectedSpeedMultiplier = 1.0;
    private PlaybackController? _playback;
    private PlaybackResult? _lastPlayback;
    private PlaybackMapStream _mapStream = new();
    private PlaybackMapFrame? _pendingFullFrame;
    private bool _mapUpdatesEnabled;
    private int _sentSampleCount;
    private readonly MapSettingsStore _mapSettingsStore;
    private BasemapOption _basemap = BasemapCatalog.Default;
    private bool _followEnabled = true;
    private ViewportBounds? _viewport;
    private Coordinates? _lastPlaybackCoordinate;
    private bool _hasUnsavedChanges;
    private bool _locating;
    private bool _autoLocateAttempted;
    private bool _closing;
    private Window? _hostWindow;

    /// <summary>使用者按下「回首頁」時通知主視窗切回紀錄網站。</summary>
    public event EventHandler? BackToHomeRequested;

    public SimulatorView(TokenStore tokenStore, DeviceNameStore deviceNameStore)
    {
        InitializeComponent();

        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _deviceNameStore = deviceNameStore ?? throw new ArgumentNullException(nameof(deviceNameStore));
        _favorites = new FavoritesStore(AppPaths.FavoritesFile);
        _library = new RouteLibraryStore(AppPaths.RoutesDirectory);
        _gpxHistory = new GpxFileHistoryStore(AppPaths.GpxHistoryFile);
        _searchClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _uploadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _geocoder = new NominatimGeocoder(_searchClient);
        _mapSettingsStore = new MapSettingsStore(AppPaths.MapSettingsFile);
        var mapSettings = _mapSettingsStore.Load();
        _basemap = BasemapCatalog.Options.First(option => option.Kind == mapSettings.Basemap);
        _followEnabled = mapSettings.Follow;
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PlaybackMapPolicy.UpdateIntervalMilliseconds),
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        IsVisibleChanged += View_IsVisibleChanged;

        _updatingUi = true;
        RouteNameBox.Text = _route.Name;
        BasemapBox.ItemsSource = BasemapCatalog.Options;
        BasemapBox.SelectedValue = _basemap.Value;
        FollowToggle.IsChecked = _followEnabled;
        _updatingUi = false;

        RefreshAllUi(updateName: false);
        RefreshFavorites();
        RefreshImportedGpxList();
        SetPlaybackControlState();
    }

    /// <summary>給第 7 階段使用的最近一次播放／直接產生結果。</summary>
    public PlaybackResult? LastPlaybackResult => _lastPlayback;

    public bool HasUnsavedChanges => _hasUnsavedChanges;

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is null)
        {
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.Activated += HostWindow_Activated;
                _hostWindow.Deactivated += HostWindow_Deactivated;
                _hostWindow.StateChanged += HostWindow_StateChanged;
            }
        }

        try
        {
            var simulatorRoot = Path.Combine(AppContext.BaseDirectory, "Simulator");
            var page = Path.Combine(simulatorRoot, "simulator.html");
            if (!File.Exists(page))
            {
                ShowError(
                    "找不到輸出資料夾裡的 Simulator\\simulator.html。\n\n" +
                    $"預期位置：{page}\n\n請重新建置專案。");
                return;
            }

            MapView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x12, 0x20);
            await MapView.EnsureCoreWebView2Async();
            if (_closing)
            {
                return;
            }

            var core = MapView.CoreWebView2;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                SimulatorHost,
                simulatorRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationCompleted += Core_NavigationCompleted;

            MapView.Source = new Uri($"https://{SimulatorHost}/simulator.html");
            UpdateMapActivity();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ShowError("初始化模擬地圖失敗：\n\n" + ex.Message);
            }
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
            LoadingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError($"載入模擬地圖失敗（{e.WebErrorStatus}）。");
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeNode))
            {
                return;
            }

            var type = typeNode.GetString();
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    ReadCenter(root);
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    SendRoute();
                    SendBasemap();
                    SetPlaybackControlState();
                    UpdateMapActivity();

                    // 路線還是空的就自動定位一次，讓地圖一開啟就在使用者附近。
                    if (_route.Waypoints.Count == 0)
                    {
                        _ = AutoLocateAsync();
                    }

                    break;
                case "center":
                    ReadCenter(root);
                    break;
                case "followPaused":
                    _followEnabled = false;
                    _updatingUi = true;
                    FollowToggle.IsChecked = false;
                    _updatingUi = false;
                    SaveMapSettings();
                    break;
                case "viewport":
                {
                    var west = GetDouble(root, "west");
                    var south = GetDouble(root, "south");
                    var east = GetDouble(root, "east");
                    var north = GetDouble(root, "north");
                    if (double.IsFinite(west) && double.IsFinite(south) &&
                        double.IsFinite(east) && double.IsFinite(north))
                    {
                        _viewport = new ViewportBounds(west, south, east, north);
                    }

                    break;
                }
                case "mapClick":
                {
                    var lat = GetDouble(root, "lat");
                    var lon = GetDouble(root, "lon");
                    if (double.IsFinite(lat) && double.IsFinite(lon))
                    {
                        AddWaypointAt(lat, lon);
                    }

                    break;
                }
                case "markerMoved":
                {
                    var id = GetString(root, "id");
                    var lat = GetDouble(root, "lat");
                    var lon = GetDouble(root, "lon");
                    if (!string.IsNullOrWhiteSpace(id) &&
                        double.IsFinite(lat) && double.IsFinite(lon) &&
                        _route.MoveWaypoint(id, new Coordinates(lat, lon)))
                    {
                        _selectedWaypointId = id;
                        _hasUnsavedChanges = true;
                        RefreshWaypointList(id);
                        SendRoute();
                    }

                    break;
                }
                case "markerClick":
                {
                    var id = GetString(root, "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        SelectWaypoint(id);
                    }

                    break;
                }
                case "error":
                    SetStatus("地圖回報錯誤：" + (GetString(root, "message") ?? "未知錯誤"));
                    break;
            }
        }
        catch (JsonException)
        {
            // 忽略格式不正確的訊息。
        }
    }

    private void AddWaypointAt(double latitude, double longitude)
    {
        try
        {
            _selectedWaypointId = _route.AddWaypoint(new Coordinates(latitude, longitude));
            _hasUnsavedChanges = true;
            RefreshWaypointList(_selectedWaypointId);
            RefreshTimingControls();
            SendRoute();
            SetStatus($"已新增路線點（{_route.Waypoints.Count} 點）。");
        }
        catch (ArgumentException ex)
        {
            SetStatus("無法新增路線點：" + ex.Message);
        }
    }

    private void RefreshAllUi(bool updateName)
    {
        var previous = _updatingUi;
        _updatingUi = true;
        if (updateName)
        {
            RouteNameBox.Text = _route.Name;
        }

        SpeedBox.Text = FormatSpeed(_route.SpeedKph);
        LoopCheck.IsChecked = _route.Loop;
        NaturalCheck.IsChecked = _route.NaturalSpeed;
        _updatingUi = previous;

        RefreshTimingControls();
        RefreshWaypointList(_selectedWaypointId);
        RefreshOutageList();
        SetPlaybackControlState();
    }

    private void RefreshTimingControls()
    {
        var previous = _updatingUi;
        _updatingUi = true;

        TimingCheck.IsEnabled = _route.CanUseGpxTiming;
        TimingCheck.IsChecked = _route.UseGpxTiming;
        var fixedSpeed = !_route.UseGpxTiming;
        SpeedPresetPanel.IsEnabled = fixedSpeed;
        SpeedBox.IsEnabled = fixedSpeed;
        ApplySpeedButton.IsEnabled = fixedSpeed;
        NaturalCheck.IsEnabled = fixedSpeed;

        _updatingUi = previous;
    }

    private void RefreshWaypointList(string? selectId)
    {
        var previous = _updatingUi;
        _updatingUi = true;

        var rows = _route.Waypoints
            .Select((waypoint, index) => new WaypointRow(
                waypoint.Id,
                $"{index + 1}. {waypoint.Name}" +
                (waypoint.StopDurationSeconds > 0 ? $"（停留 {waypoint.StopDurationSeconds} 秒）" : string.Empty) +
                (waypoint.TimeOffsetSeconds is { } offset ? $" · {offset}s" : string.Empty)))
            .ToList();
        WaypointList.ItemsSource = rows;
        WaypointSummaryText.Text = $"共 {rows.Count} 點" + (_route.HasTiming ? "（有時間資料）" : "（沒有時間資料）");

        WaypointRow? selected = null;
        if (!string.IsNullOrWhiteSpace(selectId))
        {
            selected = rows.FirstOrDefault(row => row.Id == selectId);
            WaypointList.SelectedItem = selected;
        }
        else
        {
            WaypointList.SelectedItem = null;
        }

        _selectedWaypointId = selected?.Id;
        StopSecondsBox.Text = selected == null
            ? string.Empty
            : _route.Waypoints.First(waypoint => waypoint.Id == selected.Id).StopDurationSeconds
                .ToString(CultureInfo.InvariantCulture);

        _updatingUi = previous;
    }

    private void RefreshOutageList()
    {
        var previous = _updatingUi;
        _updatingUi = true;
        OutageList.ItemsSource = _route.Outages
            .Select(outage => new OutageRow(
                outage,
                $"{FormatClock(outage.StartSeconds)} 起，持續 {FormatClock(outage.DurationSeconds)}" +
                $"（{outage.StartSeconds}s / {outage.DurationSeconds}s）"))
            .ToList();
        _updatingUi = previous;
    }

    private void RefreshFavorites()
    {
        var previous = _updatingUi;
        _updatingUi = true;
        FavoriteList.ItemsSource = _favorites.Load()
            .Select(place => new FavoriteRow(
                place,
                $"{place.Name}（{place.Coordinates.FormattedLatitude()}, {place.Coordinates.FormattedLongitude()}）"))
            .ToList();
        _updatingUi = previous;
    }

    /// <summary>列出匯入 GPX 頁面與模擬頁用過、而且檔案還在的 GPX。</summary>
    private void RefreshImportedGpxList()
    {
        if (!IsVisible)
        {
            return;
        }

        var previousSelection = ImportedGpxBox.SelectedValue as string;
        var rows = _gpxHistory.Load()
            .Where(entry => File.Exists(entry.FilePath))
            .Select(entry => new ImportedGpxRow(entry.FilePath, entry.DisplayName))
            .ToList();

        var previous = _updatingUi;
        _updatingUi = true;
        ImportedGpxBox.ItemsSource = rows;
        if (previousSelection != null)
        {
            ImportedGpxBox.SelectedValue = previousSelection;
        }

        if (ImportedGpxBox.SelectedItem == null && rows.Count > 0)
        {
            ImportedGpxBox.SelectedIndex = 0;
        }

        _updatingUi = previous;
        ImportedGpxHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportedGpxBox.IsEnabled = rows.Count > 0;
        LoadImportedGpxButton.IsEnabled = rows.Count > 0;
    }

    private void SendRoute()
    {
        if (_playback == null || _playback.State is PlaybackState.Stopped or PlaybackState.Completed)
        {
            _lastPlayback = null;
            _lastPlaybackCoordinate = null;
            UploadRecordButton.IsEnabled = false;
        }

        if (!_webReady)
        {
            return;
        }

        var points = _route.Waypoints
            .Select((waypoint, index) => new
            {
                id = waypoint.Id,
                name = waypoint.Name,
                lat = waypoint.Coordinates.Latitude,
                lon = waypoint.Coordinates.Longitude,
                stop = waypoint.StopDurationSeconds,
                index,
            })
            .ToList();
        Post(new
        {
            type = "setRoute",
            points,
            loop = _route.Loop,
            selectedId = _selectedWaypointId,
        });
    }

    private void PostFit()
    {
        Post(new { type = "fitBounds" });
    }

    private void Post(object message)
    {
        if (_closing || !_webReady || MapView.CoreWebView2 == null)
        {
            return;
        }

        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SelectWaypoint(string id)
    {
        _selectedWaypointId = id;
        RefreshWaypointList(id);
        Post(new { type = "selectMarker", id });
    }

    private void RouteNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingUi)
        {
            _route.Name = RouteNameBox.Text;
            _hasUnsavedChanges = true;
        }
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        _route.Loop = LoopCheck.IsChecked == true;
        _route.NaturalSpeed = NaturalCheck.IsChecked == true;
        _hasUnsavedChanges = true;
        try
        {
            _route.SetUseGpxTiming(TimingCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }

        RefreshTimingControls();
        SetPlaybackControlState();
        SendRoute();
    }

    private void ApplyRouteSettings_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RouteNameBox.Text))
        {
            SetStatus("路線名稱不能是空白。");
            return;
        }

        if (!double.TryParse(
                SpeedBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var speed))
        {
            SetStatus("速度格式不正確。");
            return;
        }

        try
        {
            _route.Name = RouteNameBox.Text.Trim();
            _route.SetSpeed(speed);
            _route.Loop = LoopCheck.IsChecked == true;
            _route.NaturalSpeed = NaturalCheck.IsChecked == true;
            _route.SetUseGpxTiming(TimingCheck.IsChecked == true && _route.CanUseGpxTiming);
        }
        catch (Exception ex)
        {
            SetStatus("套用路線設定失敗：" + ex.Message);
            return;
        }

        _hasUnsavedChanges = true;
        RefreshTimingControls();
        SendRoute();
        SetStatus("已套用路線設定。");
    }

    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || sender is not Button button ||
            !double.TryParse(
                button.Tag?.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var speed))
        {
            return;
        }

        ApplySpeed(speed);
    }

    private void ApplySpeed_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(
                SpeedBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var speed))
        {
            SetStatus("速度格式不正確。");
            return;
        }

        ApplySpeed(speed);
    }

    private void ApplySpeed(double speed)
    {
        try
        {
            _route.SetSpeed(speed);
            _hasUnsavedChanges = true;
            SpeedBox.Text = FormatSpeed(_route.SpeedKph);
            SetStatus($"速度設為 {FormatSpeed(_route.SpeedKph)} km/h。");
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void WaypointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        if (WaypointList.SelectedItem is WaypointRow row)
        {
            _selectedWaypointId = row.Id;
            var waypoint = _route.Waypoints.FirstOrDefault(item => item.Id == row.Id);
            StopSecondsBox.Text = waypoint?.StopDurationSeconds.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Post(new { type = "selectMarker", id = row.Id });
        }
    }

    private void ApplyStop_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedWaypointId))
        {
            SetStatus("請先選一個路線點。");
            return;
        }

        if (!long.TryParse(
                StopSecondsBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            SetStatus("停留秒數格式不正確。");
            return;
        }

        try
        {
            _route.SetWaypointStop(_selectedWaypointId, seconds);
            _hasUnsavedChanges = true;
            RefreshWaypointList(_selectedWaypointId);
            SendRoute();
            SetStatus($"已把停留時間設為 {seconds} 秒。");
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelectedWaypoint(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelectedWaypoint(1);

    private void MoveSelectedWaypoint(int delta)
    {
        if (string.IsNullOrWhiteSpace(_selectedWaypointId))
        {
            SetStatus("請先選一個路線點。");
            return;
        }

        var index = _route.Waypoints
            .Select((waypoint, position) => (waypoint, position))
            .FirstOrDefault(item => item.waypoint.Id == _selectedWaypointId)
            .position;
        if (!_route.ReorderWaypoint(_selectedWaypointId, index + delta))
        {
            return;
        }

        _hasUnsavedChanges = true;
        RefreshAllUi(updateName: false);
        SendRoute();
    }

    private void DeleteWaypoint_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedWaypointId))
        {
            SetStatus("請先選一個路線點。");
            return;
        }

        var ids = _route.Waypoints.Select(waypoint => waypoint.Id).ToList();
        var index = ids.IndexOf(_selectedWaypointId);
        if (!_route.DeleteWaypoint(_selectedWaypointId))
        {
            return;
        }

        _hasUnsavedChanges = true;
        _selectedWaypointId = _route.Waypoints.Count == 0
            ? null
            : _route.Waypoints[Math.Clamp(index, 0, _route.Waypoints.Count - 1)].Id;
        RefreshAllUi(updateName: false);
        SendRoute();
        SetStatus($"已刪除路線點，剩 {_route.Waypoints.Count} 點。");
    }

    private void ClearWaypoints_Click(object sender, RoutedEventArgs e)
    {
        var count = _route.Waypoints.Count;
        if (count == 0)
        {
            SetStatus("目前沒有路線點。");
            return;
        }

        var answer = ShowMessageBox(
            $"確定要清除全部 {count} 個路線點嗎？\n\nGPS 中斷設定會保留。",
            "清除全部路線點",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _route.ClearWaypoints();
        _selectedWaypointId = null;
        _hasUnsavedChanges = true;
        RefreshAllUi(updateName: false);
        SendRoute();
        SetStatus($"已清除全部路線點（原本 {count} 點）。");
    }

    private void AddOutage_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(
                OutageStartBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var start) ||
            !long.TryParse(
                OutageDurationBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var duration))
        {
            SetStatus("中斷的開始秒數與持續秒數都要是整數。");
            return;
        }

        try
        {
            _route.AddOutage(start, duration);
            _hasUnsavedChanges = true;
            RefreshOutageList();
            OutageStartBox.Clear();
            OutageDurationBox.Clear();
            SetStatus($"已新增 GPS 中斷（{start}s 起，{duration}s）。");
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void DeleteOutage_Click(object sender, RoutedEventArgs e)
    {
        if (OutageList.SelectedItem is not OutageRow row)
        {
            SetStatus("請先選一個中斷時段。");
            return;
        }

        _route.RemoveOutage(row.Outage);
        _hasUnsavedChanges = true;
        RefreshOutageList();
        SetStatus("已刪除 GPS 中斷。");
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SetStatus("請輸入地點關鍵字。");
            return;
        }

        SearchButton.IsEnabled = false;
        var previous = SearchButton.Content;
        SearchButton.Content = "搜尋中…";
        SetStatus("正在查詢 Nominatim…（每秒最多一次）");
        try
        {
            var results = await _geocoder.SearchAsync(query, _lifetime.Token);
            if (_closing)
            {
                return;
            }

            _updatingUi = true;
            SearchResultsList.ItemsSource = results;
            _updatingUi = false;
            SetStatus(results.Count == 0 ? "找不到符合的地點。" : $"找到 {results.Count} 個地點，點一下可移動地圖。");
        }
        catch (OperationCanceledException)
        {
            // 視窗關閉。
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("地點搜尋失敗：" + ex.Message);
            }
        }
        finally
        {
            if (!_closing)
            {
                SearchButton.Content = previous;
                SearchButton.IsEnabled = true;
            }
        }
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || SearchResultsList.SelectedItem is not GeocodingResult result)
        {
            return;
        }

        Post(new
        {
            type = "flyTo",
            lat = result.Coordinates.Latitude,
            lon = result.Coordinates.Longitude,
            zoom = result.ZoomLevel,
        });
        SetStatus("已移動到：" + result.DisplayName);
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var name = FavoriteNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = $"收藏 {DateTime.Now:MM/dd HH:mm}";
        }

        try
        {
            _favorites.Add(name, new Coordinates(_mapCenterLat, _mapCenterLon));
            FavoriteNameBox.Clear();
            RefreshFavorites();
            SetStatus("已加入收藏地點。");
        }
        catch (Exception ex)
        {
            SetStatus("加入收藏失敗：" + ex.Message);
        }
    }

    private void DeleteFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteList.SelectedItem is not FavoriteRow row)
        {
            SetStatus("請先選一個收藏地點。");
            return;
        }

        _favorites.Remove(row.Place.Id);
        RefreshFavorites();
        SetStatus("已刪除收藏地點。");
    }

    private void FavoriteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || FavoriteList.SelectedItem is not FavoriteRow row)
        {
            return;
        }

        Post(new
        {
            type = "flyTo",
            lat = row.Place.Coordinates.Latitude,
            lon = row.Place.Coordinates.Longitude,
            zoom = 16,
        });
        SetStatus("已移動到收藏：" + row.Place.Name);
    }

    private void GenerateRandom_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(
                RandomCountBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count) ||
            !double.TryParse(
                RandomRadiusBox.Text.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var radius))
        {
            SetStatus("點數與半徑格式不正確。");
            return;
        }

        var tag = (ShapeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var shape = tag == "Loop" ? RandomRouteShape.Loop : RandomRouteShape.Walk;
        try
        {
            _route.GenerateRandom(
                new Coordinates(_mapCenterLat, _mapCenterLon),
                count,
                radius,
                shape,
                new Random(),
                $"隨機路線 {DateTime.Now:MMdd-HHmm}");
            _selectedWaypointId = null;
            _hasUnsavedChanges = true;
            RefreshAllUi(updateName: true);
            SendRoute();
            PostFit();
            SetStatus($"已產生 {count} 點的隨機路線（{(shape == RandomRouteShape.Loop ? "環狀" : "漫步")}）。");
        }
        catch (Exception ex)
        {
            SetStatus("產生隨機路線失敗：" + ex.Message);
        }
    }

    private void NewRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_route.Waypoints.Count > 0)
        {
            var answer = ShowMessageBox(
                "要清空目前的路線並開一條新路線嗎？",
                "新路線",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _route.Reset();
        _selectedWaypointId = null;
        _hasUnsavedChanges = false;
        RefreshAllUi(updateName: true);
        SendRoute();
        SetStatus("已建立新路線。");
    }

    private void SaveRoute_Click(object sender, RoutedEventArgs e)
    {
        _route.Name = RouteNameBox.Text;
        try
        {
            var route = _route.ToRoute();
            _library.Save(route);
            _hasUnsavedChanges = false;
            SetStatus($"已儲存「{route.Name}」（{route.Waypoints.Count} 點）。");
        }
        catch (Exception ex)
        {
            SetStatus("儲存路線失敗：" + ex.Message);
        }
    }

    private void SavedRoutes_Click(object sender, RoutedEventArgs e)
    {
        var window = new SavedRoutesWindow(_library)
        {
            Owner = Window.GetWindow(this),
        };
        if (window.ShowDialog() == true && window.SelectedRoute != null)
        {
            _route.ReplaceRoute(window.SelectedRoute);
            _selectedWaypointId = null;
            _hasUnsavedChanges = false;
            RefreshAllUi(updateName: true);
            SendRoute();
            PostFit();
            SetStatus($"已載入「{_route.Name}」。");
        }
    }

    private async void ImportRoute_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "匯入 GPX／KML",
            Filter = "GPX／KML (*.gpx;*.kml)|*.gpx;*.kml|所有檔案 (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        await LoadRouteFileAsync(dialog.FileName);
    }

    private async void LoadImportedGpx_Click(object sender, RoutedEventArgs e)
    {
        if (ImportedGpxBox.SelectedItem is not ImportedGpxRow row)
        {
            SetStatus("請先選擇一個匯入過的 GPX。");
            return;
        }

        if (!File.Exists(row.FilePath))
        {
            SetStatus($"找不到檔案：{row.FilePath}");
            RefreshImportedGpxList();
            return;
        }

        await LoadRouteFileAsync(row.FilePath);
    }

    /// <summary>從指定檔案載入路線；匯入過的路徑會記住，之後不用再選檔。</summary>
    private async Task LoadRouteFileAsync(string path)
    {
        SetStatus("匯入中…");
        try
        {
            var fallbackName = Path.GetFileNameWithoutExtension(path);
            var imported = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return LocationDocumentCodec.ImportStream(stream, fallbackName);
            });
            if (_closing)
            {
                return;
            }

            _route.ReplaceRoute(imported.Route);
            _selectedWaypointId = _route.Waypoints.Count > 0 ? _route.Waypoints[0].Id : null;
            _hasUnsavedChanges = true;
            RefreshAllUi(updateName: true);
            SendRoute();
            PostFit();

            if (path.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _gpxHistory.Remember(path);
                    RefreshImportedGpxList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // 記錄失敗不影響匯入結果。
                }
            }

            SetStatus(imported.WasSimplified
                ? $"已匯入「{_route.Name}」：原始 {imported.OriginalPointCount} 點，保留 {imported.KeptPointCount} 點。"
                : $"已匯入「{_route.Name}」（{_route.Waypoints.Count} 點）。");
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("匯入失敗：" + ex.Message);
            }
        }
    }

    private void ExportGpx_Click(object sender, RoutedEventArgs e) => ExportRoute(LocationDocumentFormat.Gpx);

    private void ExportKml_Click(object sender, RoutedEventArgs e) => ExportRoute(LocationDocumentFormat.Kml);

    private void ExportRoute(LocationDocumentFormat format)
    {
        var extension = format == LocationDocumentFormat.Gpx ? "gpx" : "kml";
        var dialog = new SaveFileDialog
        {
            Title = $"匯出 {extension.ToUpperInvariant()}",
            Filter = format == LocationDocumentFormat.Gpx
                ? "GPX (*.gpx)|*.gpx"
                : "KML (*.kml)|*.kml",
            FileName = SafeFileName(_route.Name) + "." + extension,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var content = LocationDocumentCodec.Export(_route.ToRoute(), format);
            File.WriteAllText(
                dialog.FileName,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"已匯出 {extension.ToUpperInvariant()}：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"匯出 {extension.ToUpperInvariant()} 失敗：" + ex.Message);
        }
    }

    private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackSpeedBox.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(
                item.Tag?.ToString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var multiplier))
        {
            return;
        }

        _selectedSpeedMultiplier = multiplier;
        if (_playback != null)
        {
            try
            {
                _playback.SetSpeedMultiplier(multiplier);
            }
            catch (ArgumentException ex)
            {
                SetStatus(ex.Message);
            }
        }
    }

    private void StartPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (!_webReady || _directGenerating || _playback?.State is PlaybackState.Playing or PlaybackState.Paused)
        {
            return;
        }

        SimulationRoute route;
        try
        {
            route = _route.ToRoute();
        }
        catch (Exception ex)
        {
            SetStatus("無法播放：" + ex.Message);
            return;
        }

        if (!TryReadLaps(route, out var laps))
        {
            return;
        }

        var controller = new PlaybackController(route, laps);
        controller.SetSpeedMultiplier(_selectedSpeedMultiplier);
        controller.Start();
        _playback = controller;
        _lastPlayback = null;
        _lastPlaybackCoordinate = controller.Current?.Coordinates;
        _mapStream = new PlaybackMapStream();
        _pendingFullFrame = null;
        _sentSampleCount = 0;
        PlaybackOutageText.Text = string.Empty;

        var current = controller.Current;
        Post(new
        {
            type = "playbackReset",
            lat = current?.Coordinates.Latitude,
            lon = current?.Coordinates.Longitude,
        });
        AppendNewPlaybackSamples();
        if (_mapUpdatesEnabled)
        {
            SendMapFrame(_mapStream.BuildFrame());
        }

        SendPlaybackPosition();
        UpdatePlaybackUi();
        SetPlaybackControlState();

        _playbackWatch.Restart();
        _playbackTimer.Start();
        SetStatus("播放開始。");
    }

    private void PausePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playback?.State != PlaybackState.Playing)
        {
            return;
        }

        _playback.Pause();
        _playbackTimer.Stop();
        _playbackWatch.Reset();
        UpdatePlaybackUi();
        SetPlaybackControlState();
        SetStatus("播放已暫停。");
    }

    private void ResumePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playback?.State != PlaybackState.Paused)
        {
            return;
        }

        _playback.Resume();
        _playbackWatch.Restart();
        _playbackTimer.Start();
        SetPlaybackControlState();
        SetStatus("繼續播放。");
    }

    private void StopPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playback == null || _playback.State is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            return;
        }

        _playback.Stop();
        _playbackTimer.Stop();
        _playbackWatch.Reset();
        _lastPlayback = _playback.BuildResult();
        AppendNewPlaybackSamples();
        if (_mapUpdatesEnabled)
        {
            SendMapFrame(_mapStream.BuildFrame());
            Post(new { type = "playbackEnd" });
        }

        UpdatePlaybackUi();
        SetPlaybackControlState();
        SetStatus($"播放已停止，保留 {_lastPlayback.Samples.Count} 個取樣點。");
    }

    private async void DirectGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (!_webReady || _directGenerating || _playback?.State is PlaybackState.Playing or PlaybackState.Paused)
        {
            return;
        }

        SimulationRoute route;
        try
        {
            route = _route.ToRoute();
        }
        catch (Exception ex)
        {
            SetStatus("無法產生：" + ex.Message);
            return;
        }

        if (!TryReadLaps(route, out var laps))
        {
            return;
        }

        _directGenerating = true;
        SetPlaybackControlState();
        SetStatus("正在直接產生完整路線…");

        try
        {
            var result = await Task.Run(() => PlaybackController.GenerateDirect(route, laps));
            if (_closing)
            {
                return;
            }

            _playback = null;
            _lastPlayback = result;
            _lastPlaybackCoordinate = result.Samples.LastOrDefault()?.Coordinates
                ?? route.Waypoints[^1].Coordinates;
            _sentSampleCount = 0;
            _mapStream = new PlaybackMapStream();
            foreach (var sample in result.Samples)
            {
                _mapStream.Append(sample);
            }

            var frame = _mapStream.BuildFrame(forceReplace: true);
            var first = result.Samples.FirstOrDefault();
            Post(new
            {
                type = "playbackReset",
                lat = first?.Coordinates.Latitude ?? route.Waypoints[0].Coordinates.Latitude,
                lon = first?.Coordinates.Longitude ?? route.Waypoints[0].Coordinates.Longitude,
            });
            if (_mapUpdatesEnabled)
            {
                SendMapFrame(frame);
                Post(new { type = "playbackEnd" });
            }
            else
            {
                _pendingFullFrame = frame;
            }

            PlaybackProgress.Value = 100;
            PlaybackOutageText.Text = string.Empty;
            PlaybackMetaText.Text = result.Samples.Count == 0
                ? "沒有取樣點（整段可能都在 GPS 中斷）"
                : $"已過 {FormatClock((long)Math.Floor(result.ElapsedSeconds))}｜" +
                  $"終點速度 {(result.Samples[^1].SpeedMetersPerSecond * 3.6).ToString("0.0", CultureInfo.InvariantCulture)} km/h｜" +
                  $"已走 {(result.DistanceMeters / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} km";
            SetStatus($"直接產生完成：{result.Samples.Count} 個取樣點。");
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("直接產生失敗：" + ex.Message);
            }
        }
        finally
        {
            _directGenerating = false;
            if (!_closing)
            {
                SetPlaybackControlState();
            }
        }
    }

    private void UploadRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPlayback == null)
        {
            SetStatus("請先即時播放或直接產生一次模擬結果。");
            return;
        }

        if (!_tokenStore.HasToken)
        {
            ShowMessageBox(
                "上傳需要 GitHub Token，請先回主畫面按「設定」輸入。",
                "尚未設定 Token",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var window = new SimulatedUploadWindow(
            _lastPlayback,
            _tokenStore,
            _uploadClient,
            _deviceNameStore.Load())
        {
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();
        if (window.Uploaded)
        {
            SetStatus($"模擬紀錄已上傳（裝置 {_deviceNameStore.Load()}、來源 simulated）。");
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _playback?.State != PlaybackState.Playing)
        {
            return;
        }

        var elapsed = _playbackWatch.Elapsed;
        _playbackWatch.Restart();
        try
        {
            _playback.Advance(elapsed);
        }
        catch (Exception ex)
        {
            _playbackTimer.Stop();
            _playbackWatch.Reset();
            _playback.Stop();
            _lastPlayback = _playback.BuildResult();
            SetPlaybackControlState();
            SetStatus("播放失敗：" + ex.Message);
            return;
        }

        AppendNewPlaybackSamples();
        var completed = _playback.State == PlaybackState.Completed;
        if (_mapUpdatesEnabled && !completed)
        {
            SendMapFrame(_mapStream.BuildFrame());
            SendPlaybackPosition();
        }

        UpdatePlaybackUi();

        if (completed)
        {
            _playbackTimer.Stop();
            _playbackWatch.Reset();
            _lastPlayback = _playback.BuildResult();
            if (_mapUpdatesEnabled)
            {
                SendMapFrame(_mapStream.BuildFrame());
                Post(new { type = "playbackEnd" });
            }

            SetPlaybackControlState();
            SetStatus($"播放完成：{_lastPlayback.Samples.Count} 個取樣點。");
        }
    }

    private void AppendNewPlaybackSamples()
    {
        if (_playback == null)
        {
            return;
        }

        var samples = _playback.Samples;
        for (; _sentSampleCount < samples.Count; _sentSampleCount++)
        {
            _mapStream.Append(samples[_sentSampleCount]);
        }
    }

    private void SendMapFrame(PlaybackMapFrame frame)
    {
        if (!_webReady)
        {
            return;
        }

        if (frame.ReplaceAll)
        {
            var segments = frame.Segments
                .Select(segment => segment.Points
                    .Select(point => new
                    {
                        lat = point.Coordinates.Latitude,
                        lon = point.Coordinates.Longitude,
                        elapsed = point.ElapsedSeconds,
                    })
                    .ToList())
                .ToList();
            Post(new { type = "playbackReplace", segments });
            return;
        }

        if (frame.Samples.Count == 0)
        {
            return;
        }

        var samples = frame.Samples
            .Select(sample => new
            {
                lat = sample.Coordinates.Latitude,
                lon = sample.Coordinates.Longitude,
                elapsed = sample.ElapsedSeconds,
                speed = sample.SpeedMetersPerSecond,
                outage = sample.IsInOutage,
            })
            .ToList();
        Post(new { type = "playbackBatch", samples });
    }

    private void SendPlaybackPosition()
    {
        if (!_mapUpdatesEnabled)
        {
            return;
        }

        var current = _playback?.Current;
        if (current == null)
        {
            return;
        }

        _lastPlaybackCoordinate = current.Coordinates;
        Post(new
        {
            type = "playbackPosition",
            lat = current.Coordinates.Latitude,
            lon = current.Coordinates.Longitude,
            outage = _playback!.IsInOutage,
        });

        MaybeFollow(current);
    }

    private void MaybeFollow(RoutePosition current)
    {
        if (_viewport == null || !_followEnabled)
        {
            return;
        }

        if (ViewportFollowPolicy.ShouldPan(_viewport, current.Coordinates, _followEnabled))
        {
            Post(new
            {
                type = "followPan",
                lat = current.Coordinates.Latitude,
                lon = current.Coordinates.Longitude,
            });
        }
    }

    private void UpdatePlaybackUi()
    {
        if (_playback?.Current == null)
        {
            return;
        }

        var current = _playback.Current;
        PlaybackProgress.Value = Math.Clamp(_playback.Progress * 100.0, 0.0, 100.0);
        PlaybackMetaText.Text =
            $"已過 {FormatClock((long)Math.Floor(current.ElapsedSeconds))}｜" +
            $"目前 {(current.SpeedMetersPerSecond * 3.6).ToString("0.0", CultureInfo.InvariantCulture)} km/h｜" +
            $"已走 {(_playback.DistanceMeters / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} km";
        PlaybackOutageText.Text = _playback.IsInOutage
            ? "GPS 中斷中（不記錄位置）"
            : string.Empty;
    }

    private void SetPlaybackControlState()
    {
        var active = _playback?.State is PlaybackState.Playing or PlaybackState.Paused;
        RouteEditPanel.IsEnabled = !active && !_directGenerating;
        StartPlaybackButton.IsEnabled = !active && !_directGenerating && _webReady;
        DirectGenerateButton.IsEnabled = !active && !_directGenerating && _webReady;
        PausePlaybackButton.IsEnabled = _playback?.State == PlaybackState.Playing;
        ResumePlaybackButton.IsEnabled = _playback?.State == PlaybackState.Paused;
        StopPlaybackButton.IsEnabled = active;
        UploadRecordButton.IsEnabled = !active && !_directGenerating && _lastPlayback != null;
        LapsBox.IsEnabled = !active && !_directGenerating && _route.Loop;
        PlaybackSpeedBox.IsEnabled = !_directGenerating;
    }

    private bool TryReadLaps(SimulationRoute route, out int laps)
    {
        laps = 1;
        if (!route.Loop)
        {
            return true;
        }

        if (!int.TryParse(
                LapsBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed < 1 ||
            parsed > 10_000)
        {
            SetStatus("循環路線的圈數必須是 1 到 10000 的整數。");
            return false;
        }

        laps = parsed;
        return true;
    }

    private void Basemap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        _basemap = BasemapCatalog.Get(BasemapBox.SelectedValue as string);
        SaveMapSettings();
        Post(new { type = "setBasemap", basemap = _basemap.Value });
        SetStatus($"底圖：{_basemap.Label}");
    }

    private void Follow_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        _followEnabled = FollowToggle.IsChecked == true;
        SaveMapSettings();

        if (_followEnabled)
        {
            var coordinate = _playback?.Current?.Coordinates ?? _lastPlaybackCoordinate;
            if (coordinate != null)
            {
                Post(new
                {
                    type = "followPan",
                    lat = coordinate.Latitude,
                    lon = coordinate.Longitude,
                });
            }
        }
    }

    private void SaveMapSettings()
    {
        try
        {
            _mapSettingsStore.Save(new SimulatorMapSettings(_basemap.Kind, _followEnabled));
        }
        catch (Exception ex)
        {
            SetStatus("儲存底圖設定失敗：" + ex.Message);
        }
    }

    private void SendBasemap() => Post(new { type = "setBasemap", basemap = _basemap.Value });

    private void HostWindow_StateChanged(object? sender, EventArgs e) => UpdateMapActivity();

    private void HostWindow_Activated(object? sender, EventArgs e)
    {
        UpdateMapActivity();
        RefreshImportedGpxList();
    }

    private void HostWindow_Deactivated(object? sender, EventArgs e) => UpdateMapActivity();

    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateMapActivity();
        RefreshImportedGpxList();
    }

    /// <summary>
    /// 主視窗縮小、被其他視窗蓋住、失去焦點或切回紀錄網站時先停止送地圖更新；
    /// 模擬仍由計時器照常推進，恢復時用一次完整（已抽稀的）畫面補上。
    /// </summary>
    private void UpdateMapActivity()
    {
        // 關閉途中地圖元件可能已經釋放，不能再碰 CoreWebView2。
        if (_closing)
        {
            return;
        }

        var host = _hostWindow ?? Window.GetWindow(this);
        var shouldUpdate = IsVisible &&
                           host?.WindowState != WindowState.Minimized &&
                           host?.IsActive == true;

        if (MapView.CoreWebView2 != null)
        {
            MapView.CoreWebView2.MemoryUsageTargetLevel = host?.WindowState == WindowState.Minimized
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }

        if (!shouldUpdate)
        {
            _mapUpdatesEnabled = false;
            return;
        }

        if (_mapUpdatesEnabled)
        {
            return;
        }

        _mapUpdatesEnabled = true;
        ResumeMapUpdates();
    }

    private void ResumeMapUpdates()
    {
        if (!_webReady)
        {
            return;
        }

        if (_pendingFullFrame != null)
        {
            SendMapFrame(_pendingFullFrame);
            _pendingFullFrame = null;
        }
        else if (_mapStream.DisplayPointCount > 0)
        {
            SendMapFrame(_mapStream.BuildFrame(forceReplace: true));
        }

        if (_playback != null)
        {
            SendPlaybackPosition();
        }

        if (_lastPlayback != null &&
            (_playback == null || _playback.State is PlaybackState.Completed or PlaybackState.Stopped))
        {
            Post(new { type = "playbackEnd" });
        }
    }

    private void ReadCenter(JsonElement root)
    {
        var lat = GetDouble(root, "lat");
        var lon = GetDouble(root, "lon");
        if (double.IsFinite(lat) && double.IsFinite(lon))
        {
            _mapCenterLat = lat;
            _mapCenterLon = lon;
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private MessageBoxResult ShowMessageBox(
        string message,
        string title,
        MessageBoxButton button,
        MessageBoxImage image)
    {
        var owner = _hostWindow ?? Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, message, title, button, image)
            : MessageBox.Show(message, title, button, image);
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static string FormatSpeed(double speed) =>
        speed.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatClock(long seconds) =>
        $"{seconds / 60}:{seconds % 60:00}";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var safe = builder.ToString().Trim().Trim('.').Trim();
        if (safe.Length > 80)
        {
            safe = safe[..80];
        }

        return safe.Length == 0 ? "route" : safe;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static double GetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetDouble(out var value)
            ? value
            : double.NaN;

    private async void CurrentLocation_Click(object sender, RoutedEventArgs e)
    {
        await LocateFromWindowsAsync(center: true, quiet: false);
    }

    /// <summary>開啟視窗且路線還是空的時候自動定位一次，把地圖移到使用者附近。</summary>
    private async Task AutoLocateAsync()
    {
        if (_autoLocateAttempted || _locating || _closing)
        {
            return;
        }

        _autoLocateAttempted = true;
        await LocateFromWindowsAsync(center: _route.Waypoints.Count == 0, quiet: true);
    }

    private async Task LocateFromWindowsAsync(bool center, bool quiet)
    {
        if (_closing || _locating)
        {
            return;
        }

        _locating = true;
        CurrentLocationButton.IsEnabled = false;
        if (!quiet)
        {
            SetStatus("正在透過 Windows 定位服務取得目前位置（會使用 Wi-Fi／基地台）…");
        }

        try
        {
            var result = await CurrentLocationProvider.GetAsync();
            if (_closing)
            {
                return;
            }

            if (!result.Success || result.Coordinates == null)
            {
                SetStatus(result.ErrorMessage ?? "無法取得目前位置。");
                return;
            }

            var coordinate = result.Coordinates;
            Post(new
            {
                type = "showCurrentLocation",
                lat = coordinate.Latitude,
                lon = coordinate.Longitude,
                center,
            });
            SetStatus(
                $"目前位置：{coordinate.FormattedLatitude()}, {coordinate.FormattedLongitude()}" +
                (center ? "（已移到地圖中心）" : "（Windows 定位）"));
        }
        finally
        {
            _locating = false;
            if (!_closing)
            {
                CurrentLocationButton.IsEnabled = true;
            }
        }
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        var coordinate = _playback?.Current?.Coordinates ?? _lastPlaybackCoordinate;
        if (coordinate == null)
        {
            SetStatus("目前沒有可定位的播放位置。");
            return;
        }

        Post(new
        {
            type = "locatePosition",
            lat = coordinate.Latitude,
            lon = coordinate.Longitude,
        });
        SetStatus("已定位到目前位置。");
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        // 回首頁只切換主視窗內容，模擬狀態保留；再次按「模擬」會回到同一個編輯狀態。
        BackToHomeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 側欄滾輪：游標在清單上時先讓清單自己滾，清單到頂／底後改滾整個側欄，
    /// 避免內層清單吃掉滾輪事件，導致側欄滾不到最底端。
    /// </summary>
    private void SideScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SideScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var inner = FindInnerScrollViewer(e.OriginalSource as DependencyObject);
        if (inner is not null && !ReferenceEquals(inner, SideScrollViewer) && inner.ScrollableHeight > 0)
        {
            var atTop = inner.VerticalOffset <= 0;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight - 0.5;
            if (!(e.Delta > 0 && atTop) && !(e.Delta < 0 && atBottom))
            {
                return;
            }
        }

        SideScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(SideScrollViewer.VerticalOffset - e.Delta, 0, SideScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private ScrollViewer? FindInnerScrollViewer(DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, SideScrollViewer))
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>主程式關閉時停止播放與背景工作；地圖由行程結束時釋放。</summary>
    public void Shutdown()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            IsVisibleChanged -= View_IsVisibleChanged;
            if (_hostWindow is { } host)
            {
                host.Activated -= HostWindow_Activated;
                host.Deactivated -= HostWindow_Deactivated;
                host.StateChanged -= HostWindow_StateChanged;
                _hostWindow = null;
            }
        }
        catch (Exception)
        {
            // 解除事件失敗不影響關閉。
        }

        try
        {
            _playbackTimer.Stop();
            _playbackWatch.Reset();
            _playback?.Stop();
        }
        catch (Exception)
        {
            // 播放資源可能已經停止。
        }

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
            if (MapView.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= Core_WebMessageReceived;
                core.NavigationCompleted -= Core_NavigationCompleted;
                core.Stop();
            }
        }
        catch (Exception)
        {
            // WebView2 可能還沒初始化完成。
        }

        try
        {
            _searchClient.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _uploadClient.Dispose();
        }
        catch (Exception)
        {
        }
    }
}

public sealed record WaypointRow(string Id, string Display)
{
    public override string ToString() => Display;
}

public sealed record OutageRow(RouteOutage Outage, string Display)
{
    public override string ToString() => Display;
}

public sealed record FavoriteRow(FavoritePlace Place, string Display)
{
    public override string ToString() => Display;
}

/// <summary>模擬頁「匯入過的 GPX」下拉項目。</summary>
public sealed record ImportedGpxRow(string FilePath, string Display)
{
    public override string ToString() => Display;
}
