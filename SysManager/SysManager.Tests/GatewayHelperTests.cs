// SysManager · GatewayHelperTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

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

    [Fact]
    public void DetectDefaultGateway_ReturnsNullOrValidIPv4()
    {
        var gw = GatewayHelper.DetectDefaultGateway();
        if (gw == null) return;

        Assert.True(System.Net.IPAddress.TryParse(gw, out var ip), $"'{gw}' is not a valid IP");
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
        Assert.NotEqual("0.0.0.0", gw);
    }
}
