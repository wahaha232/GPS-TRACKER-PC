using System.Windows;
using System.Windows.Media;

namespace GpsTrackerPc.App;

/// <summary>顯示好友／群組邀請碼，讓使用者自行傳給朋友；等同分享權杖，請勿公開張貼。</summary>
public partial class InviteDisplayWindow : Window
{
    public InviteDisplayWindow(string title, string description, string code)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DescriptionText.Text = description;
        CodeBox.Text = code;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeBox.Text);
            StatusText.Text = "已複製到剪貼簿。";
            StatusText.Foreground = (Brush)FindResource("AppSuccessText");
        }
        catch (Exception ex)
        {
            StatusText.Text = "無法複製：" + ex.Message;
            StatusText.Foreground = (Brush)FindResource("AppDangerText");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
