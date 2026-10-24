using System.Globalization;
using System.Windows;
using GpsTrackerPc.Core.Gpx;

namespace GpsTrackerPc.App;

public sealed record PendingRecordRow(PendingRecord Record, string Display)
{
    public override string ToString() => Display;
}

/// <summary>管理本機待上傳紀錄：查看失敗原因、立即補傳或刪除。</summary>
public partial class PendingRecordsWindow : Window
{
    private readonly PendingRecordStore _store;
    private readonly GitHubRecordUploader _uploader;
    private readonly Action _onChanged;
    private bool _busy;

    public PendingRecordsWindow(
        PendingRecordStore store,
        GitHubRecordUploader uploader,
        Action onChanged)
    {
        InitializeComponent();
        _store = store;
        _uploader = uploader;
        _onChanged = onChanged;
        Refresh();
    }

    private void Refresh()
    {
        var rows = _store.LoadAll()
            .Select(record => new PendingRecordRow(record, Format(record)))
            .ToList();
        RecordList.ItemsSource = rows;
        CountText.Text = $"待上傳：{rows.Count} 筆";
        UploadAllButton.IsEnabled = !_busy && rows.Count > 0;
        DeleteButton.IsEnabled = !_busy && rows.Count > 0;
        if (rows.Count == 0)
        {
            StatusText.Text = "目前沒有待上傳紀錄。";
        }
    }

    private static string Format(PendingRecord record)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(record.Entry.StartTimeMs).LocalDateTime;
        var distance = record.Entry.DistanceMeters / 1000.0;
        var attempts = record.Attempts > 0
            ? $"・失敗 {record.Attempts} 次：{record.LastError ?? "未知錯誤"}"
            : string.Empty;
        return $"{start:yyyy-MM-dd HH:mm}・{distance.ToString("0.00", CultureInfo.InvariantCulture)} km" +
            $"・{record.Entry.TrackId}{attempts}";
    }

    private async void UploadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "這會把本機所有待上傳紀錄立即公開上傳到 GitHub，且不受每日自動補傳上限限制。\n\n要繼續嗎？",
            "立即補傳",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        StatusText.Text = "上傳中…";
        try
        {
            var uploader = new PendingRecordUploader(_store, _uploader);
            var report = await uploader.UploadAllAsync(int.MaxValue);
            StatusText.Text = report.Failed > 0
                ? $"已上傳 {report.Uploaded} 筆、略過重複 {report.Duplicates} 筆；仍有失敗：{report.LastError}"
                : $"已完成：上傳 {report.Uploaded} 筆、略過重複 {report.Duplicates} 筆。";
            _onChanged();
        }
        catch (Exception ex)
        {
            StatusText.Text = "補傳失敗：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            Refresh();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RecordList.SelectedItem is not PendingRecordRow row)
        {
            StatusText.Text = "請先選取要刪除的紀錄。";
            return;
        }

        var answer = MessageBox.Show(
            this,
            "刪除後這筆紀錄就不會再自動上傳，確定嗎？",
            "刪除待上傳紀錄",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _store.Remove(row.Record.Entry.TrackId);
        _onChanged();
        Refresh();
        StatusText.Text = "已刪除選取的待上傳紀錄。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UploadAllButton.IsEnabled = !busy && RecordList.Items.Count > 0;
        DeleteButton.IsEnabled = !busy && RecordList.Items.Count > 0;
    }
}
