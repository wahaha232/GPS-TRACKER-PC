using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Gpx;
using Microsoft.Win32;

namespace GpsTrackerPc.App;

/// <summary>GPX 批次匯出：列出本機 manifest 的紀錄，勾選後一次寫到指定資料夾。</summary>
public partial class ExportWindow : Window
{
    private readonly string _dataDirectory;

    public ExportWindow(string dataDirectory)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory;
        DataContext = this;
        LoadRecords();
    }

    public ObservableCollection<ExportRecordItem> Records { get; } = new();

    private void LoadRecords()
    {
        var manifestPath = Path.Combine(_dataDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            StatusText.Text = "找不到本機資料，請先回主畫面按「立即同步」。";
            ExportButton.IsEnabled = false;
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array)
            {
                StatusText.Text = "manifest.json 沒有 tracks 陣列。";
                ExportButton.IsEnabled = false;
                return;
            }

            var items = new List<ExportRecordItem>();
            foreach (var track in tracks.EnumerateArray())
            {
                var trackId = GetString(track, "trackId");
                if (string.IsNullOrWhiteSpace(trackId))
                {
                    continue;
                }

                var file = GetString(track, "file");
                if (string.IsNullOrWhiteSpace(file))
                {
                    file = trackId + ".json";
                }

                var startTimeMs = GetLong(track, "startTimeMs");
                var distanceMeters = GetDouble(track, "distanceMeters");
                var durationMs = GetLong(track, "durationMs");
                var mode = GetString(track, "dominantMode") ?? "UNKNOWN";
                var device = GetString(track, "deviceName");
                var item = new ExportRecordItem
                {
                    TrackId = trackId,
                    FileName = Path.GetFileName(file),
                    StartTimeMs = startTimeMs,
                    DateText = FormatDate(startTimeMs),
                    ModeText = ModeCatalog.DisplayName(mode),
                    DistanceText = (distanceMeters / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " km",
                    DurationText = FormatDuration(durationMs),
                    DeviceText = string.IsNullOrWhiteSpace(device) ? "未命名裝置" : device,
                };
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ExportRecordItem.IsSelected))
                    {
                        UpdateCount();
                    }
                };
                items.Add(item);
            }

            foreach (var item in items.OrderByDescending(item => item.StartTimeMs))
            {
                Records.Add(item);
            }

            StatusText.Text = $"共 {Records.Count} 筆紀錄；勾選後選資料夾即可匯出。";
            ExportButton.IsEnabled = Records.Count > 0;
            UpdateCount();
        }
        catch (Exception ex)
        {
            StatusText.Text = "讀取 manifest.json 失敗：" + ex.Message;
            ExportButton.IsEnabled = false;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Records)
        {
            item.IsSelected = true;
        }

        UpdateCount();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Records)
        {
            item.IsSelected = false;
        }

        UpdateCount();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var selected = Records.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "請先勾選要匯出的紀錄。", "尚未選擇", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "選擇 GPX 儲存資料夾",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetBusy(true);
        Progress.Minimum = 0;
        Progress.Maximum = selected.Count;
        Progress.Value = 0;

        var success = 0;
        var failed = 0;
        string? lastError = null;

        for (var index = 0; index < selected.Count; index++)
        {
            var item = selected[index];
            StatusText.Text = $"匯出中… {index + 1} / {selected.Count}（{item.TrackId}）";
            try
            {
                var sourcePath = Path.Combine(_dataDirectory, item.FileName);
                var json = await File.ReadAllTextAsync(sourcePath);
                var track = JsonSerializer.Deserialize<GpxTrackDocument>(json, GpxJson.Options)
                    ?? throw new InvalidDataException("紀錄檔格式不正確。");
                var gpx = GpxExporter.Export(track);
                var targetPath = Path.Combine(dialog.FolderName, item.TrackId + ".gpx");
                await File.WriteAllTextAsync(targetPath, gpx, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = $"{item.TrackId}：{ex.Message}";
            }

            Progress.Value = index + 1;
        }

        StatusText.Text = $"完成：成功 {success} 筆、失敗 {failed} 筆。" +
            (failed > 0 ? $" 最後錯誤：{lastError}" : string.Empty);
        SetBusy(false);

        if (failed > 0)
        {
            MessageBox.Show(
                this,
                $"有 {failed} 筆匯出失敗。\n\n{lastError}",
                "匯出完成（部分失敗）",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SetBusy(bool busy)
    {
        ExportButton.IsEnabled = !busy && Records.Count > 0;
        CloseButton.IsEnabled = !busy;
    }

    private void UpdateCount()
    {
        var selected = Records.Count(item => item.IsSelected);
        CountText.Text = $"已選 {selected} / {Records.Count} 筆";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatDate(long epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            .ToLocalTime()
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatDuration(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1000);
        var hours = seconds / 3600;
        var minutes = (seconds % 3600) / 60;
        return hours > 0
            ? $"{hours}h {minutes}m"
            : $"{minutes}m";
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0L;
        }

        return value.TryGetInt64(out var parsed) ? parsed : (long)value.GetDouble();
    }

    private static double GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return 0.0;
        }

        return value.GetDouble();
    }
}

public sealed class ExportRecordItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string TrackId { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public long StartTimeMs { get; init; }

    public string DateText { get; init; } = string.Empty;

    public string ModeText { get; init; } = string.Empty;

    public string DistanceText { get; init; } = string.Empty;

    public string DurationText { get; init; } = string.Empty;

    public string DeviceText { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
