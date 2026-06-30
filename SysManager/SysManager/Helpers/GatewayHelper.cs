// SysManager · GatewayHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SysManager.Helpers;

/// <summary>
/// Detects the default IPv4 gateway by scanning active network interfaces. Tunnel
/// (VPN) adapters are excluded and physical adapter types (Ethernet/Wi-Fi) are
/// preferred over virtual ones, so a VPN or virtual adapter's gateway isn't picked
/// over the real physical default route just because it enumerates first.
/// </summary>
public static class GatewayHelper
{
    public static string? DetectDefaultGateway()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(nic => new
            {
                Gateway = nic.GetIPProperties().GatewayAddresses
                    .Where(gw => gw?.Address != null
                              && gw.Address.AddressFamily == AddressFamily.InterNetwork
                              && gw.Address.ToString() != "0.0.0.0")
                    .Select(gw => gw!.Address.ToString())
                    .FirstOrDefault(),
                Rank = TypeRank(nic.NetworkInterfaceType),
                Speed = nic.Speed
            })
            .Where(x => x.Gateway != null)
            .OrderBy(x => x.Rank)            // physical types first
            .ThenByDescending(x => x.Speed); // then the fastest link

        return candidates.FirstOrDefault()?.Gateway;
    }

    // Lower rank = more likely to be the real default route.
    private static int TypeRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        NetworkInterfaceType.Ppp => 2,
        _ => 3 // virtual / unknown
    };
}
