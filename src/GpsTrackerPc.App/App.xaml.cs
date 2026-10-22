using System.Windows;
using GpsTrackerPc.Core.Updates;

namespace GpsTrackerPc.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 上次更新成功後留下的 .old 在這裡清掉。
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            UpdateInstaller.CleanupOldVersion(executable);
        }

        base.OnStartup(e);
    }
}
