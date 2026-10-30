// SysManager · ConnectionBandwidthSourceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Buffers.Binary;
using System.Net;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// How <see cref="ConnectionBandwidthSource"/> — the Bandwidth Monitor's default, no-admin mode — names a
/// process, which has to match the name precise mode gives the same process.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="BandwidthAggregationTests"/>, which is pure: these read the test process's own
/// name from Windows, a lightweight OS call the unit suite allows.
/// </remarks>
public class ConnectionBandwidthSourceTests
{
    /// <summary>
    /// A process is named the way the rest of the app names it: <c>Process.ProcessName</c>, no extension.
    /// </summary>
    /// <remarks>
    /// This appended ".exe", so connection mode said "chrome.exe" where precise mode — TraceEvent's image name
    /// without the extension — said "chrome", and turning precise rates on relabelled every row. It also made the
    /// kernel's own row "System.exe", a file that does not exist, which then missed both the path search and the
    /// Windows-process list and fell to the generic icon (#2426). The test process names itself, so the expected
    /// value is whatever Windows reports rather than a fixture that could drift from it.
    /// </remarks>
    [Fact]
    public void ResolveName_UsesTheProcessNameTheRestOfTheAppShows()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        var name = ConnectionBandwidthSource.ResolveName(self.Id, new Dictionary<int, string>());

        Assert.Equal(self.ProcessName, name);
        Assert.False(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), name);
    }

    /// <summary>
    /// A PID that is not running gets the app's own wording, which precise mode now uses too.
    /// </summary>
    /// <remarks>
    /// <see cref="int.MaxValue"/> is not a live PID in practice: Windows hands out process ids in multiples of
    /// four, and this one is odd.
    /// </remarks>
    [Fact]
    public void ResolveName_ForAPidThatIsNotRunning_SaysPid()
    {
        Assert.Equal($"PID {int.MaxValue}",
            ConnectionBandwidthSource.ResolveName(int.MaxValue, new Dictionary<int, string>()));
    }

    // ── the TCP tables, as Windows lays them out (#2706) ──
    //
    // The per-app list read the IPv4 table alone, so an app talking over IPv6 was missing, and counted every listening
    // socket and bound UDP endpoint, so the services that always listen were always listed as using the network.

    private const int Established = 5;
    private const int Listen = 2;

    /// <summary>A <c>MIB_TCPROW_OWNER_PID</c>: state, local address and port, remote address and port, PID.</summary>
    private static byte[] V4Row(int state, string remote, int remotePort, int pid)
    {
        var row = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(row, state);
        IPAddress.Parse("192.168.1.20").GetAddressBytes().CopyTo(row, 4);
        Port(54000).CopyTo(row, 8);
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 12);
        Port(remotePort).CopyTo(row, 16);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(20), pid);
        return row;
    }

    /// <summary>
    /// A <c>MIB_TCP6ROW_OWNER_PID</c>: local address, scope and port, remote address, scope and port, state, PID.
    /// </summary>
    private static byte[] V6Row(int state, string remote, int remotePort, int pid)
    {
        var row = new byte[56];
        IPAddress.Parse("2001:db8::20").GetAddressBytes().CopyTo(row, 0);
        Port(54000).CopyTo(row, 20);
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 24);
        Port(remotePort).CopyTo(row, 44);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(48), state);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(52), pid);
        return row;
    }

    // A port in network byte order in the low 16 bits of a DWORD, as the tables keep it.
    private static byte[] Port(int port) => [(byte)(port >> 8), (byte)port, 0, 0];

    private static byte[] Table(params byte[][] rows) => Table(rows.Length, rows);

    private static byte[] Table(int count, params byte[][] rows)
    {
        var table = new byte[4 + rows.Sum(row => row.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(table, count);
        var at = 4;
        foreach (var row in rows)
        {
            row.CopyTo(table, at);
            at += row.Length;
        }
        return table;
    }

    [Fact]
    public void AnIpv4Table_GivesEachConnectionToAnotherMachine_ItsPidAndRemotePort()
    {
        var table = Table(
            V4Row(Established, "93.184.216.34", 443, pid: 100),
            V4Row(Established, "140.82.112.4", 22, pid: 200));

        Assert.Equal([(100, 443), (200, 22)], ConnectionBandwidthSource.ReadTcpTable(table, ipv6: false));
    }

    [Fact]
    public void AnIpv6Table_IsReadInItsOwnLayout()
    {
        var table = Table(
            V6Row(Established, "2606:4700:4700::1111", 443, pid: 300),
            V6Row(Established, "2a00:1450:4001:80b::200e", 8443, pid: 400));

        Assert.Equal([(300, 443), (400, 8443)], ConnectionBandwidthSource.ReadTcpTable(table, ipv6: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AListener_AndAConnectionToThisPc_AreNotNetworkUse(bool ipv6)
    {
        var table = ipv6
            ? Table(
                V6Row(Listen, "::", 0, pid: 900),
                V6Row(Established, "::1", 5000, pid: 901),
                V6Row(Established, "::ffff:127.0.0.1", 5001, pid: 902),
                V6Row(Established, "2606:4700:4700::1111", 443, pid: 903))
            : Table(
                V4Row(Listen, "0.0.0.0", 0, pid: 900),
                V4Row(Established, "127.0.0.1", 5000, pid: 901),
                V4Row(Established, "127.12.0.9", 5001, pid: 902),
                V4Row(Established, "93.184.216.34", 443, pid: 903));

        Assert.Equal([(903, 443)], ConnectionBandwidthSource.ReadTcpTable(table, ipv6));
    }

    [Fact]
    public void AListeningRow_IsLeftOut_EvenWithARemoteAddress()
    {
        // The connections table should hold no listener at all; this does not rely on it.
        var table = Table(V4Row(Listen, "93.184.216.34", 443, pid: 100));

        Assert.Empty(ConnectionBandwidthSource.ReadTcpTable(table, ipv6: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATableShorterThanItsCount_IsReadAsFarAsItGoes(bool ipv6)
    {
        var row = ipv6 ? V6Row(Established, "2606:4700:4700::1111", 443, 300) : V4Row(Established, "1.1.1.1", 443, 300);

        Assert.Equal([(300, 443)], ConnectionBandwidthSource.ReadTcpTable(Table(3, row), ipv6));
        Assert.Empty(ConnectionBandwidthSource.ReadTcpTable([], ipv6));
        Assert.Empty(ConnectionBandwidthSource.ReadTcpTable([1, 0], ipv6));
    }
}
