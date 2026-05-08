// SysManager · GatewayHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SysManager.Helpers;

/// <summary>
/// Detects the default IPv4 gateway by scanning active network interfaces
/// and returning the first usable gateway found.
/// </summary>
public static class GatewayHelper
{
    public static string? DetectDefaultGateway()
    {
        var activeNics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        foreach (var nic in activeNics)
        {
            var gateway = nic.GetIPProperties().GatewayAddresses
                .Where(gw => gw?.Address != null
                          && gw.Address.AddressFamily == AddressFamily.InterNetwork
                          && gw.Address.ToString() != "0.0.0.0")
                .FirstOrDefault();

            if (gateway != null)
                return gateway.Address.ToString();
        }
        return null;
    }
}
