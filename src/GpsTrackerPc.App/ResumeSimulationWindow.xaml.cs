using System.Globalization;
using System.Windows;
using GpsTrackerPc.Core.Simulator;

namespace GpsTrackerPc.App;

public enum ResumeSimulationChoice
{
    Continue,
    SaveAndEnd,
    Discard,
}

public sealed record ResumePromptResult(ResumeSimulationChoice Choice, bool Remember);

/// <summary>更新重啟後詢問如何處理暫停中的模擬路線。</summary>
public partial class ResumeSimulationWindow : Window
{
    public ResumeSimulationWindow(SimulationResumeSnapshot snapshot)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(snapshot);
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, snapshot.ElapsedSeconds));
        var distance = snapshot.DistanceMeters / 1000.0;
        var title = snapshot.Samples.Count >= 2
            ? "上次重新啟動前，模擬路線已暫停"
            : "上次重新啟動前有未儲存的路線草稿";
        SummaryText.Text = snapshot.Samples.Count >= 2
            ? $"暫停位置：{elapsed:hh\\:mm\\:ss}\n" +
              $"已走距離：{distance.ToString("0.00", CultureInfo.InvariantCulture)} km・取樣 {snapshot.Samples.Count} 點\n\n" +
              "請選擇要繼續、存檔後結束，或直接放棄這次模擬。"
            : $"路線：{snapshot.RouteName ?? "未命名"}\n" +
              "這條路線還沒存檔，也還沒有可上傳的取樣點。\n\n" +
              "請選擇繼續編輯、存到路線庫後結束，或直接放棄。";
        Title = title;
    }

    public ResumeSimulationChoice Choice { get; private set; } = ResumeSimulationChoice.Discard;

    public bool RememberChoice => RememberChoiceCheck.IsChecked == true;

    public static ResumePromptResult ShowChoice(Window owner, SimulationResumeSnapshot snapshot)
    {
        var window = new ResumeSimulationWindow(snapshot) { Owner = owner };
        window.ShowDialog();
        return new ResumePromptResult(window.Choice, window.RememberChoice);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Choice = ResumeSimulationChoice.Continue;
        Close();
    }

    private void SaveAndEnd_Click(object sender, RoutedEventArgs e)
    {
        Choice = ResumeSimulationChoice.SaveAndEnd;
        Close();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = ResumeSimulationChoice.Discard;
        Close();
    }
}
