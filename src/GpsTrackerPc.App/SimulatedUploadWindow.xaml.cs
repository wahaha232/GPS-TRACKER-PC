using System.Globalization;
using System.Net.Http;
using System.Windows;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Simulator;

namespace GpsTrackerPc.App;

/// <summary>上傳前的摘要與確認；實際寫入仍交給 GitHubRecordUploader。</summary>
public partial class SimulatedUploadWindow : Window
{
    private readonly PlaybackResult _playback;
    private readonly TokenStore _tokenStore;
    private readonly GitHubRecordUploader _uploader;
    private readonly string _deviceName;
    private readonly string _trackId = Guid.NewGuid().ToString();
    private bool _busy;

    public SimulatedUploadWindow(
        PlaybackResult playback,
        TokenStore tokenStore,
        HttpClient httpClient,
        string deviceName)
    {
        InitializeComponent();
        _playback = playback;
        _tokenStore = tokenStore;
        _uploader = new GitHubRecordUploader(httpClient, tokenStore);
        _deviceName = DeviceNameStore.Validate(deviceName);
        StartTimeBox.Text = DateTimeOffset.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        RefreshSummary();
    }

    public bool Uploaded { get; private set; }

    private void StartTimeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            RefreshSummary();
        }
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        StartTimeBox.Text = DateTimeOffset.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (!TryGetStartTime(out var startTime))
        {
            SummaryText.Text = "開始時間格式不正確，請用 yyyy/MM/dd HH:mm:ss。";
            UploadButton.IsEnabled = false;
            return;
        }

        var build = SimulatedRecordBuilder.Build(_playback, startTime, _deviceName, _trackId);
        if (!build.IsSuccess)
        {
            SummaryText.Text = "無法轉成紀錄：" + GpxErrors.Get(build.Error!);
            UploadButton.IsEnabled = false;
            return;
        }

        var track = build.Track!;
        var duration = TimeSpan.FromMilliseconds(track.DurationMs);
        SummaryText.Text =
            $"日期：{startTime.ToLocalTime():yyyy/MM/dd HH:mm:ss}\n" +
            $"距離：{(track.DistanceMeters / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} km\n" +
            $"時間：{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}\n" +
            $"點數：{track.GpsPointCount}\n" +
            $"裝置：{_deviceName}\n" +
            $"來源：simulated（模擬）" +
            (_playback.Completed ? string.Empty : "\n狀態：未完成（播放中途停止）");

        var hasToken = _tokenStore.HasToken;
        UploadButton.IsEnabled = !_busy && hasToken;
        StatusText.Text = hasToken
            ? "確認摘要與開始時間後，按「確認上傳」。"
            : "尚未設定 GitHub Token，請先回主畫面「設定」輸入。";
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_tokenStore.HasToken)
        {
            StatusText.Text = "尚未設定 GitHub Token，請先回主畫面「設定」輸入。";
            return;
        }

        if (!TryGetStartTime(out var startTime))
        {
            StatusText.Text = "開始時間格式不正確。";
            return;
        }

        var build = SimulatedRecordBuilder.Build(_playback, startTime, _deviceName, _trackId);
        if (!build.IsSuccess)
        {
            StatusText.Text = "無法轉成紀錄：" + GpxErrors.Get(build.Error!);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "紀錄會寫入公開的 GitHub 倉庫，任何人都看得到路線與時間。\n\n確定要上傳嗎？",
            "確認公開上傳",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        SetBusy(true);
        StatusText.Text = "上傳中…";

        try
        {
            var result = await _uploader.UploadAsync(build);
            switch (result.Status)
            {
                case UploadStatus.Uploaded:
                    Uploaded = true;
                    _busy = false;
                    SetBusy(false);
                    StatusText.Text = "已上傳。GitHub Pages 約 1 分鐘後生效。";
                    MessageBox.Show(
                        this,
                        "模擬紀錄已上傳（deviceName: PC、source: simulated）。\n\nGitHub Pages 約 1 分鐘後生效。",
                        "上傳完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Close();
                    break;
                case UploadStatus.Duplicate:
                    StatusText.Text = "已跳過：GitHub 上已有相同開始時間與距離的紀錄。";
                    break;
                default:
                    StatusText.Text = "上傳失敗：" + (result.ErrorMessage ?? "未知錯誤");
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "上傳失敗：" + ex.Message;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UploadButton.IsEnabled = !busy && _tokenStore.HasToken;
        CloseButton.IsEnabled = !busy;
        StartTimeBox.IsEnabled = !busy;
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

    private bool TryGetStartTime(out DateTimeOffset startTime)
    {
        var text = StartTimeBox.Text.Trim();
        return DateTimeOffset.TryParse(
                   text,
                   CultureInfo.CurrentCulture,
                   DateTimeStyles.AssumeLocal,
                   out startTime) ||
               DateTimeOffset.TryParse(
                   text,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal,
                   out startTime);
    }
}
