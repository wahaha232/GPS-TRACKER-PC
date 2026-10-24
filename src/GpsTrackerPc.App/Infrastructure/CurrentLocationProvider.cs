using GpsTrackerPc.Simulation.Model;
using Windows.Devices.Geolocation;

namespace GpsTrackerPc.App.Infrastructure;

internal sealed record CurrentLocationResult(
    bool Success,
    Coordinates? Coordinates,
    double? AccuracyMeters,
    string? ErrorMessage);

/// <summary>
/// 使用 Windows 定位服務取得目前位置（Windows 會用 Wi-Fi／基地台輔助，
/// 沒有 GPS 的桌機也可以使用）。沒有權限或定位服務關閉時回傳可顯示的錯誤。
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
                    "Windows 定位服務沒有權限。請到「設定 → 隱私權 → 位置」允許桌面程式使用位置。");
            }

            var locator = new Geolocator
            {
                DesiredAccuracyInMeters = 100,
                ReportInterval = 1000,
            };

            if (locator.LocationStatus is PositionStatus.Disabled or PositionStatus.NotAvailable)
            {
                return Failure("Windows 定位服務未開啟或無法使用，請確認定位設定。");
            }

            var position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(1),
                timeout: TimeSpan.FromSeconds(15));
            var point = position.Coordinate.Point.Position;
            var accuracy = position.Coordinate.Accuracy;
            return new CurrentLocationResult(
                true,
                new Coordinates(point.Latitude, point.Longitude),
                double.IsFinite(accuracy) && accuracy > 0 ? accuracy : null,
                null);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(
                "Windows 不允許取得位置。請到「設定 → 隱私權 → 位置」允許桌面程式使用位置。");
        }
        catch (Exception ex)
        {
            return Failure("取得目前位置失敗：" + ex.Message);
        }
    }

    private static CurrentLocationResult Failure(string message) =>
        new(false, null, null, message);
}
