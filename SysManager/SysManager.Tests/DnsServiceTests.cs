// SysManager · DnsServiceTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Management.Automation;
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
    private static Collection<PSObject> Result(string value) =>
        new() { PSObject.AsPSObject(value) };

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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s =>
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
        // Capture now tags each address with its family ("IPv4=" / "IPv6=").
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IPv4=8.8.8.8"),
                  PSObject.AsPSObject("IPv4=8.8.4.4"),
              });
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Equal(["8.8.8.8", "8.8.4.4"], snapshot);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_CapturesBothFamilies()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IPv4=8.8.8.8"),
                  PSObject.AsPSObject("IPv6=2001:4860:4860::8888"),
              });
        using var svc = new DnsService(runner);

        var snap = await svc.CaptureSnapshotAsync();

        Assert.Equal(["8.8.8.8"], snap.V4);
        Assert.Equal(["2001:4860:4860::8888"], snap.V6);
    }

    [Fact]
    public async Task CaptureCurrentServersAsync_FiltersNonIpNoise()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>
              {
                  PSObject.AsPSObject("IPv4=1.1.1.1"),
                  PSObject.AsPSObject(""),            // blank line
                  PSObject.AsPSObject("IPv4=garbage"),// non-IP noise
              });
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Equal(["1.1.1.1"], snapshot);
    }

    [Fact]
    public async Task CaptureCurrentServersAsync_Dhcp_ReturnsEmpty()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
        using var svc = new DnsService(runner);

        var snapshot = await svc.CaptureCurrentServersAsync();

        Assert.Empty(snapshot);
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s => s.Contains("-ResetServerAddresses")),
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s => s.Contains("-ResetServerAddresses") && !s.Contains("@(\"")),
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
            Arg.Is<string>(s =>
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
            Arg.Is<string>(s => s.Contains("8.8.8.8") && !s.Contains("::")),
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
}
