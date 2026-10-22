using System.Diagnostics;
using System.IO;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Appearance;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;

namespace GpsTrackerPc.App;

/// <summary>設定畫面：GitHub Token、裝置名稱與本機資料夾，不會顯示 Token 內容。</summary>
public partial class SettingsWindow : Window
{
    private readonly TokenStore _tokenStore;
    private readonly DeviceNameStore _deviceNameStore;
    private readonly UpdateCheckStore _updateCheckStore;
    private readonly ThemeSettingsStore _themeStore;
    private readonly Func<Task> _checkUpdates;
    private bool _updatingUi;

    public SettingsWindow(
        TokenStore tokenStore,
        DeviceNameStore deviceNameStore,
        UpdateCheckStore updateCheckStore,
        Func<Task> checkUpdates)
    {
        InitializeComponent();
        _tokenStore = tokenStore;
        _deviceNameStore = deviceNameStore;
        _updateCheckStore = updateCheckStore;
        _themeStore = new ThemeSettingsStore(AppPaths.ThemeFile);
        _checkUpdates = checkUpdates;
        DataPathText.Text = AppPaths.DataDirectory;
        DeviceNameBox.Text = _deviceNameStore.Load();
        VersionText.Text = $"目前版本：v{AppVersion.Current}";
        _updatingUi = true;
        AutoCheckUpdatesCheck.IsChecked = _updateCheckStore.Load().AutoCheckEnabled;
        ThemeBox.ItemsSource = ThemeCatalog.Options;
        ThemeBox.SelectedValue = _themeStore.Load();
        _updatingUi = false;
        RefreshLastCheck();
        RefreshTokenStatus();
    }

    private void SaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        var devices = ManifestDeviceReader.Read(Path.Combine(AppPaths.DataDirectory, "manifest.json"));
        var validation = DeviceNamePolicy.Validate(name, devices, _deviceNameStore.Load());
        if (!validation.IsValid)
        {
            DeviceNameStatusText.Text = validation.ErrorMessage;
            MessageBox.Show(
                this,
                validation.ErrorMessage ?? "裝置名稱不合法。",
                "無法儲存裝置名稱",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (validation.HasSimulatedConflict)
        {
            var answer = MessageBox.Show(
                this,
                (validation.WarningMessage ?? "這個名稱已被另一台模擬裝置使用。") +
                "\n\n仍要儲存嗎？上傳後會合併成同一台裝置。",
                "裝置名稱重複",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            _deviceNameStore.Save(name);
            DeviceNameBox.Text = _deviceNameStore.Load();
            DeviceNameStatusText.Text = $"已儲存：{DeviceNameBox.Text}";
        }
        catch (Exception ex)
        {
            DeviceNameStatusText.Text = "儲存失敗：" + ex.Message;
            MessageBox.Show(
                this,
                "無法儲存裝置名稱：\n\n" + ex.Message,
                "儲存失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        Close();
        await _checkUpdates();
    }

    private void AutoCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        try
        {
            _updateCheckStore.SetAutoCheck(AutoCheckUpdatesCheck.IsChecked == true);
            RefreshLastCheck();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "儲存自動檢查設定失敗：\n\n" + ex.Message,
                "儲存失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Theme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingUi || ThemeBox.SelectedValue is not AppThemePreference preference)
        {
            return;
        }

        _themeStore.Save(preference);
        ThemeManager.Apply(preference);
    }

    private void RefreshLastCheck()
    {
        var state = _updateCheckStore.Load();
        LastCheckText.Text = state.LastCheckMs is { } lastCheckMs
            ? "最後檢查更新：" + DateTimeOffset.FromUnixTimeMilliseconds(lastCheckMs)
                .ToLocalTime()
                .ToString("yyyy/MM/dd HH:mm")
            : "最後檢查更新：尚未檢查";
    }

    private void RefreshTokenStatus()
    {
        var hasToken = _tokenStore.HasToken;
        TokenStatusText.Text = hasToken
            ? "狀態：已設定（內容不會顯示）"
            : "狀態：未設定";
        TokenStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            hasToken ? "AppSuccessText" : "AppTextMuted");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            MessageBox.Show(this, "請先輸入 Token。", "Token 是空的", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _tokenStore.Save(token);
            TokenBox.Clear();
            RefreshTokenStatus();
            MessageBox.Show(this, "Token 已用 DPAPI 加密儲存在本機。", "已儲存", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // 只顯示錯誤類型與訊息，不把 Token 寫進任何輸出。
            MessageBox.Show(this, "Token 儲存失敗：\n\n" + ex.Message, "儲存失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!_tokenStore.HasToken)
        {
            MessageBox.Show(this, "目前沒有儲存 Token。", "沒有 Token", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "確定要刪除本機儲存的 GitHub Token 嗎？",
            "刪除 Token",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _tokenStore.Delete();
            TokenBox.Clear();
            RefreshTokenStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Token 刪除失敗：\n\n" + ex.Message, "刪除失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "無法開啟資料夾：\n\n" + ex.Message, "開啟失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
