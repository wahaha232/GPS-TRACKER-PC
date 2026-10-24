using System.Diagnostics;
using System.IO;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Appearance;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Settings;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Core.Updates;
using Microsoft.Win32;

namespace GpsTrackerPc.App;

/// <summary>設定畫面：GitHub Token、裝置名稱與本機資料夾，不會顯示 Token 內容。</summary>
public partial class SettingsWindow : Window
{
    private readonly TokenStore _tokenStore;
    private readonly DeviceNameStore _deviceNameStore;
    private readonly UpdateCheckStore _updateCheckStore;
    private readonly ThemeSettingsStore _themeStore;
    private readonly TraySettingsStore _trayStore;
    private readonly StorageSettingsStore _storageStore;
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
        _trayStore = new TraySettingsStore(AppPaths.TraySettingsFile);
        _storageStore = new StorageSettingsStore(AppPaths.StorageSettingsFile);
        _checkUpdates = checkUpdates;
        RefreshDataPath();
        DeviceNameBox.Text = _deviceNameStore.Load();
        VersionText.Text = $"目前版本：v{AppVersion.Current}";
        _updatingUi = true;
        var updateState = _updateCheckStore.Load();
        AutoCheckUpdatesCheck.IsChecked = updateState.AutoCheckEnabled;
        BackgroundDownloadCheck.IsChecked = updateState.BackgroundDownloadEnabled;
        AutoInstallUpdateCheck.IsChecked = updateState.InstallMode == UpdateInstallMode.Automatic;
        AutoInstallUpdateCheck.IsEnabled = updateState.BackgroundDownloadEnabled;
        AutoUploadPendingCheck.IsChecked = updateState.AutoUploadPendingRecords;
        MinimizeToTrayCheck.IsChecked = _trayStore.Load().MinimizeToTray;
        ThemeBox.ItemsSource = ThemeCatalog.Options;
        ThemeBox.SelectedValue = _themeStore.Load();
        _updatingUi = false;
        RefreshLastCheck();
        RefreshTokenStatus();
    }

    private void SaveDeviceName_Click(object sender, RoutedEventArgs e) =>
        TrySaveDeviceName(showSuccessMessage: true);

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveDeviceName(showSuccessMessage: false))
        {
            return;
        }

        try
        {
            if (ThemeBox.SelectedValue is AppThemePreference preference)
            {
                _themeStore.Save(preference);
                ThemeManager.Apply(preference);
            }

            _updateCheckStore.SetAutoCheck(AutoCheckUpdatesCheck.IsChecked == true);
            _updateCheckStore.SetBackgroundDownload(BackgroundDownloadCheck.IsChecked == true);
            _updateCheckStore.SetInstallMode(AutoInstallUpdateCheck.IsChecked == true
                ? UpdateInstallMode.Automatic
                : UpdateInstallMode.Manual);
            _updateCheckStore.SetAutoUploadPendingRecords(AutoUploadPendingCheck.IsChecked == true);
            _trayStore.SetMinimizeToTray(MinimizeToTrayCheck.IsChecked == true);
            DeviceNameStatusText.Text = "已套用設定。";
        }
        catch (Exception ex)
        {
            DeviceNameStatusText.Text = "套用設定失敗：" + ex.Message;
        }
    }

    private bool TrySaveDeviceName(bool showSuccessMessage)
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
            return false;
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
                return false;
            }
        }

        try
        {
            _deviceNameStore.Save(name);
            DeviceNameBox.Text = _deviceNameStore.Load();
            DeviceNameStatusText.Text = $"已儲存：{DeviceNameBox.Text}";
            if (showSuccessMessage)
            {
                MessageBox.Show(this, "裝置名稱已儲存。", "已儲存", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return true;
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
            return false;
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
            ShowUpdateSettingsError(ex);
        }
    }

    private void BackgroundDownload_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        try
        {
            var enabled = BackgroundDownloadCheck.IsChecked == true;
            _updateCheckStore.SetBackgroundDownload(enabled);
            AutoInstallUpdateCheck.IsEnabled = enabled;
            if (!enabled)
            {
                _updatingUi = true;
                AutoInstallUpdateCheck.IsChecked = false;
                _updatingUi = false;
                _updateCheckStore.SetInstallMode(UpdateInstallMode.Manual);
            }
        }
        catch (Exception ex)
        {
            ShowUpdateSettingsError(ex);
        }
    }

    private void AutoInstallUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        try
        {
            _updateCheckStore.SetInstallMode(AutoInstallUpdateCheck.IsChecked == true
                ? UpdateInstallMode.Automatic
                : UpdateInstallMode.Manual);
        }
        catch (Exception ex)
        {
            ShowUpdateSettingsError(ex);
        }
    }

    private void AutoUploadPending_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        try
        {
            _updateCheckStore.SetAutoUploadPendingRecords(AutoUploadPendingCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            ShowUpdateSettingsError(ex);
        }
    }

    private void ShowUpdateSettingsError(Exception ex)
    {
        MessageBox.Show(
            this,
            "儲存更新設定失敗：\n\n" + ex.Message,
            "儲存失敗",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void MinimizeToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        try
        {
            _trayStore.SetMinimizeToTray(MinimizeToTrayCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "儲存視窗行為設定失敗：\n\n" + ex.Message,
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

    private void ChooseDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇本機資料夾",
            Multiselect = false,
            InitialDirectory = Directory.Exists(AppPaths.DataDirectory)
                ? AppPaths.DataDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!AppPaths.TryValidateDataDirectory(dialog.FolderName, out var error))
        {
            MessageBox.Show(
                this,
                "這個資料夾無法寫入：\n\n" + error,
                "資料夾無法使用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            _storageStore.SetDataDirectory(dialog.FolderName);
            RefreshDataPath();
            MessageBox.Show(
                this,
                "已設定本機資料夾。\n\n重新啟動程式後才會改用新路徑；原本的資料不會自動搬移。",
                "需要重新啟動",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "儲存本機資料夾設定失敗：\n\n" + ex.Message,
                "儲存失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ResetDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _storageStore.SetDataDirectory(null);
            RefreshDataPath();
            MessageBox.Show(
                this,
                "已恢復預設資料夾。\n\n重新啟動程式後生效。",
                "恢復預設",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "恢復預設資料夾失敗：\n\n" + ex.Message,
                "儲存失敗",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshDataPath()
    {
        var configured = _storageStore.Load().DataDirectory;
        var effective = string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultDataDirectory : configured;
        DataPathText.Text = effective;

        var same = false;
        try
        {
            same = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(effective)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 路徑格式不正確時視為不同，提示重新啟動後確認。
        }

        DataFolderStatusText.Text = same
            ? "目前使用這個資料夾。"
            : "已變更，重新啟動程式後生效。";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
