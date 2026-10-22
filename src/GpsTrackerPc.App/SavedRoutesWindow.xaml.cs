using System.Globalization;
using System.Windows;
using GpsTrackerPc.Core.Simulator;
using GpsTrackerPc.Simulation.Routes;

namespace GpsTrackerPc.App;

public partial class SavedRoutesWindow : Window
{
    private readonly RouteLibraryStore _store;

    public SavedRoutesWindow(RouteLibraryStore store)
    {
        InitializeComponent();
        _store = store;
        Refresh();
    }

    public SimulationRoute? SelectedRoute { get; private set; }

    private void Refresh()
    {
        var routes = _store.LoadAll();
        RouteList.ItemsSource = routes
            .Select(route => new SavedRouteRow(
                route,
                $"{route.Name}｜{route.Waypoints.Count} 點｜" +
                $"{route.SpeedKph.ToString("0.#", CultureInfo.InvariantCulture)} km/h｜" +
                DateTimeOffset.FromUnixTimeMilliseconds(route.CreatedAt)
                    .ToLocalTime()
                    .ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)))
            .ToList();
        StatusText.Text = routes.Count == 0
            ? "目前沒有儲存的路線。"
            : $"共 {routes.Count} 條路線。";
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not SavedRouteRow row)
        {
            MessageBox.Show(this, "請先選一條路線。", "尚未選擇", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedRoute = row.Route;
        DialogResult = true;
        Close();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not SavedRouteRow row)
        {
            MessageBox.Show(this, "請先選一條路線。", "尚未選擇", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"確定要刪除「{row.Route.Name}」嗎？",
            "刪除路線",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _store.Delete(row.Route.Id);
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record SavedRouteRow(SimulationRoute Route, string Display)
{
    public override string ToString() => Display;
}
