// SysManager · DnsServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using NSubstitute;
using SysManager.Services;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DnsService"/> (audit finding tests #5).
/// <para>
/// <c>SetDnsAsync</c> validates both addresses with <c>IPAddress.TryParse</c>
/// and throws <see cref="ArgumentException"/> before any PowerShell runs — the
/// guard that stops a malformed or injected value from reaching
/// <c>Set-DnsClientServerAddress</c>. The interface index (an integer) is used
/// rather than the adapter name to avoid command injection. These tests pin the
/// validation guard and assert the exact script on the happy path via the
/// <see cref="IPowerShellRunner"/> seam, so no live DNS state is touched.
/// </para>
/// </summary>
public class DnsServiceTests
{
    private const string TestInterfaceGuid = "11111111-2222-3333-4444-555555555555";

    private static Collection<PSObject> Result(string value) =>
        new() { PSObject.AsPSObject(value) };

    private static Collection<PSObject> CaptureResult(int ifIndex, params string[] addresses)
    {
        var v4Source = addresses.Any(static value => value.StartsWith("IPv4=", StringComparison.Ordinal))
            ? DnsService.DnsConfigurationSource.Static
            : DnsService.DnsConfigurationSource.Automatic;
        var v6Source = addresses.Any(static value => value.StartsWith("IPv6=", StringComparison.Ordinal))
            ? DnsService.DnsConfigurationSource.Static
            : DnsService.DnsConfigurationSource.Automatic;
        return CaptureResult(ifIndex, v4Source, v6Source, addresses);
    }

    private static Collection<PSObject> CaptureResult(
        int ifIndex,
        DnsService.DnsConfigurationSource v4Source,
        DnsService.DnsConfigurationSource v6Source,
        params string[] addresses)
    {
        string[] values =
        [
            $"IFINDEX={ifIndex}", $"IFGUID={TestInterfaceGuid}",
            $"SOURCE_IPv4={v4Source}", $"SOURCE_IPv6={v6Source}",
            .. addresses,
            "COMPLETE=IPv4", "COMPLETE=IPv6", "IDENTITY=Verified"
        ];
        return new Collection<PSObject>(values.Select(PSObject.AsPSObject).ToList());
    }

    // ---------- IP-validation guard (#5) ----------

    public static IEnumerable<object[]> InvalidAddresses()
    {
        yield return ["not-an-ip"];
        yield return ["8.8.8.8; calc.exe"];   // injection attempt
        yield return ["8.8.8.8\")"];           // quote/paren break-out
        yield return ["999.999.999.999"];      // out-of-range octets
        yield return [""];
        yield return ["   "];
    }

