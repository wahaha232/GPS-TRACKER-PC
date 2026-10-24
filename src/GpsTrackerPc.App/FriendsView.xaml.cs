using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Social;
using GpsTrackerPc.Simulation.Model;
using Microsoft.Web.WebView2.Core;

namespace GpsTrackerPc.App;

/// <summary>
/// 好友與群組的獨立分頁：清單、邀請碼、分享開關，以及右側的位置地圖。
/// 位置以 AES-GCM 加密後經 ntfy.sh 中繼，中繼服務看不到座標或名稱。
/// </summary>
public partial class FriendsView : UserControl
{
    private const string FriendsHost = "friends.local";

    private readonly DeviceNameStore _deviceNameStore;
    private readonly SocialSettingsStore _settingsStore;
    private readonly HttpClient _httpClient;
    private readonly NtfySocialTransport _transport;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<SocialRow> _friendRows = new();
    private readonly ObservableCollection<SocialRow> _groupRows = new();
    private readonly ObservableCollection<SocialRow> _memberRows = new();

    private SocialSettings _settings;
    private SocialDirectory _directory = new();
    private Coordinates? _selfCoordinates;
    private double? _selfAccuracyMeters;
    private DateTimeOffset? _selfUpdatedAt;
    private string? _locationError;
    private string? _selectedGroupId;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private bool _initialized;
    private bool _shareToggleGuard = true;
    private bool _refreshingRows;
    private bool _publishing;
    private bool _polling;
    private bool _webReady;
    private bool _mapStarted;
    private bool _fitMap = true;
    private bool _active;
    private bool _closing;

    public FriendsView(DeviceNameStore deviceNameStore)
    {
        InitializeComponent();

        _deviceNameStore = deviceNameStore ?? throw new ArgumentNullException(nameof(deviceNameStore));
        _settingsStore = new SocialSettingsStore(AppPaths.SocialFile);
        _settings = _settingsStore.Load();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _transport = new NtfySocialTransport(_httpClient);

        FriendList.ItemsSource = _friendRows;
        GroupList.ItemsSource = _groupRows;
        MemberList.ItemsSource = _memberRows;

        _shareToggleGuard = true;
        ShareToggle.IsChecked = _settings.ShareLocation;
        _shareToggleGuard = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        IsVisibleChanged += View_IsVisibleChanged;
        RefreshRows();
        UpdateSelfStatus();
        _initialized = true;
    }

    /// <summary>使用者在這個分頁按「回首頁」時通知主視窗。</summary>
    public event EventHandler? BackToHomeRequested;

    public bool IsSharing => _settings.ShareLocation;

    /// <summary>主視窗切換分頁時呼叫；只有顯示中才輪詢，分享開關不受影響。</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (_closing)
        {
            return;
        }

