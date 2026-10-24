using System.Windows;

namespace GpsTrackerPc.App;

/// <summary>取得邀請碼或名稱的簡單輸入視窗；不顯示任何已儲存的秘密。</summary>
public partial class InviteCodeWindow : Window
{
    public InviteCodeWindow(
        string title,
        string prompt,
        string hint,
        bool multiline,
        string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        HintText.Text = hint;
        ValueBox.Text = initialValue;
        ValueBox.AcceptsReturn = multiline;
        ValueBox.MinHeight = multiline ? 110 : 36;
        ValueBox.MaxLength = multiline ? 4096 : 60;
        MinHeight = multiline ? 360 : 250;
    }

    public string Value => ValueBox.Text.Trim();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ValueBox.Focus();
        ValueBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            MessageBox.Show(this, "請先輸入內容。", "內容是空的", MessageBoxButton.OK, MessageBoxImage.Information);
            ValueBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