    [Theory]
    [MemberData(nameof(InvalidAddresses))]
    public async Task SetDnsAsync_InvalidPrimary_ThrowsArgumentException_AndNeverRunsScript(string badPrimary)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetDnsAsync(badPrimary, "8.8.4.4"));

        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Theory]
    [MemberData(nameof(InvalidAddresses))]
    public async Task SetDnsAsync_InvalidSecondary_ThrowsArgumentException(string badSecondary)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetDnsAsync("8.8.8.8", badSecondary));
    }

    // ---------- happy path: exact Set script via the seam ----------

    [Fact]
    public async Task SetDnsAsync_ValidAddresses_RunsSetScriptWithThoseAddresses()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        // First RunAsync resolves the active interface index; subsequent calls
        // (the Set script) can return anything — the result is not consumed.
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("5"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync("1.1.1.1", "1.0.0.1");

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Set-DnsClientServerAddress") &&
                s.Contains("-InterfaceIndex 5") &&
                s.Contains("1.1.1.1") &&
                s.Contains("1.0.0.1")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetToDhcpAsync_RunsResetScript()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("3"));
        using var svc = new DnsService(runner);

        await svc.ResetToDhcpAsync();

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Set-DnsClientServerAddress") &&
                s.Contains("-InterfaceIndex 3") &&
                s.Contains("-ResetServerAddresses")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    // ---------- fail-loud: cmdlet failures must be made terminating (regression) ----------

    [Fact]
    public async Task SetDnsAsync_MutationScript_RequestsTerminatingErrors()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("5"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync("1.1.1.1", "1.0.0.1");

        // The Set call must request terminating errors so a non-terminating cmdlet
        // failure surfaces instead of being reported as a false success.
        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Set-DnsClientServerAddress") &&
                s.Contains("-ErrorAction Stop") &&
                s.Contains("$ErrorActionPreference = 'Stop'")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetToDhcpAsync_MutationScript_RequestsTerminatingErrors()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("3"));
        using var svc = new DnsService(runner);

        await svc.ResetToDhcpAsync();

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("-ResetServerAddresses") && s.Contains("-ErrorAction Stop")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetActiveInterfaceIndex_UsesSameOrderedNonVirtualSelectorAsDisplay()
    {
        // Read and mutate must select the active adapter by the SAME rule (Up,
        // non-virtual, ordered by ifIndex) so display/capture and set target the
        // same NIC on a multi-adapter machine. The Set path's index resolution must
        // therefore carry the ordered, non-virtual selector.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync("9.9.9.9", "149.112.112.112");

        await runner.Received().RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Virtual -eq $false") &&
                s.Contains("Sort-Object -Property ifIndex")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCurrentDnsAsync_ReturnsFirstResultLine()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("8.8.8.8, 8.8.4.4"));
        using var svc = new DnsService(runner);

        var current = await svc.GetCurrentDnsAsync();

        Assert.Equal("8.8.8.8, 8.8.4.4", current);
    }

    [Fact]
    public async Task GetCurrentDnsAsync_NoResults_ReturnsUnknown()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);

        var current = await svc.GetCurrentDnsAsync();

        Assert.Equal("Unknown", current);
    }

    [Fact]
    public async Task GetCurrentDnsAsync_PowerShellHostUnavailable_ReturnsUnavailable()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<Collection<PSObject>>>(
                _ => throw new RuntimeException(
                    "Windows PowerShell 5.1 is unavailable."));
        using var svc = new DnsService(runner);

        var current = await svc.GetCurrentDnsAsync();

        Assert.Equal("Unavailable", current);
    }

    // ---------- presets (pure) ----------

    [Fact]
    public void GetPresets_IncludesGoogleCloudflareQuad9OpenDnsAndAutomatic()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());

        var presets = svc.GetPresets();
        var names = presets.Select(p => p.Name).ToList();

        Assert.Contains("Google", names);
        Assert.Contains("Cloudflare", names);
        Assert.Contains("Quad9", names);
        Assert.Contains("OpenDNS", names);
        Assert.Contains(names, n => n.Contains("Automatic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPresets_GoogleHasExpectedAddresses()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());

        var google = svc.GetPresets().First(p => p.Name == "Google");

        Assert.Equal("8.8.8.8", google.Primary);
        Assert.Equal("8.8.4.4", google.Secondary);
    }

    // ---------- snapshot / restore (reversibility, #3) ----------

    [Fact]
    public async Task CaptureCurrentServersAsync_ReturnsParsedAddresses()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(5, "IPv4=8.8.8.8", "IPv4=8.8.4.4"));
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Equal(["8.8.8.8", "8.8.4.4"], snapshot);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_CapturesBothFamilies()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(
                  5, "IPv4=8.8.8.8", "IPv6=2001:4860:4860::8888"));
        using var svc = new DnsService(runner);

        var snap = await svc.CaptureSnapshotAsync();

        Assert.Equal(["8.8.8.8"], snap.V4);
        Assert.Equal(["2001:4860:4860::8888"], snap.V6);
        Assert.Equal(DnsService.DnsConfigurationSource.Static, snap.V4Source);
        Assert.Equal(DnsService.DnsConfigurationSource.Static, snap.V6Source);
    }

    [Fact]
    public async Task CaptureCurrentServersAsync_FiltersUnrelatedNoise()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(5, "IPv4=1.1.1.1", "", "NOISE=garbage"));
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Equal(["1.1.1.1"], snapshot);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_MalformedTaggedAddress_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(
                  5,
                  DnsService.DnsConfigurationSource.Static,
                  DnsService.DnsConfigurationSource.Automatic,
                  "IPv4=1.1.1.1",
                  "IPv4=garbage"));
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Fact]
    public async Task CaptureCurrentServersAsync_Dhcp_ReturnsEmpty()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(
                  5,
                  DnsService.DnsConfigurationSource.Automatic,
                  DnsService.DnsConfigurationSource.Automatic,
                  "IPv4=192.0.2.53"));
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_DhcpRetainsEffectiveAddressAsAutomatic()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(
                  5,
                  DnsService.DnsConfigurationSource.Automatic,
                  DnsService.DnsConfigurationSource.Automatic,
                  "IPv4=192.0.2.53"));
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureSnapshotAsync();

        Assert.Equal(["192.0.2.53"], snapshot.V4);
        Assert.Equal(DnsService.DnsConfigurationSource.Automatic, snapshot.V4Source);
        Assert.Empty(snapshot.V6);
        Assert.Equal(DnsService.DnsConfigurationSource.Automatic, snapshot.V6Source);
    }

    [Fact]
    public async Task RestoreServersAsync_WithAddresses_ReAppliesThem()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7")); // interface index lookup
        using var svc = new DnsService(runner);

        await svc.RestoreServersAsync(["9.9.9.9", "149.112.112.112"]);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Set-DnsClientServerAddress") &&
                s.Contains("-InterfaceIndex 7") &&
                s.Contains("9.9.9.9") &&
                s.Contains("149.112.112.112")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreServersAsync_EmptySnapshot_ResetsToDhcp()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.RestoreServersAsync([]);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null && s.Contains("-ResetServerAddresses")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreSnapshotAsync_ResetsThenReAppliesBothFamilies()
    {
        // The reversibility regression: restoring must CLEAR both families first (so any
        // filtering IPv6 resolver applied since is removed) and then re-apply the captured
        // v4 + v6 — not leave the IPv6 in place as the old IPv4-only restore did.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.RestoreSnapshotAsync(new DnsService.DnsSnapshot(["9.9.9.9"], ["2620:fe::fe"]));

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("-ResetServerAddresses") &&        // clears whatever was applied since
                s.Contains("9.9.9.9") &&                      // re-applies captured IPv4
                s.Contains("2620:fe::fe")),                   // re-applies captured IPv6
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreSnapshotAsync_EmptyBothFamilies_ResetsToDhcp()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.RestoreSnapshotAsync(DnsService.DnsSnapshot.Empty);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null && s.Contains("-ResetServerAddresses") && !s.Contains("@(\"")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreSnapshotAsync_MixedAutomaticAndStatic_ReappliesOnlyStaticFamily()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            ["192.0.2.53"],
            ["2001:4860:4860::8888"],
            12,
            Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Automatic,
            DnsService.DnsConfigurationSource.Static);

        await svc.RestoreSnapshotAsync(snapshot);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(script => script != null &&
                script.Contains("-ResetServerAddresses") &&
                !script.Contains("192.0.2.53") &&
                script.Contains("2001:4860:4860::8888")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreServersAsync_NullSnapshot_Throws()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.RestoreServersAsync(null!));
    }

    // ---------- filtering variants + IPv6 (#910) ----------

    [Fact]
    public void GetPresets_IncludesFilteringVariants()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());
        var names = svc.GetPresets().Select(p => p.Name).ToList();

        Assert.Contains(names, n => n.Contains("Malware", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("AdGuard", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Family", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("FamilyShield", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPresets_PlainResolversCarryIpv6()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());
        var cloudflare = svc.GetPresets().First(p => p.Name == "Cloudflare");

        Assert.True(cloudflare.HasIpv6);
        Assert.Equal("2606:4700:4700::1111", cloudflare.PrimaryV6);
    }

    [Fact]
    public void GetPresets_AutomaticHasNoIpv6()
    {
        using var svc = new DnsService(Substitute.For<IPowerShellRunner>());
        var auto = svc.GetPresets().First(p => p.Name.Contains("Automatic", StringComparison.OrdinalIgnoreCase));
        Assert.False(auto.HasIpv6);
    }

    [Fact]
    public async Task SetDnsAsync_WithIpv6_SetsBothFamiliesInSeparateCalls()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync("1.1.1.2", "1.0.0.2", "2606:4700:4700::1112", "2606:4700:4700::1002");

        // One script issues two Set-DnsClientServerAddress calls: one IPv4, one IPv6.
        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("-InterfaceIndex 7") &&
                s.Contains("1.1.1.2") && s.Contains("1.0.0.2") &&
                s.Contains("2606:4700:4700::1112") && s.Contains("2606:4700:4700::1002")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDnsAsync_WithoutIpv6_OnlySetsIpv4()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync("8.8.8.8", "8.8.4.4", "", "");

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null && s.Contains("8.8.8.8") && !s.Contains("::")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("zzzz::1")]
    public async Task SetDnsAsync_InvalidIpv6_ThrowsAndNeverRunsScript(string badV6)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetDnsAsync("1.1.1.1", "1.0.0.1", badV6, ""));
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task RestoreServersAsync_InvalidAddressInSnapshot_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RestoreServersAsync(["8.8.8.8", "not-an-ip"]));

        // Validation happens before any interface lookup or Set script runs.
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    // ---------- adapter-pinned snapshot (#23 reversibility) ----------

    [Fact]
    public async Task CaptureSnapshotAsync_PopulatesStableAdapterIdentity()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(
                  12, "IPv4=1.1.1.1", "IPv6=2606:4700:4700::1111"));
        using var svc = new DnsService(runner);

        var snap = await svc.CaptureSnapshotAsync();

        await runner.Received(1).RunAsync(
            Arg.Is<string>(script => script != null &&
                script.Contains("$ErrorActionPreference = 'Stop'") &&
                script.Contains("Get-DnsClientServerAddress") &&
                script.Contains("-ErrorAction Stop") &&
                script.Contains("Tcpip\\Parameters\\Interfaces") &&
                script.Contains("Tcpip6\\Parameters\\Interfaces") &&
                script.Contains("NameServer") &&
                script.Contains("$nameServerBefore") &&
                script.Contains("$nameServerAfter") &&
                script.Contains("[string]::Equals") &&
                script.Contains("-isnot [string]") &&
                script.Contains("$adapterAfterCapture = Get-NetAdapter -InterfaceIndex $ifIndex") &&
                script.Contains("if ([Guid]($adapterAfterCapture.InterfaceGuid) -ne $interfaceGuid)") &&
                script.LastIndexOf("Get-DnsClientServerAddress", StringComparison.Ordinal) <
                script.IndexOf("$adapterAfterCapture = Get-NetAdapter", StringComparison.Ordinal) &&
                script.IndexOf(
                    "if ([Guid]($adapterAfterCapture.InterfaceGuid) -ne $interfaceGuid)",
                    StringComparison.Ordinal) <
                script.IndexOf("\"IDENTITY=Verified\"", StringComparison.Ordinal) &&
                script.Contains("\"SOURCE_${fam}=$source\"") &&
                script.Contains("\"COMPLETE=$fam\"")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(12, snap.IfIndex);
        Assert.Equal(Guid.Parse(TestInterfaceGuid), snap.InterfaceGuid);
        Assert.Equal(["1.1.1.1"], snap.V4);
        Assert.Equal(["2606:4700:4700::1111"], snap.V6);
        Assert.Equal(DnsService.DnsConfigurationSource.Static, snap.V4Source);
        Assert.Equal(DnsService.DnsConfigurationSource.Static, snap.V6Source);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_AdapterIdentityChangesBeforeFinalCheck_RejectsMarker()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(CaptureResult(12, "IPv4=1.1.1.1"));
        using var svc = new DnsService(runner);

        await svc.CaptureSnapshotAsync();

        var captureScript = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single()!;
        const string guardStartToken =
            "$adapterAfterCapture = Get-NetAdapter -InterfaceIndex $ifIndex -ErrorAction Stop";
        const string markerToken = "\"IDENTITY=Verified\"";
        var guardStart = captureScript.IndexOf(guardStartToken, StringComparison.Ordinal);
        Assert.True(guardStart >= 0);
        var markerStart = captureScript.IndexOf(markerToken, guardStart, StringComparison.Ordinal);
        Assert.True(markerStart > guardStart);
        var guardFragment = captureScript[guardStart..(markerStart + markerToken.Length)];

        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using (var setup = PowerShell.Create())
        {
            setup.Runspace = runspace;
            setup.AddScript("""
                function Get-NetAdapter {
                    [CmdletBinding()]
                    param([int] $InterfaceIndex)
                    [pscustomobject]@{
                        InterfaceGuid = [Guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                        ifIndex = $InterfaceIndex
                    }
                }
                """).Invoke();
        }

        using (var guard = PowerShell.Create())
        {
            guard.Runspace = runspace;
            guard.AddScript($$"""
                $ifIndex = 12
                $interfaceGuid = [Guid]'{{TestInterfaceGuid}}'
                $script:identityGuardThrew = $false
                $script:identityMarkerObserved = $false
                try {
                    & {
                        {{guardFragment}}
                    } | ForEach-Object {
                        if ($_ -eq 'IDENTITY=Verified') {
                            $script:identityMarkerObserved = $true
                        }
                    }
                }
                catch {
                    $script:identityGuardThrew = $true
                }
                """).Invoke();
        }

        using var query = PowerShell.Create();
        query.Runspace = runspace;
        var state = query.AddScript(
            "$script:identityGuardThrew; $script:identityMarkerObserved").Invoke();
        Assert.True((bool)state[0].BaseObject);
        Assert.False((bool)state[1].BaseObject);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_NoAdapter_ThrowsInsteadOfReturningAmbiguousSnapshot()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("No active network adapter", ex.Message);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_MalformedAdapterGuid_ThrowsBeforeSnapshotIsTrusted()
    {
        var results = CaptureResult(12, "IPv4=1.1.1.1");
        results[1] = PSObject.AsPSObject("IFGUID=not-a-guid");
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(results);
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());
    }

    [Fact]
    public async Task CaptureSnapshotAsync_IncompleteFamilyRead_ThrowsInsteadOfTrustingMissingDataAsDhcp()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IFINDEX=12"),
                  PSObject.AsPSObject($"IFGUID={TestInterfaceGuid}"),
                  PSObject.AsPSObject("IPv4=1.1.1.1"),
                  PSObject.AsPSObject("COMPLETE=IPv4"),
                  PSObject.AsPSObject("SOURCE_IPv4=Static"),
                  PSObject.AsPSObject("SOURCE_IPv6=Automatic"),
                  PSObject.AsPSObject("IDENTITY=Verified"),
              });
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());
        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_MissingSourceMetadata_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IFINDEX=12"),
                  PSObject.AsPSObject($"IFGUID={TestInterfaceGuid}"),
                  PSObject.AsPSObject("SOURCE_IPv4=Static"),
                  PSObject.AsPSObject("IPv4=1.1.1.1"),
                  PSObject.AsPSObject("COMPLETE=IPv4"),
                  PSObject.AsPSObject("COMPLETE=IPv6"),
                  PSObject.AsPSObject("IDENTITY=Verified"),
              });
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_MalformedSourceMetadata_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IFINDEX=12"),
                  PSObject.AsPSObject($"IFGUID={TestInterfaceGuid}"),
                  PSObject.AsPSObject("SOURCE_IPv4=Static"),
                  PSObject.AsPSObject("SOURCE_IPv6=DHCP"),
                  PSObject.AsPSObject("IPv4=1.1.1.1"),
                  PSObject.AsPSObject("COMPLETE=IPv4"),
                  PSObject.AsPSObject("COMPLETE=IPv6"),
                  PSObject.AsPSObject("IDENTITY=Verified"),
              });
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_MissingPostCaptureIdentityVerification_Throws()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IFINDEX=12"),
                  PSObject.AsPSObject($"IFGUID={TestInterfaceGuid}"),
                  PSObject.AsPSObject("SOURCE_IPv4=Static"),
                  PSObject.AsPSObject("SOURCE_IPv6=Automatic"),
                  PSObject.AsPSObject("IPv4=1.1.1.1"),
                  PSObject.AsPSObject("COMPLETE=IPv4"),
                  PSObject.AsPSObject("COMPLETE=IPv6"),
              });
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Theory]
    [InlineData("Unverified", false)]
    [InlineData("Verified", true)]
    public async Task CaptureSnapshotAsync_InvalidPostCaptureIdentityMetadata_Throws(
        string marker,
        bool duplicate)
    {
        var results = CaptureResult(12, "IPv4=1.1.1.1");
        if (duplicate)
            results.Add(PSObject.AsPSObject($"IDENTITY={marker}"));
        else
            results[results.Count - 1] = PSObject.AsPSObject($"IDENTITY={marker}");

        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(results);
        using var svc = new DnsService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaptureSnapshotAsync());

        Assert.Contains("could not be captured completely", ex.Message);
    }

    [Fact]
    public async Task SetDnsAsync_CapturedTarget_GuardsAndMutatesSameAdapterWithoutReselection()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        await svc.SetDnsAsync(target, "1.1.1.1", "1.0.0.1", "", "");

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Get-NetAdapter -InterfaceIndex 12") &&
                s.Contains(TestInterfaceGuid) &&
                s.Contains("$expectedSources") &&
                s.Contains("$expectedAddresses") &&
                s.Contains("$nameServerBefore") &&
                s.Contains("$nameServerAfter") &&
                s.Contains("Set-DnsClientServerAddress -InterfaceIndex $targetIfIndex") &&
                s.LastIndexOf("Get-NetAdapter -InterfaceIndex 12", StringComparison.Ordinal) <
                s.IndexOf("Set-DnsClientServerAddress", StringComparison.Ordinal) &&
                !s.Contains("Where-Object")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Static", "8.8.8.8", true)]
    [InlineData("Automatic", "8.8.8.8", false)]
    [InlineData("Static", "9.9.9.9", false)]
    [InlineData("Static", "8.8.8.8,1.1.1.1", false)]
    public async Task SetDnsAsync_DnsStateComparison_BlocksSourceAndAddressDrift(
        string actualSource,
        string actualAddressesCsv,
        bool mutationExpected)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        await svc.SetDnsAsync(target, "1.1.1.1", "1.0.0.1", "", "");

        var script = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single(value => value?.Contains("Set-DnsClientServerAddress") == true)!;
        const string startToken = "# SYSMANAGER_DNS_STATE_COMPARISON_START";
        const string endToken = "# SYSMANAGER_DNS_STATE_COMPARISON_END";
        var comparisonStart = script.IndexOf(startToken, StringComparison.Ordinal);
        var comparisonEnd = script.IndexOf(endToken, StringComparison.Ordinal);
        Assert.True(comparisonStart >= 0);
        Assert.True(comparisonEnd > comparisonStart);
        comparisonStart += startToken.Length;
        var comparison = script[comparisonStart..comparisonEnd];

        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        runspace.SessionStateProxy.SetVariable(
            "actualNameServer",
            actualSource == "Static" ? "8.8.8.8" : "");
        runspace.SessionStateProxy.SetVariable(
            "actualAddresses",
            actualAddressesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries));

        using var guard = PowerShell.Create();
        guard.Runspace = runspace;
        var state = guard.AddScript($$"""
            $fam = 'IPv4'
            $nameServerAfter = $actualNameServer
            $dns = [pscustomobject]@{ ServerAddresses = @($actualAddresses) }
            $expectedSources = @{ IPv4 = 'Static' }
            $expectedAddresses = @{ IPv4 = @('8.8.8.8') }
            $script:mutationAttempted = $false
            $script:guardRejected = $false
            try {
                {{comparison}}
                $script:mutationAttempted = $true
            }
            catch {
                $script:guardRejected = $true
            }
            $script:mutationAttempted
            $script:guardRejected
            """).Invoke();

        Assert.Empty(guard.Streams.Error);
        Assert.Equal(2, state.Count);
        Assert.Equal(mutationExpected, (bool)state[0].BaseObject);
        Assert.Equal(!mutationExpected, (bool)state[1].BaseObject);
    }
    [Fact]
    public async Task SetDnsAsync_GuidMismatch_StopsBeforeMutationCommandRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        await svc.SetDnsAsync(target, "1.1.1.1", "1.0.0.1", "", "");

        var script = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single(value => value?.Contains("Set-DnsClientServerAddress") == true)!;
        Assert.True(
            script.IndexOf("if ([Guid]($capturedAdapter.InterfaceGuid)", StringComparison.Ordinal) <
            script.IndexOf("Set-DnsClientServerAddress", StringComparison.Ordinal));

        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using (var setup = PowerShell.Create())
        {
            setup.Runspace = runspace;
            setup.AddScript("""
                function Get-NetAdapter {
                    [CmdletBinding()]
                    param([int] $InterfaceIndex)
                    [pscustomobject]@{
                        InterfaceGuid = [Guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
                        ifIndex = $InterfaceIndex
                    }
                }
                function Set-DnsClientServerAddress {
                    [CmdletBinding()]
                    param(
                        [int] $InterfaceIndex,
                        [string[]] $ServerAddresses,
                        [switch] $ResetServerAddresses)
                    $script:mutationAttempted = $true
                }
                $script:mutationAttempted = $false
                """).Invoke();
        }

        using (var mutation = PowerShell.Create())
        {
            mutation.Runspace = runspace;
            var output = mutation.AddScript(script).Invoke();
            Assert.Contains(output, item =>
                item?.ToString()?.StartsWith(
                    "SYSMANAGER_DNS_PRECONDITION_FAILED|",
                    StringComparison.Ordinal) == true);
        }

        using var query = PowerShell.Create();
        query.Runspace = runspace;
        var attempted = query.AddScript("$script:mutationAttempted").Invoke();
        Assert.False((bool)attempted[0].BaseObject);
    }

    [Fact]
    public async Task ResetToDhcpAsync_CapturedTarget_GuardsAndResetsSameAdapterWithoutReselection()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        await svc.ResetToDhcpAsync(target);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Get-NetAdapter -InterfaceIndex 12") &&
                s.Contains(TestInterfaceGuid) &&
                s.Contains("$expectedSources") &&
                s.Contains("$expectedAddresses") &&
                s.Contains("Set-DnsClientServerAddress -InterfaceIndex $targetIfIndex") &&
                s.Contains("-ResetServerAddresses") &&
                !s.Contains("Where-Object")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDnsAsync_PreconditionMarker_ThrowsTypedNoMutationException()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result(
                "SYSMANAGER_DNS_PRECONDITION_FAILED|DNS registry state is unavailable."));
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        var ex = await Assert.ThrowsAsync<DnsService.DnsMutationPreconditionException>(() =>
            svc.SetDnsAsync(target, "1.1.1.1", "1.0.0.1", "", ""));

        Assert.Contains("DNS registry state is unavailable", ex.Message);
    }

    [Fact]
    public async Task SetDnsAsync_WithIpv6_RechecksStateAndIdentityBeforeSecondMutation()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        await svc.SetDnsAsync(
            target,
            "1.1.1.1",
            "1.0.0.1",
            "2606:4700:4700::1111",
            "2606:4700:4700::1001");

        var script = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single(value => value?.Contains("Set-DnsClientServerAddress") == true)!;
        var firstMutation = script.IndexOf("Set-DnsClientServerAddress", StringComparison.Ordinal);
        var secondMutation = script.IndexOf(
            "Set-DnsClientServerAddress",
            firstMutation + 1,
            StringComparison.Ordinal);
        var interveningStateComparison = script.IndexOf(
            "# SYSMANAGER_DNS_STATE_COMPARISON_START",
            firstMutation + 1,
            StringComparison.Ordinal);
        var interveningGuard = script.IndexOf(
            "Get-NetAdapter -InterfaceIndex 12",
            Math.Max(firstMutation + 1, interveningStateComparison + 1),
            StringComparison.Ordinal);

        Assert.True(firstMutation >= 0);
        Assert.True(interveningStateComparison > firstMutation);
        Assert.True(interveningGuard > interveningStateComparison);
        Assert.True(secondMutation > interveningGuard);
        Assert.Contains(
            "$expectedSources = @{ IPv4 = 'Static'; IPv6 = 'Automatic' }",
            script[firstMutation..secondMutation]);
        Assert.Contains(
            "$expectedAddresses = @{ IPv4 = @(\"1.1.1.1\",\"1.0.0.1\"); IPv6 = @() }",
            script[firstMutation..secondMutation]);
    }

    [Fact]
    public async Task SetDnsAsync_Ipv6Failure_PropagatesAfterIpv4Mutation()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result("7"));
        using var svc = new DnsService(runner);

        await svc.SetDnsAsync(
            "1.1.1.1",
            "1.0.0.1",
            "2606:4700:4700::1111",
            "2606:4700:4700::1001");

        var script = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single(value => value?.Contains("Set-DnsClientServerAddress") == true)!;
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using var mutation = PowerShell.Create();
        mutation.Runspace = runspace;
        var state = mutation.AddScript($$"""
            $script:setCalls = 0
            $script:completed = $false
            $script:failureObserved = $false
            function Set-DnsClientServerAddress {
                [CmdletBinding()]
                param(
                    [int] $InterfaceIndex,
                    [string[]] $ServerAddresses,
                    [switch] $ResetServerAddresses)
                $script:setCalls++
                if ($script:setCalls -eq 2) {
                    throw 'Simulated IPv6 mutation failure.'
                }
            }
            try {
                & {
                    {{script}}
                }
                $script:completed = $true
            }
            catch {
                $script:failureObserved = $true
            }
            $script:setCalls
            $script:completed
            $script:failureObserved
            """).Invoke();

        Assert.Empty(mutation.Streams.Error);
        Assert.Equal(2, (int)state[0].BaseObject);
        Assert.False((bool)state[1].BaseObject);
        Assert.True((bool)state[2].BaseObject);
    }
    [Fact]
    public async Task RestoreSnapshotAsync_StableGuidFollowsIndexDriftBeforeEveryMutation()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            ["9.9.9.9"], ["2620:fe::fe"], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Static);

        await svc.RestoreSnapshotAsync(snapshot);

        var script = runner.ReceivedCalls()
            .Select(call => call.GetArguments()[0] as string)
            .Single(value => value?.Contains("Set-DnsClientServerAddress") == true)!;
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using (var setup = PowerShell.Create())
        {
            setup.Runspace = runspace;
            setup.AddScript($$"""
                $script:nextIndex = 26
                $script:mutationIndexes = @()
                function Get-NetAdapter {
                    [CmdletBinding()]
                    param([switch] $IncludeHidden)
                    $script:nextIndex++
                    [pscustomobject]@{
                        InterfaceGuid = [Guid]'{{TestInterfaceGuid}}'
                        ifIndex = $script:nextIndex
                    }
                }
                function Set-DnsClientServerAddress {
                    [CmdletBinding()]
                    param(
                        [int] $InterfaceIndex,
                        [string[]] $ServerAddresses,
                        [switch] $ResetServerAddresses)
                    $script:mutationIndexes += $InterfaceIndex
                }
                """).Invoke();
        }

        using (var mutation = PowerShell.Create())
        {
            mutation.Runspace = runspace;
            mutation.AddScript(script).Invoke();
            Assert.Empty(mutation.Streams.Error);
        }

        using var query = PowerShell.Create();
        query.Runspace = runspace;
        var indexes = query.AddScript("$script:mutationIndexes").Invoke();
        Assert.Equal([27, 28, 29], indexes.Select(item => (int)item.BaseObject));
    }
    [Fact]
    public async Task SetDnsAsync_InvalidCapturedTarget_RejectsBeforeAnyScriptRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetDnsAsync(
            DnsService.DnsSnapshot.Empty, "1.1.1.1", "1.0.0.1", "", ""));

        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task SetDnsAsync_CapturedTargetWithoutStableGuid_RejectsBeforeAnyScriptRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);
        var target = new DnsService.DnsSnapshot(["8.8.8.8"], [], 12);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SetDnsAsync(
            target, "1.1.1.1", "1.0.0.1", "", ""));

        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_PinnedTargetWithoutStableGuid_RejectsBeforeAnyScriptRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, null,
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RestoreSnapshotAsync(snapshot));

        Assert.Contains("captured network adapter", ex.Message);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_PinnedTargetWithoutSourceMetadata_RejectsBeforeAnyScriptRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            ["8.8.8.8"], [], 12, Guid.Parse(TestInterfaceGuid));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RestoreSnapshotAsync(snapshot));

        Assert.Contains("configuration source", ex.Message);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_StaticSourceWithoutAddress_RejectsBeforeAnyScriptRuns()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            [], [], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Automatic);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.RestoreSnapshotAsync(snapshot));

        Assert.Contains("must include", ex.Message);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default);
    }

    [Fact]
    public async Task RestoreSnapshotAsync_PinnedIdentity_GuardsAndRestoresInSingleScript()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);
        var snapshot = new DnsService.DnsSnapshot(
            ["9.9.9.9"], ["2620:fe::fe"], 12, Guid.Parse(TestInterfaceGuid),
            DnsService.DnsConfigurationSource.Static,
            DnsService.DnsConfigurationSource.Static);

        await svc.RestoreSnapshotAsync(snapshot);

        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("Get-NetAdapter -IncludeHidden") &&
                s.Contains("Where-Object { [Guid]$_.InterfaceGuid") &&
                s.Contains(TestInterfaceGuid) &&
                s.Contains("-InterfaceIndex $targetIfIndex") &&
                s.Contains("-ResetServerAddresses") &&
                s.Contains("9.9.9.9") &&
                s.Contains("2620:fe::fe")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreSnapshotAsync_ZeroIfIndex_FallsBackToDynamicLookup()
    {
        // IfIndex=0 means legacy snapshot — should use GetActiveInterfaceIndexAsync
        // (the ActiveAdapterSelector script), not the Get-NetAdapter verify path.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(Result("5"));
        using var svc = new DnsService(runner);

        await svc.RestoreSnapshotAsync(new DnsService.DnsSnapshot(["8.8.8.8"], [], 0));

        // Should NOT issue Get-NetAdapter -InterfaceIndex 0 verify.
        await runner.DidNotReceive().RunAsync(
            Arg.Is<string>(s => s != null && s.Contains("Get-NetAdapter -InterfaceIndex 0")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());

        // Should use the dynamic selector and then target the resolved index.
        await runner.Received(1).RunAsync(
            Arg.Is<string>(s => s != null &&
                s.Contains("-InterfaceIndex 5") &&
                s.Contains("-ResetServerAddresses")),
            Arg.Any<IDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }
}
