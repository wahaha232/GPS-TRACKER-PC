using GpsTrackerPc.Simulation.Model;
using Windows.Devices.Geolocation;

namespace GpsTrackerPc.App.Infrastructure;

internal sealed record CurrentLocationResult(
    bool Success,
    Coordinates? Coordinates,
    string? ErrorMessage);

/// <summary>
/// 使用 Windows 定位服務取得本機現在位置；Windows 會依可用硬體使用 Wi-Fi／基地台定位，
/// 沒有 GPS 的桌機也能取得位置。
/// </summary>
internal static class CurrentLocationProvider
{
    public static async Task<CurrentLocationResult> GetAsync()
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                return Failure(
                    "Windows 定位權限未開啟。請到「設定 → 隱私權 → 位置」允許桌面應用程式使用位置。");
            }

            var locator = new Geolocator
            {
                DesiredAccuracyInMeters = 100,
                ReportInterval = 1000,
            };

            if (locator.LocationStatus is PositionStatus.Disabled or PositionStatus.NotAvailable)
            {
                return Failure("Windows 定位服務目前無法使用，請確認位置服務已開啟。");
            }

            var position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(5),
                timeout: TimeSpan.FromSeconds(15));
            var point = position.Coordinate.Point.Position;
            return new CurrentLocationResult(
                true,
                new Coordinates(point.Latitude, point.Longitude),
                null);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                "Windows 不允許這個程式使用位置。請到「設定 → 隱私權 → 位置」開啟權限。");
        }
        catch (Exception ex)
        {
            return Failure("取得目前位置失敗：" + ex.Message);
        }
    }

    private static CurrentLocationResult Failure(string message) =>
        new(false, null, message);
}
