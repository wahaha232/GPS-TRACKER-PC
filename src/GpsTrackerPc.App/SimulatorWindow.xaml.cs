using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Model;
using GpsTrackerPc.Simulation.Routes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace GpsTrackerPc.App;

/// <summary>
/// 6A 路線編輯視窗。地圖（simulator.html）只負責顯示與送出點選事件；
/// 路線的實際資料、順序、停留、速度與中斷都由 C# 的 RouteEditorModel 管理。
/// </summary>
public partial class SimulatorWindow : Window
{
    private const string SimulatorHost = "simulator.local";

    private readonly RouteEditorModel _route = new();
    private readonly FavoritesStore _favorites;
    private readonly RouteLibraryStore _library;
    private readonly HttpClient _searchClient;
    private readonly NominatimGeocoder _geocoder;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _webReady;
    private bool _updatingUi;
    private string? _selectedWaypointId;
    private double _mapCenterLat = Coordinates.Default.Latitude;
    private double _mapCenterLon = Coordinates.Default.Longitude;

    public SimulatorWindow()
    {
        InitializeComponent();

        _favorites = new FavoritesStore(AppPaths.FavoritesFile);
        _library = new RouteLibraryStore(AppPaths.RoutesDirectory);
        _searchClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _geocoder = new NominatimGeocoder(_searchClient);

        _updatingUi = true;
        RouteNameBox.Text = _route.Name;
        _updatingUi = false;

        RefreshAllUi(updateName: false);
        RefreshFavorites();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
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
        }
        catch (Exception ex)
        {
            ShowError("初始化模擬地圖失敗：\n\n" + ex.Message);
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowError($"載入模擬地圖失敗（{e.WebErrorStatus}）。");
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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
                    break;
                case "center":
                    ReadCenter(root);
                    break;
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

    private void SendRoute()
    {
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
        if (!_webReady || MapView.CoreWebView2 == null)
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
        try
        {
            _route.SetUseGpxTiming(TimingCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }

        RefreshTimingControls();
        SendRoute();
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

        _selectedWaypointId = _route.Waypoints.Count == 0
            ? null
            : _route.Waypoints[Math.Clamp(index, 0, _route.Waypoints.Count - 1)].Id;
        RefreshAllUi(updateName: false);
        SendRoute();
        SetStatus($"已刪除路線點，剩 {_route.Waypoints.Count} 點。");
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
            SetStatus("地點搜尋失敗：" + ex.Message);
        }
        finally
        {
            SearchButton.Content = previous;
            SearchButton.IsEnabled = true;
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
            var answer = MessageBox.Show(
                this,
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
            Owner = this,
        };
        if (window.ShowDialog() == true && window.SelectedRoute != null)
        {
            _route.ReplaceRoute(window.SelectedRoute);
            _selectedWaypointId = null;
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
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetStatus("匯入中…");
        try
        {
            var fallbackName = Path.GetFileNameWithoutExtension(dialog.FileName);
            var path = dialog.FileName;
            var imported = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return LocationDocumentCodec.ImportStream(stream, fallbackName);
            });

            _route.ReplaceRoute(imported.Route);
            _selectedWaypointId = _route.Waypoints.Count > 0 ? _route.Waypoints[0].Id : null;
            RefreshAllUi(updateName: true);
            SendRoute();
            PostFit();
            SetStatus(imported.WasSimplified
                ? $"已匯入「{_route.Name}」：原始 {imported.OriginalPointCount} 點，保留 {imported.KeptPointCount} 點。"
                : $"已匯入「{_route.Name}」（{_route.Waypoints.Count} 點）。");
        }
        catch (Exception ex)
        {
            SetStatus("匯入失敗：" + ex.Message);
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
        if (dialog.ShowDialog(this) != true)
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _lifetime.Cancel();
        _searchClient.Dispose();
        MapView.Dispose();
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
