// SysManager · DnsHostsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
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

/// <summary>
/// Confirmation-gate coverage for the destructive/system-mutating DNS &amp; hosts
/// commands. These swap the process-wide <see cref="DialogService.Instance"/>, so they
/// run in the serialized "DialogService" collection. Both injected services are real
/// but harmless: <see cref="DnsService"/> takes a substituted <see cref="IPowerShellRunner"/>
/// (no live netsh/PowerShell), and <see cref="HostsFileService"/> takes a temp-file path
/// (never touches System32). <c>IsElevated</c> is set true in-test to pass the admin guard
/// that sits before each gate.
/// </summary>
[Collection("DialogService")]
public class DnsHostsViewModelGateTests
{
    private static (DnsHostsViewModel vm, string hostsPath, string dir, IPowerShellRunner runner) NewVm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_dnsgate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hostsPath = Path.Combine(dir, "hosts");
        File.WriteAllText(hostsPath, "127.0.0.1 localhost\n");
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = new DnsHostsViewModel(new DnsService(runner), new HostsFileService(hostsPath)) { IsElevated = true };
        return (vm, hostsPath, dir, runner);
    }

    // ── SaveHosts (overwrites the system hosts file) ──────────────────────

    [StaFact]
    public void SaveHosts_WhenUserDeclinesConfirm_DoesNotWrite()
    {
        var (vm, hostsPath, dir, _) = NewVm();
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            var before = File.ReadAllText(hostsPath);
            vm.HostEntries.Add(new HostsEntry { IpAddress = "10.0.0.1", Hostname = "managed.local" });

            vm.SaveHostsCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must leave the hosts file byte-for-byte untouched.
            Assert.Equal(before, File.ReadAllText(hostsPath));
            Assert.Contains("cancelled", vm.HostsStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [StaFact]
    public void SaveHosts_WhenUserConfirms_ProceedsPastGate()
    {
        // Confirm side: the gate is passed and the save path runs (StatusMessage
        // advances to the success/saved text, set synchronously right after the
        // gate). We assert the gate + the "not cancelled" outcome rather than the
        // written file, because the VM's async init also reads the same temp hosts
        // file — racing it here would be non-deterministic. The decline test below
        // already proves the gate blocks the write.
        var (vm, _, dir, _) = NewVm();
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // "Yes"
        DialogService.Instance = dialog;
        try
        {
            vm.HostEntries.Add(new HostsEntry { IpAddress = "10.0.0.1", Hostname = "managed.local", IsEnabled = true });

            vm.SaveHostsCommand.Execute(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Confirming must NOT short-circuit with the cancellation message.
            Assert.DoesNotContain("cancelled", vm.HostsStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [StaFact]
    public void SaveHosts_WhenNotElevated_NeverPromptsConfirm()
    {
        var (vm, _, dir, _) = NewVm();
        vm.IsElevated = false; // admin guard sits before the gate
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.HostEntries.Add(new HostsEntry { IpAddress = "10.0.0.1", Hostname = "x.local" });
            vm.SaveHostsCommand.Execute(null);

            // Non-elevated short-circuits before the destructive prompt.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── ApplyDns (changes the system DNS servers) ─────────────────────────

    [StaFact]
    public async Task ApplyDns_WhenUserDeclinesConfirm_ShortCircuits()
    {
        var (vm, _, dir, _) = NewVm();
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            // A non-DHCP preset (non-empty Primary) reaches the Confirm gate.
            vm.SelectedPreset = vm.Presets.First(p => !string.IsNullOrEmpty(p.Primary));

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining sets the cancellation message and never enters the applying
            // state. (We assert VM state rather than runner calls because the VM's
            // async startup also calls the runner to read current DNS — asserting
            // "no runner calls" would race that init.)
            Assert.Equal("DNS change cancelled.", vm.StatusMessage);
            Assert.False(vm.IsDnsApplying);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── RestoreHosts (discards current hosts, restores backup) ────────────

    [StaFact]
    public async Task RestoreHosts_WhenUserDeclinesConfirm_DoesNotRestore()
    {
        var (vm, hostsPath, dir, _) = NewVm();
        // A backup must exist for the gate to be reachable (HasBackup guard).
        var backup = hostsPath + ".bak";
        File.WriteAllText(backup, "# ORIGINAL pristine\n127.0.0.1 original\n");
        var current = "# CURRENT managed\n10.0.0.1 managed\n";
        File.WriteAllText(hostsPath, current);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            await vm.RestoreHostsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must leave the live hosts file untouched (not restored from .bak).
            Assert.Equal(current, File.ReadAllText(hostsPath));
        }
        finally
        {
            DialogService.Instance = prevDialog;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
