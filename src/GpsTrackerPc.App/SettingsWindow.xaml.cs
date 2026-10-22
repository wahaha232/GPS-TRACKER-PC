using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using GpsTrackerPc.App.Infrastructure;
using GpsTrackerPc.Core.Security;

namespace GpsTrackerPc.App;

/// <summary>設定畫面：只處理 GitHub Token 與本機資料夾，不會顯示 Token 內容。</summary>
public partial class SettingsWindow : Window
{
    private readonly TokenStore _tokenStore;

    public SettingsWindow(TokenStore tokenStore)
    {
        InitializeComponent();
        _tokenStore = tokenStore;
        DataPathText.Text = AppPaths.DataDirectory;
        RefreshTokenStatus();
    }

    private void RefreshTokenStatus()
    {
        var hasToken = _tokenStore.HasToken;
        TokenStatusText.Text = hasToken
            ? "狀態：已設定（內容不會顯示）"
            : "狀態：未設定";
        TokenStatusText.Foreground = new SolidColorBrush(
            hasToken ? Color.FromRgb(0x86, 0xEF, 0xAC) : Color.FromRgb(0x94, 0xA3, 0xB8));
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
