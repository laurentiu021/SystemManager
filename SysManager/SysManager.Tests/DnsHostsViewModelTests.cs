// SysManager · DnsHostsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DnsHostsViewModel"/>. Verifies presets, host entry
/// management, and validation without performing actual DNS or file operations.
/// Uses StaFact for tests that instantiate the full ViewModel (requires WPF Dispatcher).
/// </summary>
public class DnsHostsViewModelTests
{
    private static DnsHostsViewModel CreateVm() =>
        new(new DnsService(new PowerShellRunner()), new HostsFileService());

    [StaFact]
    public void Constructor_PresetsListPopulated_With5Presets()
    {
        var vm = CreateVm();
        Assert.Equal(5, vm.Presets.Count);
    }

    [StaFact]
    public void Constructor_HostEntries_IsNotNull()
    {
        var vm = CreateVm();
        Assert.NotNull(vm.HostEntries);
    }

    [StaFact]
    public void Presets_ContainsExpectedNames()
    {
        var vm = CreateVm();
        var names = vm.Presets.Select(p => p.Name).ToList();
        Assert.Contains("Google", names);
        Assert.Contains("Cloudflare", names);
        Assert.Contains("Quad9", names);
        Assert.Contains("OpenDNS", names);
        Assert.Contains("Automatic (DHCP)", names);
    }

    [Fact]
    public void HostsFileService_AddEntry_WithValidIpAndHostname_ReturnsEntry()
    {
        var hostsService = new HostsFileService();
        var entry = hostsService.AddEntry("127.0.0.1", "myhost.local");
        Assert.Equal("127.0.0.1", entry.IpAddress);
        Assert.Equal("myhost.local", entry.Hostname);
        Assert.True(entry.IsEnabled);
    }

    [Theory]
    [InlineData("999.999.999.999", "valid.host")]
    [InlineData("notanip", "valid.host")]
    [InlineData("", "valid.host")]
    public void HostsFileService_AddEntry_WithInvalidIp_ThrowsArgumentException(string ip, string hostname)
    {
        var hostsService = new HostsFileService();
        Assert.Throws<ArgumentException>(() => hostsService.AddEntry(ip, hostname));
    }

    [Theory]
    [InlineData("127.0.0.1", "")]
    [InlineData("127.0.0.1", "   ")]
    [InlineData("127.0.0.1", "invalid host name!")]
    public void HostsFileService_AddEntry_WithInvalidHostname_ThrowsArgumentException(string ip, string hostname)
    {
        var hostsService = new HostsFileService();
        Assert.Throws<ArgumentException>(() => hostsService.AddEntry(ip, hostname));
    }

    [StaFact]
    public void RemoveEntry_RemovesFromCollection()
    {
        var vm = CreateVm();
        var entry = new HostsEntry { IpAddress = "10.0.0.1", Hostname = "test.local" };
        vm.HostEntries.Add(entry);

        var countBefore = vm.HostEntries.Count;
        vm.RemoveEntryCommand.Execute(entry);
        Assert.Equal(countBefore - 1, vm.HostEntries.Count);
        Assert.DoesNotContain(entry, vm.HostEntries);
    }
}
