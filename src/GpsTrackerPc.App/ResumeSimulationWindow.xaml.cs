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

/// <summary>更新重啟後詢問如何處理暫停中的模擬路線。</summary>
public partial class ResumeSimulationWindow : Window
{
    public ResumeSimulationWindow(SimulationResumeSnapshot snapshot)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(snapshot);
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, snapshot.ElapsedSeconds));
        var distance = snapshot.DistanceMeters / 1000.0;
        SummaryText.Text =
            $"暫停位置：{elapsed:hh\\:mm\\:ss}（{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}）\n" +
            $"已走距離：{distance.ToString("0.00", CultureInfo.InvariantCulture)} km・取樣 {snapshot.Samples.Count} 點\n\n" +
            "請選擇要繼續、存檔後結束，或直接放棄這次模擬。";
    }

    public ResumeSimulationChoice Choice { get; private set; } = ResumeSimulationChoice.Discard;

    public static ResumeSimulationChoice ShowChoice(Window owner, SimulationResumeSnapshot snapshot)
    {
        var window = new ResumeSimulationWindow(snapshot) { Owner = owner };
        window.ShowDialog();
        return window.Choice;
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
