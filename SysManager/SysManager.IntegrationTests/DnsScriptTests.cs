// SysManager · DnsScriptTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using SysManager.Services;

namespace SysManager.IntegrationTests;

/// <summary>
/// <c>DnsService.CurrentDnsScript</c> and <c>DnsService.ActiveInterfaceIndexScript</c> run in real Windows PowerShell,
/// with <c>Get-NetAdapter</c>, <c>Get-DnsClientServerAddress</c> and <c>Find-NetRoute</c> shadowed by functions so
/// nothing on this machine is read or changed.
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
        WindowsPowerShellScript.StubsInEffect("Get-NetAdapter", "Get-DnsClientServerAddress", "Find-NetRoute");

    // No route to the internet, so the selector falls back to the adapter order. Shadowed in every test: the real
    // cmdlet reads this machine's routes, and an interface number of its could match a stub's.
    private const string NoRoute = "function Find-NetRoute { [CmdletBinding()] param($RemoteIPAddress) } ; ";

    /// <summary>
    /// <c>Find-NetRoute</c> answering for one family: the interface of the route to <paramref name="probeFamily"/>'s
    /// public address, and nothing for the other family.
    /// </summary>
    private static string RouteVia(int interfaceIndex, string probeFamily = "IPv4") =>
        "function Find-NetRoute { [CmdletBinding()] param($RemoteIPAddress) " +
        $"$isV6 = [string]$RemoteIPAddress -like '*:*'; if ($isV6 -eq ('{probeFamily}' -eq 'IPv6')) " +
        $"{{ [PSCustomObject]@{{ InterfaceIndex = {interfaceIndex} }} }} }} ; ";

    // A laptop on a dock: its built-in Wi-Fi, created first, is still connected, and the dock's Ethernet, created
    // later with a higher interface number, carries the default route.
    private const string WiFiAndDockEthernetUp =
        "function Get-NetAdapter { [CmdletBinding()] param() " +
        "[PSCustomObject]@{ Name = 'Wi-Fi'; Status = 'Up'; Virtual = $false; ifIndex = 5 }; " +
        "[PSCustomObject]@{ Name = 'Ethernet 3'; Status = 'Up'; Virtual = $false; ifIndex = 17 } } ; ";

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
            OneAdapterUp + ServerReadFails + NoRoute + Guard + DnsService.CurrentDnsScript + After);

        Assert.DoesNotContain("Automatic (DHCP)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WhenTheAdapterReadFails_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterReadFails + ServersAre("") + NoRoute + Guard + DnsService.CurrentDnsScript + After);

        Assert.DoesNotContain("No active adapter", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithNoServersSet_SaysAutomatic()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("") + NoRoute + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("Automatic (DHCP)", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithServersSet_ListsThem()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("'1.1.1.1', '1.0.0.1'") + NoRoute + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("1.1.1.1, 1.0.0.1", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentDns_WithNoAdapterUp_SaysSo()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            NoAdapterUp + ServersAre("") + NoRoute + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("No active adapter", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceIndex_WhenTheAdapterReadFails_StopsTheScript()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterReadFails + ServersAre("") + NoRoute + Guard + DnsService.ActiveInterfaceIndexScript + After);

        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceIndex_WithAnAdapterUp_PrintsIt()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            OneAdapterUp + ServersAre("") + NoRoute + Guard + DnsService.ActiveInterfaceIndexScript + After);

        Assert.Equal(["7", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------- the adapter Windows sends its traffic through (#2879) ----------

    [Fact]
    public async Task InterfaceIndex_IsTheAdapterOfTheBestRoute_NotTheLowestNumber()
    {
        // The selector took the lowest interface number, the Wi-Fi, so a family preset was written to an adapter that
        // carried no traffic while names went on resolving through the Ethernet's servers.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            WiFiAndDockEthernetUp + ServersAre("") + RouteVia(17) + Guard + DnsService.ActiveInterfaceIndexScript
            + After);

        Assert.Equal(["17", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task InterfaceIndex_OnAnIpv6OnlyRoute_IsThatRoutesAdapter()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            WiFiAndDockEthernetUp + ServersAre("") + RouteVia(17, "IPv6") + Guard
            + DnsService.ActiveInterfaceIndexScript + After);

        Assert.Equal(["17", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task InterfaceIndex_WithNoRoute_IsTheFirstAdapterUp()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            WiFiAndDockEthernetUp + ServersAre("") + NoRoute + Guard + DnsService.ActiveInterfaceIndexScript + After);

        Assert.Equal(["5", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task InterfaceIndex_WhenTheRoutesAdapterIsNotUp_IsTheFirstAdapterUp()
    {
        // A route can name an interface that is not one of the adapters that are up, such as a tunnel that has no
        // network adapter; the change is not sent to an interface the tab cannot describe.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            WiFiAndDockEthernetUp + ServersAre("") + RouteVia(40) + Guard + DnsService.ActiveInterfaceIndexScript
            + After);

        Assert.Equal(["5", "__SM_AFTER_READ__"], output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task CurrentDns_ReadsTheAdapterOfTheBestRoute()
    {
        // The readout asks the same adapter the change goes to, so it shows what Windows resolves names with.
        const string ServersOfEach =
            "function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex, $AddressFamily) " +
            "$servers = if ($InterfaceIndex -eq 17) { @('9.9.9.9') } else { @('1.1.1.3') }; " +
            "[PSCustomObject]@{ InterfaceIndex = $InterfaceIndex; ServerAddresses = $servers } } ; ";

        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            WiFiAndDockEthernetUp + ServersOfEach + RouteVia(17) + Guard + DnsService.CurrentDnsScript + After);

        Assert.Contains("9.9.9.9", output, StringComparison.Ordinal);
        Assert.DoesNotContain("1.1.1.3", output, StringComparison.Ordinal);
    }

    // ---------- an adapter with IPv6 turned off (#2709) ----------

    /// <summary>
    /// The active adapter at interface 7, under <paramref name="interfaceGuid"/>, with a DNS client entry for each
    /// address family in <paramref name="families"/> (2 is IPv4, 23 is IPv6).
    /// </summary>
    /// <remarks>
    /// <c>Get-DnsClientServerAddress</c> answers as Windows does for an adapter with IPv6 turned off: an entry for each
    /// family it has, and "No matching MSFT_DNSClientServerAddress objects found" for one it has not.
    /// </remarks>
    private static string AdapterWith(Guid interfaceGuid, params int[] families) =>
        "function Get-NetAdapter { [CmdletBinding()] param($InterfaceIndex, [switch] $IncludeHidden) " +
        "[PSCustomObject]@{ Name = 'Ethernet'; Status = 'Up'; Virtual = $false; ifIndex = 7; " +
        $"InterfaceGuid = [Guid]'{interfaceGuid:D}' }} }} ; " +
        "function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex, $AddressFamily) " +
        "$entries = @(" + string.Join(", ", families.Select(family =>
            $"[PSCustomObject]@{{ InterfaceIndex = 7; AddressFamily = [uint16]{family}; ServerAddresses = @() }}")) +
        "); $codes = @{ IPv4 = 2; IPv6 = 23 }; " +
        "$found = @($entries | Where-Object { -not $AddressFamily -or $_.AddressFamily -eq $codes[[string]$AddressFamily] }); " +
        "if ($found.Count -eq 0) { Write-Error -Message 'No matching MSFT_DNSClientServerAddress objects found' " +
        "-Category ObjectNotFound } else { $found } } ; ";

    /// <summary>
    /// A network interface this machine has a key for under both Tcpip and Tcpip6, which the capture reads, and only
    /// reads, to tell automatic DNS from static.
    /// </summary>
    private static Guid AnInterfaceKeyOfThisMachine()
    {
        static string Interfaces(string service) => $@"SYSTEM\CurrentControlSet\Services\{service}\Parameters\Interfaces";
        using var v4 = Registry.LocalMachine.OpenSubKey(Interfaces("Tcpip"));
        using var v6 = Registry.LocalMachine.OpenSubKey(Interfaces("Tcpip6"));
        var guid = (v4?.GetSubKeyNames() ?? [])
            .Intersect(v6?.GetSubKeyNames() ?? [], StringComparer.OrdinalIgnoreCase)
            .Select(name => Guid.TryParse(name, out var parsed) ? parsed : Guid.Empty)
            .FirstOrDefault(parsed => parsed != Guid.Empty);
        if (guid == Guid.Empty)
            Assert.Skip("This machine has no network interface key under both Tcpip and Tcpip6 for the capture to read.");
        return guid;
    }

    private static string[] Lines(string output) =>
        output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task FamiliesProbe_WhenTheReadFails_StopsTheScript()
    {
        // A failed read that carried on would leave both families looking absent, and what a snapshot leaves out,
        // Undo does not put back.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            ServerReadFails + WindowsPowerShellScript.StubsInEffect("Get-DnsClientServerAddress")
            + "$ErrorActionPreference = 'Stop'; $dnsIfIndex = 7; " + DnsService.FamiliesPresentScript + After);

        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FamiliesProbe_OnAnAdapterWithIpv6TurnedOff_FindsIpv4Alone()
    {
        // The positive control for the test above, in the same PowerShell.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterWith(Guid.Empty, 2) + WindowsPowerShellScript.StubsInEffect("Get-DnsClientServerAddress")
            + "$ErrorActionPreference = 'Stop'; $dnsIfIndex = 7; " + DnsService.FamiliesPresentScript
            + "; \"IPv4=$($familiesPresent.IPv4) IPv6=$($familiesPresent.IPv6)\"" + After);

        Assert.Contains("IPv4=True IPv6=False", output, StringComparison.Ordinal);
        Assert.Contains("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_OnAnAdapterWithIpv6TurnedOff_RecordsIpv6AsAbsent_AndCompletes()
    {
        // Reading IPv6 alone stopped the script, so every DNS change on the tab failed at its snapshot.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterWith(AnInterfaceKeyOfThisMachine(), 2) + NoRoute + Guard + DnsService.CaptureScript + After);

        var lines = Lines(output);
        Assert.Contains("COMPLETE=IPv4", lines);
        Assert.Contains("SOURCE_IPv6=Absent", lines);
        Assert.Contains("COMPLETE=IPv6", lines);
        Assert.Contains("IDENTITY=Verified", lines);
        Assert.Contains("__SM_AFTER_READ__", lines);
    }

    [Fact]
    public async Task Capture_OnAnAdapterWithBothFamilies_ReadsIpv6()
    {
        // The positive control: an adapter that has IPv6 has it read, never called absent.
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterWith(AnInterfaceKeyOfThisMachine(), 2, 23) + NoRoute + Guard + DnsService.CaptureScript + After);

        var lines = Lines(output);
        Assert.Contains(lines, line => line.StartsWith("SOURCE_IPv6=", StringComparison.Ordinal));
        Assert.DoesNotContain("SOURCE_IPv6=Absent", lines);
        Assert.Contains("COMPLETE=IPv6", lines);
        Assert.Contains("__SM_AFTER_READ__", lines);
    }

    [Fact]
    public async Task Capture_WhenTheDnsReadFails_StopsTheScript_RatherThanCallingIpv6Absent()
    {
        var (_, output, _) = await WindowsPowerShellScript.RunAsync(
            AdapterWith(AnInterfaceKeyOfThisMachine(), 2) + ServerReadFails + NoRoute + Guard + DnsService.CaptureScript
            + After);

        Assert.DoesNotContain("SOURCE_IPv6=Absent", output, StringComparison.Ordinal);
        Assert.DoesNotContain("__SM_AFTER_READ__", output, StringComparison.Ordinal);
    }
}
