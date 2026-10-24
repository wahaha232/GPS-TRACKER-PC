using System.Net.NetworkInformation;

namespace GpsTrackerPc.App.Infrastructure;

/// <summary>
/// Windows 沒有簡單 API 可判斷 Wi-Fi 是否為計量網路，這裡用「行動寬頻」介面當作計量連線；
/// 有線與一般 Wi-Fi 視為非計量。判斷失敗時回傳 false，不阻擋背景下載。
/// </summary>
public static class NetworkCostHeuristic
{
    public static bool IsLikelyMetered()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .Where(network => network.NetworkInterfaceType is not (
                    NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel))
                .Where(network =>
                {
                    try
                    {
                        return network.GetIPProperties().GatewayAddresses.Count > 0;
                    }
                    catch (NetworkInformationException)
                    {
                        return false;
                    }
                })
                .ToList();

            return active.Count > 0 && active.All(network =>
                network.NetworkInterfaceType is
                    NetworkInterfaceType.Wman or
                    NetworkInterfaceType.Wwanpp or
                    NetworkInterfaceType.Wwanpp2);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
