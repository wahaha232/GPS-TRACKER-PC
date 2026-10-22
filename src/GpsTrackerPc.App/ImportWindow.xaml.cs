using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Gpx;
using GpsTrackerPc.Core.Security;
using GpsTrackerPc.Core.Simulator;
using Microsoft.Win32;

namespace GpsTrackerPc.App;

/// <summary>GPX 批次匯入：多選或拖放、顯示摘要、依序上傳，單筆失敗不中斷整批。</summary>
public partial class ImportWindow : Window
{
    private readonly TokenStore _tokenStore;
    private readonly DeviceNameStore _deviceNameStore;
    private readonly GitHubRecordUploader _uploader;
    private CancellationTokenSource? _cts;
    private bool _importing;

    public ImportWindow(
        TokenStore tokenStore,
        DeviceNameStore deviceNameStore,
        GitHubRecordUploader uploader)
    {
        InitializeComponent();
        _tokenStore = tokenStore;
        _deviceNameStore = deviceNameStore;
        _uploader = uploader;
        DataContext = this;

        ModeBox.ItemsSource = ModeCatalog.Options;
        ModeBox.SelectedValue = "UNKNOWN";

        RefreshTokenState();
    }

    public ObservableCollection<ImportRow> Rows { get; } = new();

    public int UploadedCount { get; private set; }