        if (active)
        {
            _fitMap = true;
            _ = RefreshAsync(poll: true);
            PostMap();
        }
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_mapStarted)
        {
            _mapStarted = true;
            _ = InitializeMapAsync();
        }

        if (_settings.ShareLocation)
        {
            _ = PublishSelfAsync();
        }

        if (_active)
        {
            _ = RefreshAsync(poll: true);
        }
    }

    private void View_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        var visible = IsVisible;
        _active = visible;
        if (visible)
        {
            _fitMap = true;
            _ = RefreshAsync(poll: true);
            PostMap();
        }
    }

    private async Task InitializeMapAsync()
    {
        try
        {
            var friendsRoot = Path.Combine(AppContext.BaseDirectory, "Friends");
            var page = Path.Combine(friendsRoot, "friends-map.html");
            if (!File.Exists(page))
            {
                ShowMapError(
                    "找不到好友地圖頁面。\n\n" +
                    $"預期位置：{page}\n\n" +
                    "請重新建置或重新安裝程式。");
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
                FriendsHost,
                friendsRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += Core_NavigationCompleted;
            MapView.Source = new Uri($"https://{FriendsHost}/friends-map.html");
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                ShowMapError("初始化好友地圖失敗：\n\n" + ex.Message);
            }
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            ShowMapError($"載入好友地圖失敗（{e.WebErrorStatus}）。請確認網路與 WebView2 Runtime。");
            return;
        }

        _webReady = true;
        LoadingPanel.Visibility = Visibility.Collapsed;
        MapErrorPanel.Visibility = Visibility.Collapsed;
        _fitMap = true;
        PostMap();
    }

    private void ShowMapError(string message)
    {
        if (_closing)
        {
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        MapErrorText.Text = message;
        MapErrorPanel.Visibility = Visibility.Visible;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (_settings.ShareLocation && now - _lastPublish >= SocialLimits.PublishInterval)
        {
            await PublishSelfAsync();
        }

        if (_active && now - _lastPoll >= SocialLimits.PollInterval)
        {
            await RefreshAsync(poll: true);
        }
    }

    private async Task PublishSelfAsync()
    {
        if (_closing || _publishing || !_settings.ShareLocation)
        {
            return;
        }

        _publishing = true;
        _lastPublish = DateTimeOffset.Now;
        try
        {
            var result = await CurrentLocationProvider.GetAsync();
            if (_closing || !_settings.ShareLocation)
            {
                return;
            }

            if (!result.Success || result.Coordinates == null)
            {
                _locationError = result.ErrorMessage ?? "無法取得目前位置。";
                UpdateSelfStatus();
                SetStatus(_locationError, error: true);
                return;
            }

            _selfCoordinates = result.Coordinates;
            _selfAccuracyMeters = result.AccuracyMeters;
            _selfUpdatedAt = DateTimeOffset.Now;
            _locationError = null;

            if (_settings.Targets.Count == 0)
            {
                SetStatus("已取得目前位置；尚未加入好友或群組，所以還沒有分享對象。");
                UpdateSelfStatus();
                PostMap();
                return;
            }

            var snapshot = new SocialSnapshot(
                _settings.DeviceId,
                GetDeviceName(),
                _selfCoordinates.Latitude,
                _selfCoordinates.Longitude,
                _selfAccuracyMeters,
                _selfUpdatedAt.Value.ToUnixTimeMilliseconds(),
                Sharing: true);

            var errors = await PublishToTargetsAsync(snapshot);
            if (errors.Count == 0)
            {
                SetStatus("已更新並分享目前位置。");
            }
            else
            {
                SetStatus("部分分享失敗：" + string.Join("；", errors.Take(2)), error: true);
            }

            UpdateSelfStatus();
            PostMap();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_closing)
            {
                return;
            }

            _locationError = "分享位置失敗：" + ex.Message;
            UpdateSelfStatus();
            SetStatus(_locationError, error: true);
        }
        finally
        {
            _publishing = false;
        }
    }

    private async Task PublishStoppedAsync()
    {
        if (_closing || _selfCoordinates == null || _settings.Targets.Count == 0)
        {
            return;
        }

        try
        {
            var snapshot = new SocialSnapshot(
                _settings.DeviceId,
                GetDeviceName(),
                _selfCoordinates.Latitude,
                _selfCoordinates.Longitude,
                _selfAccuracyMeters,
                DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Sharing: false);
            await PublishToTargetsAsync(snapshot);
            SetStatus("已停止分享，並通知好友。");
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("停止分享的通知沒有送出：" + ex.Message, error: true);
            }
        }
    }

    private async Task<List<string>> PublishToTargetsAsync(SocialSnapshot snapshot)
    {
        var errors = new List<string>();
        foreach (var target in _settings.Targets.ToList())
        {
            try
            {
                await _transport.PublishAsync(target, snapshot, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{target.Name}：{ex.Message}");
            }
        }

        return errors;
    }

    private async Task RefreshAsync(bool poll)
    {
        if (_closing || _polling)
        {
            return;
        }

        _polling = true;
        _lastPoll = DateTimeOffset.Now;
        try
        {
            _settings = _settingsStore.Load();
            _directory.Retain(_settings.Targets.Select(target => target.Id));

            if (poll && _settings.Targets.Count > 0)
            {
                var errors = new List<string>();
                foreach (var target in _settings.Targets.ToList())
                {
                    try
                    {
                        var result = await _transport.PollAsync(
                            target,
                            _directory.GetLastMessageId(target.Id),
                            _lifetime.Token);
                        _directory.Merge(target.Id, result.Snapshots);
                        _directory.SetLastMessageId(target.Id, result.LastMessageId);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{target.Name}：{ex.Message}");
                    }
                }

                if (errors.Count > 0)
                {
                    SetStatus("讀取位置失敗：" + string.Join("；", errors.Take(2)), error: true);
                }
                else
                {
                    SetStatus("已更新：" + DateTime.Now.ToString("HH:mm:ss"));
                }
            }

            RefreshRows();
            PostMap();
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                SetStatus("更新好友位置失敗：" + ex.Message, error: true);
            }
        }
        finally
        {
            _polling = false;
        }
    }

    private void RefreshRows()
    {
        _refreshingRows = true;
        var selectedGroupBefore = _selectedGroupId;
        try
        {
            var now = DateTimeOffset.Now;
            var peers = _directory.BuildPeers(
                _settings.Targets,
                _settings.DeviceId,
                _selfCoordinates,
                now);

            _friendRows.Clear();
            _groupRows.Clear();
            _memberRows.Clear();

            foreach (var target in _settings.Targets.Where(target => target.Kind == SocialTargetKind.Friend))
            {
                var members = peers
                    .Where(peer => peer.TargetId == target.Id)
                    .OrderByDescending(peer => peer.Online)
                    .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (members.Count == 0)
                {
                    _friendRows.Add(new SocialRow(
                        Id: target.Id,
                        Kind: target.Kind,
                        TargetId: target.Id,
                        Name: target.Name,
                        Status: "… 等待對方加入",
                        Detail: "把邀請碼傳給朋友，對方加入後就會顯示位置。",
                        Meta: "尚未收到位置",
                        Online: false,
                        Stopped: false));
                    continue;
                }

                foreach (var peer in members)
                {
                    _friendRows.Add(CreatePeerRow(peer, now));
                }
            }

            foreach (var target in _settings.Targets.Where(target => target.Kind == SocialTargetKind.Group))
            {
                var members = peers
                    .Where(peer => peer.TargetId == target.Id)
                    .OrderByDescending(peer => peer.Online)
                    .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                var online = members.Count(peer => peer.Online);
                var latest = members.Count == 0
                    ? (DateTimeOffset?)null
                    : members.Max(peer => peer.UpdatedAt);

                _groupRows.Add(new SocialRow(
                    Id: target.Id,
                    Kind: target.Kind,
                    TargetId: target.Id,
                    Name: target.Name,
                    Status: members.Count == 0 ? "尚無成員" : $"{online}/{members.Count} 位在線上",
                    Detail: members.Count == 0
                        ? "把邀請碼傳給朋友，對方加入後就會出現在這裡。"
                        : $"{members.Count} 位成員",
                    Meta: latest is null ? "尚未收到位置" : "最近更新：" + FormatUpdated(latest.Value, now),
                    Online: online > 0,
                    Stopped: false));
            }

            if (_groupRows.Count == 0)
            {
                _selectedGroupId = null;
            }
            else if (selectedGroupBefore == null ||
                     _groupRows.All(row => row.Id != selectedGroupBefore))
            {
                _selectedGroupId = _groupRows[0].Id;
            }

            if (_selectedGroupId != null)
            {
                var selectedTarget = _settings.Targets.FirstOrDefault(
                    target => target.Kind == SocialTargetKind.Group && target.Id == _selectedGroupId);
                var groupRow = _groupRows.FirstOrDefault(row => row.Id == _selectedGroupId);
                if (selectedTarget != null && groupRow != null)
                {
                    GroupList.SelectedItem = groupRow;
                    var members = peers
                        .Where(peer => peer.TargetId == _selectedGroupId)
                        .OrderByDescending(peer => peer.Online)
                        .ThenBy(peer => peer.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var peer in members)
                    {
                        _memberRows.Add(CreatePeerRow(peer, now));
                    }
                }
            }
        }
        finally
        {
            _refreshingRows = false;
        }
    }

    private static SocialRow CreatePeerRow(SocialPeer peer, DateTimeOffset now) =>
        new(
            Id: peer.DeviceId,
            Kind: peer.Kind,
            TargetId: peer.TargetId,
            Name: string.IsNullOrWhiteSpace(peer.DisplayName) ? "未命名裝置" : peer.DisplayName,
            Status: !peer.Sharing ? "○ 已停止分享" : peer.Online ? "● 線上" : "○ 離線",
            Detail: FormatLocation(peer),
            Meta: "更新：" + FormatUpdated(peer.UpdatedAt, now),
            Online: peer.Online,
            Stopped: !peer.Sharing);

    private static string FormatLocation(SocialPeer peer)
    {
        var coordinates =
            peer.Latitude.ToString("F6", CultureInfo.InvariantCulture) + ", " +
            peer.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        var accuracy = peer.AccuracyMeters is { } meters
            ? $"（±{meters.ToString("F0", CultureInfo.InvariantCulture)} m）"
            : string.Empty;
        var distance = peer.DistanceMeters is { } metersAway
            ? "距離 " + FormatDistance(metersAway) + "・"
            : string.Empty;
        return distance + coordinates + accuracy;
    }

    private static string FormatDistance(double meters) =>
        meters < 1000
            ? meters.ToString("F0", CultureInfo.InvariantCulture) + " 公尺"
            : (meters / 1000).ToString("F1", CultureInfo.InvariantCulture) + " 公里";

    private static string FormatUpdated(DateTimeOffset updatedAt, DateTimeOffset now)
    {
        var age = now - updatedAt;
        if (age < TimeSpan.Zero || age <= TimeSpan.FromMinutes(2))
        {
            return "剛剛";
        }

        return updatedAt.ToLocalTime().ToString("MM/dd HH:mm");
    }

    private void PostMap()
    {
        if (_closing || !_webReady || MapView.CoreWebView2 == null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        IEnumerable<SocialPeer> peers = _directory.BuildPeers(
            _settings.Targets,
            _settings.DeviceId,
            _selfCoordinates,
            now);

        if (KindTabs.SelectedIndex == 1)
        {
            peers = _selectedGroupId == null
                ? peers.Where(peer => peer.Kind == SocialTargetKind.Group)
                : peers.Where(peer => peer.TargetId == _selectedGroupId);
        }

        var peerPayload = peers
            .Select(peer => new
            {
                id = peer.DeviceId,
                name = peer.DisplayName,
                lat = peer.Latitude,
                lng = peer.Longitude,
                online = peer.Online,
                sharing = peer.Sharing,
                distanceText = peer.DistanceMeters is { } meters ? FormatDistance(meters) : string.Empty,
                updatedText = FormatUpdated(peer.UpdatedAt, now),
            })
            .ToArray();

        object? self = _selfCoordinates == null
            ? null
            : new
            {
                name = "我（" + GetDeviceName() + "）",
                lat = _selfCoordinates.Latitude,
                lng = _selfCoordinates.Longitude,
            };

        var message = new
        {
            type = "presence",
            presence = new
            {
                self,
                peers = peerPayload,
                fit = _fitMap,
            },
        };

        try
        {
            MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
            _fitMap = false;
        }
        catch (Exception)
        {
            // WebView2 正在關閉或尚未就緒；下次更新再送。
        }
    }

    private void UpdateSelfStatus()
    {
        if (!_settings.ShareLocation)
        {
            SelfStatusText.Text = "目前沒有分享位置。";
            SelfStatusText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextMuted");
            return;
        }

        if (_locationError != null)
        {
            SelfStatusText.Text = "分享中，但" + _locationError;
            SelfStatusText.SetResourceReference(TextBlock.ForegroundProperty, "AppDangerText");
            return;
        }

        if (_selfCoordinates == null || _selfUpdatedAt == null)
        {
            SelfStatusText.Text = "分享中，正在取得目前位置…";
            SelfStatusText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextMuted");
            return;
        }

        var accuracy = _selfAccuracyMeters is { } meters
            ? $"（±{meters.ToString("F0", CultureInfo.InvariantCulture)} m）"
            : string.Empty;
        SelfStatusText.Text =
            "分享中：" +
            _selfCoordinates.Latitude.ToString("F6", CultureInfo.InvariantCulture) + ", " +
            _selfCoordinates.Longitude.ToString("F6", CultureInfo.InvariantCulture) +
            accuracy + "・" + _selfUpdatedAt.Value.ToLocalTime().ToString("HH:mm:ss");
        SelfStatusText.SetResourceReference(TextBlock.ForegroundProperty, "AppSuccessText");
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            error ? "AppDangerText" : "AppTextMuted");
    }

    private async void ShareToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_shareToggleGuard || _closing)
        {
            return;
        }

        var enabled = ShareToggle.IsChecked == true;
        try
        {
            _settings = _settingsStore.SetShareLocation(enabled);
        }
        catch (Exception ex)
        {
            SetStatus("儲存分享設定失敗：" + ex.Message, error: true);
            _shareToggleGuard = true;
            ShareToggle.IsChecked = _settings.ShareLocation;
            _shareToggleGuard = false;
            return;
        }

        UpdateSelfStatus();
        if (enabled)
        {
            _lastPublish = DateTimeOffset.MinValue;
            SetStatus("已開啟分享，正在取得目前位置…");
            await PublishSelfAsync();
        }
        else
        {
            SetStatus("已停止分享。");
            await PublishStoppedAsync();
        }

        RefreshRows();
        PostMap();
    }

    private void InviteFriend_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _settingsStore.CreateTarget(SocialTargetKind.Friend, "等待好友加入");
            AddTarget(target);
            ShowInviteCode(target);
        }
        catch (Exception ex)
        {
            ShowMessage(
                "建立好友邀請失敗：\n\n" + ex.Message,
                "建立失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InviteCodeWindow(
            "新增好友",
            "貼上好友邀請碼",
            "邀請碼包含這次分享的連線金鑰，請只傳給信任的人；它不會包含 GitHub Token。",
            multiline: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var target = SocialInviteCode.Decode(dialog.Value, DateTimeOffset.Now);
            if (target.Kind != SocialTargetKind.Friend)
            {
                ShowMessage("這是群組邀請碼。請切到「群組」分頁後按「加入群組」。",
                    "邀請碼類型不符",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AddTarget(target);
        }
        catch (SocialException ex)
        {
            ShowMessage(ex.Message,
                "無法加入好友",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage("加入好友失敗：\n\n" + ex.Message,
                "加入失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InviteCodeWindow(
            "建立群組",
            "群組名稱",
            "建立後會產生邀請碼；朋友貼上邀請碼就能加入這個群組。",
            multiline: false)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var target = _settingsStore.CreateTarget(SocialTargetKind.Group, dialog.Value);
            AddTarget(target);
            ShowInviteCode(target);
        }
        catch (Exception ex)
        {
            ShowMessage("建立群組失敗：\n\n" + ex.Message,
                "建立失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void JoinGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InviteCodeWindow(
            "加入群組",
            "貼上群組邀請碼",
            "邀請碼包含這個群組的分享金鑰，請只傳給信任的人。",
            multiline: true)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var target = SocialInviteCode.Decode(dialog.Value, DateTimeOffset.Now);
            if (target.Kind != SocialTargetKind.Group)
            {
                ShowMessage("這是好友邀請碼。請切到「好友」分頁後按「新增好友」。",
                    "邀請碼類型不符",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AddTarget(target);
        }
        catch (SocialException ex)
        {
            ShowMessage(ex.Message,
                "無法加入群組",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowMessage("加入群組失敗：\n\n" + ex.Message,
                "加入失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AddTarget(SocialTarget target)
    {
        _settings = _settingsStore.AddTarget(target);
        _directory.Remove(target.Id);
        _directory.Retain(_settings.Targets.Select(item => item.Id));
        if (target.Kind == SocialTargetKind.Group)
        {
            _selectedGroupId = target.Id;
        }

        _fitMap = true;
        RefreshRows();
        PostMap();
        SetStatus(target.Kind == SocialTargetKind.Group
            ? "已加入群組，正在取得成員位置…"
            : "已加入好友，正在取得對方位置…");
        _lastPoll = DateTimeOffset.MinValue;
        _ = RefreshAsync(poll: true);
        if (_settings.ShareLocation)
        {
            _lastPublish = DateTimeOffset.MinValue;
            _ = PublishSelfAsync();
        }
    }

    private void ShowInvite_Click(object sender, RoutedEventArgs e)
    {
        var target = GetSelectedTarget();
        if (target == null)
        {
            ShowMessage("請先在清單中選擇好友或群組。",
                "尚未選擇",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            ShowInviteCode(target);
        }
        catch (Exception ex)
        {
            ShowMessage("產生邀請碼失敗：\n\n" + ex.Message,
                "產生失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowInviteCode(SocialTarget target)
    {
        var code = SocialInviteCode.Encode(target, GetDeviceName());
        var description = target.Kind == SocialTargetKind.Group
            ? $"把這段邀請碼傳給朋友，對方在「好友 & 群組 → 群組 → 加入群組」貼上後就能加入「{target.Name}」。"
            : "把這段邀請碼傳給朋友，對方在「好友 & 群組 → 好友 → 新增好友」貼上後，雙方就能互相看到位置。";
        var window = new InviteDisplayWindow(
            target.Kind == SocialTargetKind.Group ? "群組邀請碼" : "好友邀請碼",
            description +
            "\n邀請碼等於分享權杖，任何拿到的人都能看到這個分享的位置；請勿公開張貼。",
            code)
        {
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var target = GetSelectedTarget();
        if (target == null)
        {
            ShowMessage("請先在清單中選擇好友或群組。",
                "尚未選擇",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var kindText = target.Kind == SocialTargetKind.Group ? "群組" : "好友";
        var answer = ShowMessage($"確定要移除{kindText}「{target.Name}」嗎？\n\n移除後這台電腦就不再讀取或分享到這個連線；已分享的邀請碼仍然有效。",
            "移除" + kindText,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _settings = _settingsStore.RemoveTarget(target.Kind, target.Id);
            _directory.Remove(target.Id);
            if (_selectedGroupId == target.Id)
            {
                _selectedGroupId = null;
            }

            _fitMap = true;
            RefreshRows();
            PostMap();
            SetStatus($"已移除{kindText}。");
        }
        catch (Exception ex)
        {
            ShowMessage("移除失敗：\n\n" + ex.Message,
                "移除失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _fitMap = true;
        _lastPoll = DateTimeOffset.MinValue;
        _ = RefreshAsync(poll: true);
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e) =>
        BackToHomeRequested?.Invoke(this, EventArgs.Empty);

    private void KindTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _refreshingRows || _closing)
        {
            return;
        }

        if (KindTabs.SelectedIndex == 1)
        {
            if (_selectedGroupId == null && _groupRows.Count > 0)
            {
                _selectedGroupId = _groupRows[0].Id;
            }

            RefreshRows();
        }

        _fitMap = true;
        PostMap();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _refreshingRows || _closing)
        {
            return;
        }

        if (GroupList.SelectedItem is SocialRow row)
        {
            _selectedGroupId = row.TargetId;
            RefreshRows();
        }

        _fitMap = true;
        PostMap();
    }

    private void FriendList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 目前好友清單不需要額外明細；保留事件讓鍵盤操作也能選取。
    }

    private SocialTarget? GetSelectedTarget()
    {
        if (KindTabs.SelectedIndex == 0)
        {
            return FriendList.SelectedItem is SocialRow row
                ? _settings.Targets.FirstOrDefault(target =>
                    target.Kind == SocialTargetKind.Friend && target.Id == row.TargetId)
                : null;
        }

        var groupId = (GroupList.SelectedItem as SocialRow)?.TargetId ?? _selectedGroupId;
        return groupId == null
            ? null
            : _settings.Targets.FirstOrDefault(target =>
                target.Kind == SocialTargetKind.Group && target.Id == groupId);
    }

    private string GetDeviceName()
    {
        var name = SocialValidation.NormalizeName(_deviceNameStore.Load());
        return name.Length == 0 ? DeviceNameStore.DefaultName : name;
    }

    private MessageBoxResult ShowMessage(
        string text,
        string title,
        MessageBoxButton button,
        MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        return owner != null
            ? MessageBox.Show(owner, text, title, button, image)
            : MessageBox.Show(text, title, button, image);
    }

    /// <summary>主程式關閉時停止計時器與網路請求；WebView2 由行程結束時釋放。</summary>
    public void Shutdown()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();
        IsVisibleChanged -= View_IsVisibleChanged;

        try
        {
            _lifetime.Cancel();
        }
        catch (Exception)
        {
        }

        try
        {
            if (MapView.CoreWebView2 is { } core)
            {
                core.NavigationCompleted -= Core_NavigationCompleted;
                core.Stop();
            }
        }
        catch (Exception)
        {
        }

        try
        {
            _httpClient.Dispose();
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>好友／群組清單與成員清單共用的顯示資料。</summary>
public sealed record SocialRow(
    string Id,
    SocialTargetKind Kind,
    string TargetId,
    string Name,
    string Status,
    string Detail,
    string Meta,
    bool Online,
    bool Stopped)
{
    public override string ToString() => Name;
}
