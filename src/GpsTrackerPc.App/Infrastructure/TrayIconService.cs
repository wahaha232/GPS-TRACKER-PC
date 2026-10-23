using System.Drawing;
using System.Windows.Forms;

namespace GpsTrackerPc.App.Infrastructure;

/// <summary>
/// 工具列圖示區（系統匣）圖示與選單；只在設定的「縮小到系統匣」開啟時顯示。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly bool _ownsIcon;

    public TrayIconService(Action showMainWindow, Action showSimulatorWindow, Action exit)
    {
        (_icon, _ownsIcon) = LoadIcon();
        var menu = new ContextMenuStrip();
        var mainItem = new ToolStripMenuItem("顯示主視窗");
        mainItem.Click += (_, _) => showMainWindow();
        var simulatorItem = new ToolStripMenuItem("顯示模擬路線");
        simulatorItem.Click += (_, _) => showSimulatorWindow();
        var exitItem = new ToolStripMenuItem("結束");
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(mainItem);
        menu.Items.Add(simulatorItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "GPS Tracker PC",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _notifyIcon.DoubleClick += (_, _) => showMainWindow();
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void ShowBalloon(string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = "GPS Tracker PC";
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception)
        {
            // 系統匣提示失敗不影響程式。
        }
    }

    private static (Icon Icon, bool OwnsIcon) LoadIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var icon = Icon.ExtractAssociatedIcon(executable);
                if (icon != null)
                {
                    return (icon, true);
                }
            }
        }
        catch (Exception)
        {
            // 取不到執行檔圖示時用系統預設圖示。
        }

        return (SystemIcons.Application, false);
    }

    public void Dispose()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }
        catch (Exception)
        {
            // 結束時圖示可能已經被系統移除。
        }

        if (_ownsIcon)
        {
            // NotifyIcon 不會釋放呼叫端提供的 Icon；ExtractAssociatedIcon 取得的 HICON 要在這裡放掉。
            _icon.Dispose();
        }
    }
}