    private void RefreshTokenState()
    {
        var hasToken = _tokenStore.HasToken;
        TokenStatusText.Text = hasToken
            ? "Token：已設定"
            : "尚未設定 GitHub Token，請先回主畫面按「設定」輸入，才能匯入。";
        TokenStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            hasToken ? "AppSuccessText" : "AppDangerText");
        ImportButton.IsEnabled = hasToken && Rows.Any(row => row.IsValid);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_importing)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "選擇 GPX 檔",
            Filter = "GPX 檔 (*.gpx)|*.gpx|所有檔案 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await AddFilesAsync(dialog.FileNames);
        }
    }

    private async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        if (_importing)
        {
            return;
        }

        var paths = filePaths
            .Where(path => path.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            StatusText.Text = "沒有可匯入的 .gpx 檔。";
            return;
        }

        StatusText.Text = "讀取並解析 GPX…";
        foreach (var path in paths)
        {
            if (Rows.Any(row => string.Equals(row.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // 同一個檔案不重複加入清單。
            }

            var sizeCheck = GpxImportFileGuard.Check(path);
            if (!sizeCheck.IsAllowed)
            {
                Rows.Add(new ImportRow
                {
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                    Status = "略過：" + sizeCheck.ErrorMessage,
                });
                continue;
            }

            var row = new ImportRow
            {
                FileName = Path.GetFileName(path),
                FilePath = path,
            };

            try
            {
                var text = await File.ReadAllTextAsync(path);
                var (parsed, build) = await Task.Run(() =>
                {
                    var document = GpxTrackBuilder.Parse(text);
                    var result = GpxTrackBuilder.Build(
                        document,
                        new GpxBuildOptions
                        {
                            Id = Guid.NewGuid().ToString(),
                            Mode = "UNKNOWN",
                        });
                    return (document, result);
                });

                row.TrackId = build.Entry?.TrackId ?? Guid.NewGuid().ToString();
                if (build.Error != null)
                {
                    row.Status = "解析失敗：" + GpxErrors.Get(build.Error);
                }
                else
                {
                    var summary = build.Summary!;
                    row.Parsed = parsed;
                    row.IsValid = true;
                    row.DateText = FormatDate(summary.StartTimeMs);
                    row.DistanceText = (summary.DistanceMeters / 1000.0)
                        .ToString("0.00", CultureInfo.InvariantCulture) + " km";
                    row.DurationText = FormatDuration(summary.DurationMs);
                    row.PointsText = summary.Points.ToString(CultureInfo.InvariantCulture);
                    row.SkippedText = summary.SkippedNoTime > 0
                        ? summary.SkippedNoTime.ToString(CultureInfo.InvariantCulture)
                        : "0";
                    row.Status = "待匯入";
                }
            }
            catch (Exception ex)
            {
                row.Status = "讀取失敗：" + ex.Message;
            }

            Rows.Add(row);
        }

        var valid = Rows.Count(row => row.IsValid);
        StatusText.Text = $"共 {Rows.Count} 個檔案，{valid} 個可匯入。";
        ImportButton.IsEnabled = valid > 0 && _tokenStore.HasToken && !_importing;
    }

    private async void StartImport_Click(object sender, RoutedEventArgs e)
    {
        if (_importing)
        {
            return;
        }

        if (!_tokenStore.HasToken)
        {
            MessageBox.Show(
                this,
                "尚未設定 GitHub Token，請先回主畫面按「設定」輸入。",
                "無法匯入",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var validRows = Rows.Where(row => row.IsValid && row.Parsed != null).ToList();
        if (validRows.Count == 0)
        {
            MessageBox.Show(this, "目前沒有可匯入的檔案。", "沒有檔案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"即將依序匯入 {validRows.Count} 筆紀錄。\n\n" +
            "⚠️ 這些路線會寫入公開的 GitHub 倉庫，任何人都看得到（包含起點與終點）。\n\n" +
            "確定要匯入嗎？",
            "確認匯入",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var mode = ModeBox.SelectedValue as string ?? "UNKNOWN";
        var deviceName = _deviceNameStore.Load();
        var candidates = validRows
            .Select(row =>
            {
                var build = GpxTrackBuilder.Build(
                    row.Parsed,
                    new GpxBuildOptions
                    {
                        Id = row.TrackId,
                        Mode = mode,
                    });
                RecordDeviceNaming.ApplyDeviceName(build, deviceName);
                return new GpxImportCandidate(row.FileName, build);
            })
            .ToList();

        _importing = true;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        Progress.Minimum = 0;
        Progress.Maximum = candidates.Count;
        Progress.Value = 0;
        StatusText.Text = "匯入中…";

        var progress = new Progress<GpxImportProgress>(item =>
        {
            if (item.Index >= 0 && item.Index < validRows.Count)
            {
                ApplyResult(validRows[item.Index], item.Result);
                Progress.Value = item.Index + 1;
                StatusText.Text = $"匯入中… {item.Index + 1} / {validRows.Count}（{validRows[item.Index].FileName}）";
            }
        });

        try
        {
            var results = await new GpxImportBatch(_uploader)
                .ImportAsync(candidates, progress, _cts.Token);

            var uploaded = 0;
            var duplicate = 0;
            var failed = 0;
            var parseError = 0;
            for (var index = 0; index < results.Count && index < validRows.Count; index++)
            {
                var result = results[index];
                ApplyResult(validRows[index], result);
                switch (result.Status)
                {
                    case GpxImportItemStatus.Uploaded:
                        uploaded++;
                        break;
                    case GpxImportItemStatus.Duplicate:
                        duplicate++;
                        break;
                    case GpxImportItemStatus.ParseError:
                        parseError++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            UploadedCount = uploaded;
            StatusText.Text =
                $"完成：成功 {uploaded}、重複 {duplicate}、失敗 {failed}" +
                (parseError > 0 ? $"、解析失敗 {parseError}" : string.Empty) + "。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消匯入。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "匯入失敗：" + ex.Message;
        }
        finally
        {
            _importing = false;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private static void ApplyResult(ImportRow row, GpxImportItemResult result)
    {
        row.IsValid = false;
        row.Status = result.Status switch
        {
            GpxImportItemStatus.Uploaded => "已匯入",
            GpxImportItemStatus.Duplicate => "已跳過：重複",
            GpxImportItemStatus.ParseError => "解析失敗：" + result.Message,
            _ => "失敗：" + result.Message,
        };
    }

    private void SetBusy(bool busy)
    {
        AddFilesButton.IsEnabled = !busy;
        ModeBox.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        ImportButton.Content = busy ? "匯入中…" : "開始匯入";
        ImportButton.IsEnabled = !busy && _tokenStore.HasToken && Rows.Any(row => row.IsValid);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            await AddFilesAsync(files);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_importing)
        {
            MessageBox.Show(this, "匯入進行中，請等這一輪結束。", "匯入中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_importing)
        {
            e.Cancel = true;
            MessageBox.Show(this, "匯入進行中，請等這一輪結束。", "匯入中", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

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
}

public sealed class ImportRow : INotifyPropertyChanged
{
    private string _status = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string TrackId { get; set; } = string.Empty;

    public string DateText { get; set; } = string.Empty;

    public string DistanceText { get; set; } = string.Empty;

    public string DurationText { get; set; } = string.Empty;

    public string PointsText { get; set; } = string.Empty;

    public string SkippedText { get; set; } = string.Empty;

    public bool IsValid { get; set; }

    public GpxDocument? Parsed { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
