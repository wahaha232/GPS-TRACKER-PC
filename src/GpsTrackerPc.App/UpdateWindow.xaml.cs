using System.Diagnostics;
using System.IO;
using System.Windows;
using GpsTrackerPc.Core.Updates;

namespace GpsTrackerPc.App;

/// <summary>雲端更新畫面：檢查、顯示版本與 Release 說明，使用者按「更新」才下載安裝。</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updateService;
    private readonly UpdateCheckStore _checkStore;
    private readonly string _currentVersion;
    private readonly UpdateInfo? _initialUpdate;
    private UpdateInfo? _update;
    private bool _busy;

    public UpdateWindow(
        UpdateService updateService,
        UpdateCheckStore checkStore,
        string currentVersion,
        UpdateInfo? initialUpdate = null)
    {
        InitializeComponent();
        _updateService = updateService;
        _checkStore = checkStore;
        _currentVersion = currentVersion;
        _initialUpdate = initialUpdate;
        CurrentVersionText.Text = $"目前版本：v{_currentVersion}";
        UpdateButton.IsEnabled = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialUpdate != null)
        {
            ShowUpdate(_initialUpdate);
            return;
        }

        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        SetBusy(true);
        StatusText.Text = "正在檢查雲端版本…";
        try
        {
            var update = await _updateService.CheckAsync(_currentVersion);
            MarkChecked();
            if (update == null)
            {
                ShowLatest();
            }
            else
            {
                ShowUpdate(update);
            }
        }
        catch (Exception ex)
        {
            // 失敗不記錄檢查時間，讓下次自動檢查還會再試。
            ShowError("檢查失敗：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowUpdate(UpdateInfo update)
    {
        if (!UpdateVersion.IsNewer(_currentVersion, update.Version))
        {
            ShowError("伺服器上的版本不比目前版本新，已停止更新（降版保護）。");
            return;
        }

        _update = update;
        CurrentVersionText.Text = $"目前版本 v{_currentVersion} → 新版本 v{update.Version}";
        StatusText.Text = "有新版本。按「更新」下載並安裝。";
        NotesBox.Text = string.IsNullOrWhiteSpace(update.Notes)
            ? "（Release 沒有提供更新說明）"
            : update.Notes;
        UpdateButton.IsEnabled = true;
    }

    private void ShowLatest()
    {
        _update = null;
        StatusText.Text = $"已是最新版本（v{_currentVersion}）。";
        NotesBox.Text = string.Empty;
        UpdateButton.IsEnabled = false;
    }

    private void ShowError(string message)
    {
        _update = null;
        StatusText.Text = message;
        UpdateButton.IsEnabled = false;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _update == null)
        {
            return;
        }

        if (!UpdateVersion.IsNewer(_currentVersion, _update.Version))
        {
            ShowError("這個版本不比目前版本新，已停止更新（降版保護）。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"要下載並安裝 v{_update.Version} 嗎？",
            "安裝更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var fileName = Path.GetFileName(_update.AssetName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                ShowError("更新檔名不正確，已取消。");
                return;
            }

            // 目前只支援單一 EXE；ZIP 直接覆蓋 EXE 會損壞安裝。
            if (!string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("這個 Release 只提供 ZIP，無法自動更新。請手動下載解壓縮後覆蓋程式。");
                return;
            }

            var directory = Path.Combine(Path.GetTempPath(), "GpsTrackerPc-update");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, fileName);
            StatusText.Text = $"下載更新 v{_update.Version}…";
            var progress = new Progress<double?>(value =>
            {
                if (value.HasValue)
                {
                    Progress.Value = Math.Clamp(value.Value, 0.0, 1.0);
                    StatusText.Text = $"下載更新 v{_update.Version}… {value.Value:P0}";
                }
            });

            await _updateService.DownloadAsync(_update, destination, progress);

            var executable = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "GpsTrackerPc.exe");
            var apply = UpdateInstaller.Apply(destination, executable, () =>
            {
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            });
            if (!apply.Success)
            {
                ShowError("更新失敗，已保留舊版：" + apply.ErrorMessage);
                return;
            }

            StatusText.Text = "更新完成，正在重新啟動…";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ShowError("更新失敗，已保留舊版：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void MarkChecked()
    {
        try
        {
            _checkStore.MarkChecked(DateTimeOffset.Now);
        }
        catch (Exception)
        {
            // 檢查時間寫不進去不影響更新流程。
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy && _update != null;
        CloseButton.IsEnabled = !busy;
        if (!busy)
        {
            Progress.Value = 0;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            Close();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
        }
    }
}
