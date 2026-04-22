// SysManager · GatewayHelper
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
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
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var gateways = nic.GetIPProperties().GatewayAddresses;
            foreach (var gw in gateways)
            {
                if (gw?.Address == null) continue;
                if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (gw.Address.ToString() == "0.0.0.0") continue;
                return gw.Address.ToString();
            }
        }
        return null;
    }
}
