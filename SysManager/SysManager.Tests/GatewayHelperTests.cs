// SysManager · GatewayHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SysManager.Helpers;

namespace SysManager.Tests;

public class GatewayHelperTests
{
    [Fact]
    public void DetectDefaultGateway_DoesNotThrow()
    {
        var ex = Record.Exception(() => GatewayHelper.DetectDefaultGateway());
        Assert.Null(ex);
    }

    /// <summary>
    /// Both answers are pinned: no eligible adapter means null, and an eligible one means a parseable IPv4
    /// address drawn from the set those adapters actually offer.
    /// </summary>
    /// <remarks>
    /// This used to return early when the helper answered null, which made "no gateway on this host" — a
    /// container, a runner on an isolated network, a laptop whose Wi-Fi dropped — report a pass having
    /// asserted nothing. The null answer is half the contract, so it is now asserted rather than skipped.
    /// <para>The expected set is derived with the helper's own eligibility filters but flattened over every
    /// gateway of every eligible adapter, making it a superset of what the helper can return. It deliberately
    /// does not re-implement the ranking: asserting membership survives a change to the preference order,
    /// while asserting which one wins would pin that order in two places.</para>
    /// </remarks>
    [Fact]
    public void DetectDefaultGateway_ReturnsNullOrValidIPv4()
    {
        var eligible = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                       && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
            .Where(gw => gw?.Address != null
                      && gw.Address.AddressFamily == AddressFamily.InterNetwork
                      && gw.Address.ToString() != "0.0.0.0")
            .Select(gw => gw.Address.ToString())
            .ToList();

        var gw = GatewayHelper.DetectDefaultGateway();

        if (eligible.Count == 0)
        {
            Assert.Null(gw);
        }
        else
        {
            Assert.NotNull(gw);
            // The addresses themselves stay out of the message: this runs on CI, and a failure there should
            // not publish the runner's network layout to say the membership check failed.
            Assert.True(eligible.Contains(gw), "the helper returned a gateway no eligible adapter offers");
            Assert.True(IPAddress.TryParse(gw, out var ip), "the helper returned something that is not an IP");
            Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.NotEqual("0.0.0.0", gw);
        }
    }
}
