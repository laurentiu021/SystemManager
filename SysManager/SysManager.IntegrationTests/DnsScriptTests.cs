// SysManager · DnsScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>DnsService.CurrentDnsScript</c> and <c>DnsService.ActiveInterfaceIndexScript</c> run in real Windows PowerShell,
/// with <c>Get-NetAdapter</c> and <c>Get-DnsClientServerAddress</c> shadowed by functions so nothing on this machine is
/// read or changed.
/// </summary>
/// <remarks>
/// What the scripts must tell apart only shows in PowerShell itself: both cmdlets report a failure as a non-terminating
/// error, which does not stop a script unless the script makes it. The read used to carry on past one, so a failed
/// server read came out as "Automatic (DHCP)" and a failed adapter read as "No active adapter" (#2504). A failed read
/// has to stop the script, so the runner throws and the tab says the DNS is unavailable.
/// <para>The answering stubs are the positive controls: a script that stopped on everything would pass every failure
/// case.</para>
/// </remarks>
public class DnsScriptTests
{
    private static readonly string Guard =
        WindowsPowerShellScript.StubsInEffect("Get-NetAdapter", "Get-DnsClientServerAddress");

    // Printed after the script, so it shows whether a failed read STOPPED it. Stopping is what makes the runner throw.
    private const string After = "; '__SM_AFTER_READ__'";

    // Shaped like the fields the adapter selector reads.
    private const string OneAdapterUp =
        "function Get-NetAdapter { [CmdletBinding()] param() " +
        "[PSCustomObject]@{ Name = 'Ethernet'; Status = 'Up'; Virtual = $false; ifIndex = 7 } } ; ";

    private const string NoAdapterUp =
        "function Get-NetAdapter { [CmdletBinding()] param() " +
        "[PSCustomObject]@{ Name = 'Ethernet'; Status = 'Disconnected'; Virtual = $false; ifIndex = 7 } } ; ";

    private const string AdapterReadFails =
        "function Get-NetAdapter { [CmdletBinding()] param() " +
        "Write-Error -Message 'The network adapter provider did not answer.' -Category ResourceUnavailable } ; ";

    private const string ServerReadFails =
        "function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex, $AddressFamily) " +
        "Write-Error -Message 'The DNS client did not answer.' -Category ResourceUnavailable } ; ";

    private static string ServersAre(string addresses) =>
        "function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex, $AddressFamily) " +
        $"[PSCustomObject]@{{ InterfaceIndex = $InterfaceIndex; ServerAddresses = @({addresses}) }} }} ; ";

    [Fact]
    public async Task CurrentDns_WhenTheServerReadFails_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServerReadFails + Guard + DnsService.CurrentDnsScript + After);

        Assert.DoesNotContain("Automatic (DHCP)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WhenTheAdapterReadFails_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterReadFails + ServersAre("") + Guard + DnsService.CurrentDnsScript + After);

        Assert.DoesNotContain("No active adapter", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithNoServersSet_SaysAutomatic()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("") + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("Automatic (DHCP)", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithServersSet_ListsThem()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("'1.1.1.1', '1.0.0.1'") + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("1.1.1.1, 1.0.0.1", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithNoAdapterUp_SaysSo()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            NoAdapterUp + ServersAre("") + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("No active adapter", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceIndex_WhenTheAdapterReadFails_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterReadFails + ServersAre("") + Guard + DnsService.ActiveInterfaceIndexScript + After);

        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceIndex_WithAnAdapterUp_PrintsIt()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("") + Guard + DnsService.ActiveInterfaceIndexScript + After);

        Assert.Equal(["7", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }
}
