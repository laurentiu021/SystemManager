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
}
