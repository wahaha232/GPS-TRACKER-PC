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
    private readonly UpdateChannel _channel;
    private readonly Action? _requestRestart;
    private UpdateInfo? _update;
    private bool _busy;
    private CancellationTokenSource? _downloadCts;

    public UpdateWindow(
        UpdateService updateService,
        UpdateCheckStore checkStore,
        string currentVersion,
        UpdateInfo? initialUpdate = null,
        UpdateChannel channel = UpdateChannel.Stable,
        Action? requestRestart = null)
    {
        InitializeComponent();
        _updateService = updateService;
        _checkStore = checkStore;
        _currentVersion = currentVersion;
        _initialUpdate = initialUpdate;
        _channel = channel;
        _requestRestart = requestRestart;
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
            var update = await _updateService.CheckAsync(_currentVersion, _channel);
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
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
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
            var downloadFinished = false;
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (downloadFinished)
                {
                    return;
                }

                if (value.Fraction is { } fraction)
                {
                    Progress.Value = Math.Clamp(fraction, 0.0, 1.0);
                }

                StatusText.Text =
                    $"下載更新 v{_update.Version}… " +
                    (value.Fraction is { } percent ? percent.ToString("P0") : FormatBytes(value.BytesReceived)) +
                    $"・{FormatBytes(value.BytesPerSecond)}/s";
            });

            try
            {
                await _updateService.DownloadWithResumeAsync(_update, destination, progress, _downloadCts.Token);
            }
            finally
            {
                downloadFinished = true;
            }

            var signature = UpdateSignature.Inspect(destination);
            if (signature.Status == UpdateSignatureStatus.Invalid)
            {
                ShowError("更新檔數位簽章不合法，已停止安裝：" + (signature.Error ?? "簽章驗證失敗"));
                return;
            }

            var executable = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "GpsTrackerPc.exe");
            var requiredBytes = new FileInfo(destination).Length * 2 + 50L * 1024 * 1024;
            if (!UpdateInstallChecks.HasFreeSpace(
                    Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                    requiredBytes,
                    out var availableBytes))
            {
                ShowError(
                    $"安裝空間不足：需要約 {FormatBytes(requiredBytes)}，可用 {FormatBytes(availableBytes)}。");
                return;
            }

            // 不在這裡直接開新行程：先讓主視窗暫停模擬並存檔，再由同一個流程重啟。
            var apply = UpdateInstaller.Apply(destination, executable, restart: () => { });
            if (!apply.Success)
            {
                ShowError("更新失敗，已保留舊版：" + apply.ErrorMessage);
                return;
            }

            StatusText.Text = "更新完成，正在重新啟動…";
            SetBusy(false);
            if (_requestRestart != null)
            {
                _requestRestart();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下載；已下載的部分會保留，下次下載會嘗試續傳。";
        }
        catch (Exception ex)
        {
            ShowError("更新失敗，已保留舊版：" + ex.Message);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
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
        CancelButton.IsEnabled = busy;
        if (!busy)
        {
            Progress.Value = 0;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts == null || _downloadCts.IsCancellationRequested)
        {
            return;
        }

        _downloadCts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消下載…";
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
            StatusText.Text = "下載中，請先按「取消下載」或等完成。";
        }
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
}
