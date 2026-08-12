// SysManager · DnsHostsViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DnsHostsViewModel"/>. Verifies presets, host entry
/// management, and validation without performing actual DNS or file operations.
/// Uses StaFact for tests that instantiate the full ViewModel (requires WPF Dispatcher).
/// </summary>
public class DnsHostsViewModelTests
{
    private static DnsHostsViewModel NewVm() =>
        new(new DnsService(new PowerShellRunner()), new HostsFileService());

    [StaFact]
    public void Constructor_PresetsListPopulated_WithFilteringVariants()
    {
        var vm = NewVm();
        // 4 plain resolvers + 5 filtering variants + Automatic (DHCP).
        Assert.Equal(10, vm.Presets.Count);
    }

    [StaFact]
    public void Constructor_HostEntries_IsNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.HostEntries);
    }

    [StaFact]
    public void Presets_ContainsExpectedNames()
    {
        var vm = NewVm();
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
        var vm = NewVm();
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
[Collection("ProcessWideStatics")]
public class DnsHostsViewModelGateTests
{
    private const string TestInterfaceGuid = "11111111-2222-3333-4444-555555555555";
    private const string OtherInterfaceGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static Collection<PSObject> CaptureResult(
        string ipv4 = "8.8.8.8",
        int ifIndex = 12,
        string interfaceGuid = TestInterfaceGuid) =>
        new()
        {
            PSObject.AsPSObject($"IFINDEX={ifIndex}"),
            PSObject.AsPSObject($"IFGUID={interfaceGuid}"),
            PSObject.AsPSObject("SOURCE_IPv4=Static"),
            PSObject.AsPSObject("SOURCE_IPv6=Automatic"),
            PSObject.AsPSObject($"IPv4={ipv4}"),
            PSObject.AsPSObject("COMPLETE=IPv4"),
            PSObject.AsPSObject("COMPLETE=IPv6"),
            PSObject.AsPSObject("IDENTITY=Verified"),
        };

    private static Collection<PSObject> DhcpCaptureResult() =>
        new()
        {
            PSObject.AsPSObject("IFINDEX=12"),
            PSObject.AsPSObject($"IFGUID={TestInterfaceGuid}"),
            PSObject.AsPSObject("SOURCE_IPv4=Automatic"),
            PSObject.AsPSObject("SOURCE_IPv6=Automatic"),
            PSObject.AsPSObject("IPv4=192.0.2.53"),
            PSObject.AsPSObject("COMPLETE=IPv4"),
            PSObject.AsPSObject("COMPLETE=IPv6"),
            PSObject.AsPSObject("IDENTITY=Verified"),
        };

    private static (DnsHostsViewModel vm, string hostsPath, string dir, IPowerShellRunner runner) NewVm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smtest_dnsgate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var hostsPath = Path.Combine(dir, "hosts");
        File.WriteAllText(hostsPath, "127.0.0.1 localhost\n");
        var runner = Substitute.For<IPowerShellRunner>();
        // autoInit: false suppresses the async startup load so the gate assertions
        // are deterministic (no background thread mutating CurrentDns/HostsStatus or
        // reading the temp hosts file while the test runs).
        var vm = new DnsHostsViewModel(new DnsService(runner), new HostsFileService(hostsPath), autoInit: false) { IsElevated = true };
        return (vm, hostsPath, dir, runner);
    }

    private static void DeleteTestDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Test cleanup failed for '{path}': {ex.Message}");
        }
    }

    // ── SaveHosts (overwrites the system hosts file) ──────────────────────

    [StaFact]
    public async Task SaveHosts_WhenUserDeclinesConfirm_DoesNotWrite()
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

            await vm.SaveHostsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must leave the hosts file byte-for-byte untouched.
            Assert.Equal(before, File.ReadAllText(hostsPath));
            Assert.Contains("cancelled", vm.HostsStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task SaveHosts_WhenUserConfirms_WritesEntries()
    {
        var (vm, hostsPath, dir, _) = NewVm();
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // "Yes"
        DialogService.Instance = dialog;
        try
        {
            vm.HostEntries.Add(new HostsEntry { IpAddress = "10.0.0.1", Hostname = "managed.local", IsEnabled = true });

            await vm.SaveHostsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Confirming writes the entries through the SysManager-managed hosts file.
            var written = File.ReadAllText(hostsPath);
            Assert.Contains("managed.local", written);
            Assert.Contains("managed by SysManager", written);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task SaveHosts_WhenNotElevated_NeverPromptsConfirm()
    {
        var (vm, _, dir, _) = NewVm();
        vm.IsElevated = false; // admin guard sits before the gate
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.HostEntries.Add(new HostsEntry { IpAddress = "10.0.0.1", Hostname = "x.local" });
            await vm.SaveHostsCommand.ExecuteAsync(null);

            // Non-elevated short-circuits before the destructive prompt.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
            DeleteTestDirectory(dir);
        }
    }

    // ── ApplyDns (changes the system DNS servers) ─────────────────────────

    [StaFact]
    public async Task ApplyDns_WhenUserDeclinesCapturedTarget_DoesNotMutate()
    {
        var (vm, _, dir, runner) = NewVm();
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CaptureResult()));

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // "No"
        DialogService.Instance = dialog;
        try
        {
            // A non-DHCP preset (non-empty Primary) reaches the Confirm gate.
            vm.SelectedPreset = vm.Presets.First(p => !string.IsNullOrEmpty(p.Primary));

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(
                Arg.Is<string>(message => message != null &&
                    message.Contains("interface 12")),
                "Confirm DNS Change");
            await runner.DidNotReceive().RunAsync(
                Arg.Is<string>(script => script != null &&
                    script.Contains("Set-DnsClientServerAddress")),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
            Assert.Equal("DNS change cancelled.", vm.StatusMessage);
            Assert.False(vm.IsDnsApplying);
        }
        finally
        {
            DialogService.Instance = prevDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ApplyDns_WhenDnsChangesDuringConfirmation_DoesNotMutateOrArmUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) == 1
                            ? CaptureResult()
                            : CaptureResult("9.9.9.9"));
                }
                if (script.Contains("Set-DnsClientServerAddress", StringComparison.Ordinal))
                {
                    return Task.FromException<Collection<PSObject>>(
                        new RuntimeException("Mutation must not run after confirmation drift."));
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed while confirmation was open", vm.StatusMessage);
            Assert.DoesNotContain(runner.ReceivedCalls(), call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true);
            dialog.Received(1).Confirm(Arg.Any<string>(), "Confirm DNS Change");
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ResetDns_WhenDnsChangesDuringConfirmation_DoesNotMutateOrArmUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) == 1
                            ? CaptureResult()
                            : CaptureResult("9.9.9.9"));
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed while confirmation was open", vm.StatusMessage);
            Assert.DoesNotContain(runner.ReceivedCalls(), call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true);
            dialog.Received(1).Confirm(Arg.Any<string>(), "Confirm DNS Reset");
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ApplyDns_WhenAdapterIdentityChangesDuringConfirmation_DoesNotMutateOrArmUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) == 1
                            ? CaptureResult()
                            : CaptureResult(interfaceGuid: OtherInterfaceGuid));
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed while confirmation was open", vm.StatusMessage);
            Assert.DoesNotContain(runner.ReceivedCalls(), call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ResetDns_WhenAdapterIndexChangesDuringConfirmation_DoesNotMutateOrArmUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) == 1
                            ? CaptureResult()
                            : CaptureResult(ifIndex: 27));
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed while confirmation was open", vm.StatusMessage);
            Assert.DoesNotContain(runner.ReceivedCalls(), call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task FailedAttempt_PreservesPriorSuccessfulUndoAsFallbackOnSameAdapter()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) <= 2
                            ? CaptureResult()
                            : DhcpCaptureResult());
                }
                if (script.Contains("if ($adapter) { $adapter.ifIndex }", StringComparison.Ordinal))
                    return Task.FromResult(new Collection<PSObject> { PSObject.AsPSObject("27") });
                if (script.Contains("Set-DnsClientServerAddress", StringComparison.Ordinal) &&
                    !script.Contains("-ResetServerAddresses", StringComparison.Ordinal))
                {
                    return Task.FromException<Collection<PSObject>>(
                        new RuntimeException("Simulated failure after mutation dispatch."));
                }
                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var mutations = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(4, mutations.Count);
            Assert.DoesNotContain("8.8.8.8", mutations[2]);
            Assert.DoesNotContain("192.0.2.53", mutations[2]);
            Assert.Contains("8.8.8.8", mutations[3]);
            Assert.All(mutations, script =>
            {
                Assert.Contains(TestInterfaceGuid, script);
                Assert.DoesNotContain("-InterfaceIndex 27", script);
            });
            Assert.All(mutations.Take(2), script =>
            {
                Assert.Contains("Get-NetAdapter -InterfaceIndex 12", script);
                Assert.Contains(
                    "Set-DnsClientServerAddress -InterfaceIndex $targetIfIndex",
                    script);
            });
            Assert.All(mutations.Skip(2), script =>
            {
                Assert.Contains("Get-NetAdapter -IncludeHidden", script);
                Assert.Contains(
                    "Set-DnsClientServerAddress -InterfaceIndex $targetIfIndex",
                    script);
            });
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task AmbiguousFailureThenSuccessfulRetry_UndoRestoresLastTrustedSnapshot()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        var mutationCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    var capture = Interlocked.Increment(ref captureCount) <= 2
                        ? CaptureResult("8.8.8.8")
                        : CaptureResult("203.0.113.53");
                    return Task.FromResult(capture);
                }
                if (script.Contains("$expectedSources", StringComparison.Ordinal))
                {
                    return Interlocked.Increment(ref mutationCount) == 1
                        ? Task.FromException<Collection<PSObject>>(
                            new RuntimeException("Simulated ambiguous partial failure."))
                        : Task.FromResult(new Collection<PSObject>());
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);

            var restoreScript = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Last(script => script?.Contains(
                    "Get-NetAdapter -IncludeHidden", StringComparison.Ordinal) == true);
            Assert.Contains("8.8.8.8", restoreScript);
            Assert.DoesNotContain("203.0.113.53", restoreScript);
            Assert.False(vm.CanRestorePreviousDns);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }
    [StaFact]
    public async Task AmbiguousFailureThenSuccessfulRetryOnDifferentAdapter_PreservesBothUndoTargets()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        var mutationCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    var capture = Interlocked.Increment(ref captureCount) <= 2
                        ? CaptureResult("8.8.8.8", 12, TestInterfaceGuid)
                        : CaptureResult("9.9.9.9", 27, OtherInterfaceGuid);
                    return Task.FromResult(capture);
                }
                if (script.Contains("-AddressFamily IPv4", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("1.1.1.1, 1.0.0.1"),
                    });
                }
                if (script.Contains("$expectedSources", StringComparison.Ordinal))
                {
                    return Interlocked.Increment(ref mutationCount) == 1
                        ? Task.FromException<Collection<PSObject>>(
                            new RuntimeException("Simulated ambiguous partial failure."))
                        : Task.FromResult(new Collection<PSObject>());
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);
            Assert.Equal("1.1.1.1, 1.0.0.1", vm.CurrentDns);

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var restoreScripts = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Get-NetAdapter -IncludeHidden", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(2, restoreScripts.Count);
            Assert.Contains(OtherInterfaceGuid, restoreScripts[0]);
            Assert.Contains("9.9.9.9", restoreScripts[0]);
            Assert.DoesNotContain(TestInterfaceGuid, restoreScripts[0]);
            Assert.Contains(TestInterfaceGuid, restoreScripts[1]);
            Assert.Contains("8.8.8.8", restoreScripts[1]);
            Assert.DoesNotContain(OtherInterfaceGuid, restoreScripts[1]);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }


    [StaFact]
    public async Task InterleavedAmbiguousFailuresThenSuccessfulRetry_PreservesEveryAdapterRecovery()
    {
        var (vm, _, dir, runner) = NewVm();
        var captures = new[]
        {
            CaptureResult("8.8.8.8", 12, TestInterfaceGuid),
            CaptureResult("8.8.8.8", 12, TestInterfaceGuid),
            CaptureResult("9.9.9.9", 27, OtherInterfaceGuid),
            CaptureResult("9.9.9.9", 27, OtherInterfaceGuid),
            CaptureResult("203.0.113.53", 12, TestInterfaceGuid),
            CaptureResult("203.0.113.53", 12, TestInterfaceGuid),
        };
        var captureIndex = 0;
        var mutationCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                    return Task.FromResult(captures[captureIndex++]);

                if (script.Contains("$expectedSources", StringComparison.Ordinal))
                {
                    return Interlocked.Increment(ref mutationCount) <= 2
                        ? Task.FromException<Collection<PSObject>>(
                            new RuntimeException("Simulated ambiguous partial failure."))
                        : Task.FromResult(new Collection<PSObject>());
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            await vm.ApplyDnsCommand.ExecuteAsync(null);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var restoreScripts = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Get-NetAdapter -IncludeHidden", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(2, restoreScripts.Count);
            Assert.Contains(TestInterfaceGuid, restoreScripts[0]);
            Assert.Contains("8.8.8.8", restoreScripts[0]);
            Assert.DoesNotContain("203.0.113.53", restoreScripts[0]);
            Assert.Contains(OtherInterfaceGuid, restoreScripts[1]);
            Assert.Contains("9.9.9.9", restoreScripts[1]);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task SuccessfulChangesAfterCrossAdapterAmbiguity_PreserveUnresolvedRecovery()
    {
        var (vm, _, dir, runner) = NewVm();
        var captures = new[]
        {
            CaptureResult("8.8.8.8", 12, TestInterfaceGuid),
            CaptureResult("8.8.8.8", 12, TestInterfaceGuid),
            CaptureResult("9.9.9.9", 27, OtherInterfaceGuid),
            CaptureResult("9.9.9.9", 27, OtherInterfaceGuid),
            CaptureResult("1.1.1.1", 27, OtherInterfaceGuid),
            CaptureResult("1.1.1.1", 27, OtherInterfaceGuid),
        };
        var captureIndex = 0;
        var mutationCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                    return Task.FromResult(captures[captureIndex++]);

                if (script.Contains("$expectedSources", StringComparison.Ordinal))
                {
                    return Interlocked.Increment(ref mutationCount) == 1
                        ? Task.FromException<Collection<PSObject>>(
                            new RuntimeException("Simulated ambiguous partial failure."))
                        : Task.FromResult(new Collection<PSObject>());
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            await vm.ApplyDnsCommand.ExecuteAsync(null);

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Quad9");
            await vm.ApplyDnsCommand.ExecuteAsync(null);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var restoreScripts = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Get-NetAdapter -IncludeHidden", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(2, restoreScripts.Count);
            Assert.Contains(OtherInterfaceGuid, restoreScripts[0]);
            Assert.Contains("1.1.1.1", restoreScripts[0]);
            Assert.Contains(TestInterfaceGuid, restoreScripts[1]);
            Assert.Contains("8.8.8.8", restoreScripts[1]);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task SuccessfulLaterChange_DiscardsOlderUndoHistory()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        Interlocked.Increment(ref captureCount) <= 2
                            ? CaptureResult()
                            : DhcpCaptureResult());
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var mutations = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(3, mutations.Count);
            Assert.DoesNotContain("8.8.8.8", mutations[2]);
            Assert.DoesNotContain("192.0.2.53", mutations[2]);
            var mutationCount = mutations.Count;

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);

            Assert.Equal("No previous DNS to restore.", vm.StatusMessage);
            Assert.Equal(mutationCount, runner.ReceivedCalls().Count(call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true));
            dialog.Received(3).Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task CaptureFailureAndDecline_PreserveExistingUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    return Interlocked.Increment(ref captureCount) switch
                    {
                        1 or 2 => Task.FromResult(CaptureResult()),
                        3 => Task.FromResult(new Collection<PSObject>()),
                        _ => Task.FromResult(DhcpCaptureResult()),
                    };
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true, false, true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);
            Assert.Equal("DNS change cancelled.", vm.StatusMessage);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);
            Assert.False(vm.CanRestorePreviousDns);

            var mutations = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Where(script => script?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true)
                .ToList();

            Assert.Equal(2, mutations.Count);
            Assert.Contains("8.8.8.8", mutations[1]);
            dialog.Received(3).Confirm(Arg.Any<string>(), Arg.Any<string>());
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ApplyDns_WhenStateChangesAfterRevalidation_DiscardsPendingUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref captureCount);
                    return Task.FromResult(CaptureResult());
                }
                if (script.Contains("-AddressFamily IPv4", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("9.9.9.9"),
                    });
                }
                if (script.Contains("Set-DnsClientServerAddress", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("SYSMANAGER_DNS_PRECONDITION_FAILED"),
                    });
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed, or their current state could not be verified", vm.StatusMessage);
            Assert.False(vm.IsDnsApplying);
            Assert.Equal("9.9.9.9", vm.CurrentDns);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ResetDns_WhenStateChangesAfterRevalidation_DiscardsPendingUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref captureCount);
                    return Task.FromResult(CaptureResult());
                }
                if (script.Contains("-AddressFamily IPv4", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("4.4.4.4"),
                    });
                }
                if (script.Contains("-ResetServerAddresses", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("SYSMANAGER_DNS_PRECONDITION_FAILED"),
                    });
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, captureCount);
            Assert.False(vm.CanRestorePreviousDns);
            Assert.Contains("changed, or their current state could not be verified", vm.StatusMessage);
            Assert.False(vm.IsDnsApplying);
            Assert.Equal("4.4.4.4", vm.CurrentDns);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task PreMutationRejection_PreservesEarlierUndoInsteadOfHidingIt()
    {
        var (vm, _, dir, runner) = NewVm();
        var captureCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                {
                    var capture = Interlocked.Increment(ref captureCount) <= 2
                        ? CaptureResult()
                        : CaptureResult(ifIndex: 27, interfaceGuid: OtherInterfaceGuid);
                    return Task.FromResult(capture);
                }
                if (script.Contains("$expectedSources", StringComparison.Ordinal) &&
                    script.Contains("Set-DnsClientServerAddress", StringComparison.Ordinal) &&
                    !script.Contains("-ResetServerAddresses", StringComparison.Ordinal))
                {
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject("SYSMANAGER_DNS_PRECONDITION_FAILED"),
                    });
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);
            Assert.Contains("changed, or their current state could not be verified", vm.StatusMessage);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);

            Assert.False(vm.CanRestorePreviousDns);
            var restoreScript = runner.ReceivedCalls()
                .Select(call => call.GetArguments()[0] as string)
                .Last(script => script?.Contains("Get-NetAdapter -IncludeHidden", StringComparison.Ordinal) == true);
            Assert.Contains(TestInterfaceGuid, restoreScript);
            Assert.DoesNotContain(OtherInterfaceGuid, restoreScript);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }
    [StaFact]
    public async Task ApplyDns_WhenNoAdapterCanBeCaptured_OffersNoUndoAndDoesNotMutate()
    {
        var (vm, _, dir, runner) = NewVm();
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new Collection<PSObject>()));

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");

            await vm.ApplyDnsCommand.ExecuteAsync(null);

            Assert.False(vm.CanRestorePreviousDns);
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.DoesNotContain(runner.ReceivedCalls(), call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }


    [StaFact]
    public async Task RestorePreviousDns_WhenRestorePartiallyFails_RefreshesDisplayedDnsAndKeepsUndo()
    {
        var (vm, _, dir, runner) = NewVm();
        var queryCount = 0;
        runner.RunAsync(
                Arg.Any<string>(),
                Arg.Any<IDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var script = callInfo.ArgAt<string>(0);
                if (script.Contains("IFGUID=", StringComparison.Ordinal))
                    return Task.FromResult(CaptureResult());

                if (script.Contains("Get-NetAdapter -IncludeHidden", StringComparison.Ordinal))
                {
                    return Task.FromException<Collection<PSObject>>(
                        new RuntimeException("Simulated failure after reset started."));
                }

                if (script.Contains("-AddressFamily IPv4", StringComparison.Ordinal))
                {
                    var current = Interlocked.Increment(ref queryCount) == 1
                        ? "1.1.1.1"
                        : "4.4.4.4";
                    return Task.FromResult(new Collection<PSObject>
                    {
                        PSObject.AsPSObject(current),
                    });
                }

                return Task.FromResult(new Collection<PSObject>());
            });

        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SelectedPreset = vm.Presets.First(p => p.Name == "Cloudflare");
            await vm.ApplyDnsCommand.ExecuteAsync(null);
            Assert.Equal("1.1.1.1", vm.CurrentDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);

            Assert.Equal(2, queryCount);
            Assert.Equal("4.4.4.4", vm.CurrentDns);
            Assert.True(vm.CanRestorePreviousDns);
            Assert.Contains("Failed to restore DNS", vm.StatusMessage);
            Assert.False(vm.IsDnsApplying);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }
    [StaFact]
    public async Task RestorePreviousDns_WhenUserDeclines_KeepsUndoAndDoesNotMutateAgain()
    {
        var (vm, _, dir, runner) = NewVm();
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CaptureResult()));
        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true, false);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);
            Assert.True(vm.CanRestorePreviousDns);

            await vm.RestorePreviousDnsCommand.ExecuteAsync(null);

            Assert.True(vm.CanRestorePreviousDns);
            Assert.Equal("DNS restore cancelled.", vm.StatusMessage);
            Assert.Equal(1, runner.ReceivedCalls().Count(call =>
                (call.GetArguments()[0] as string)?.Contains(
                    "Set-DnsClientServerAddress", StringComparison.Ordinal) == true));
            dialog.Received(2).Confirm(Arg.Any<string>(), Arg.Any<string>());
            dialog.Received(1).Confirm(
                Arg.Is<string>(message => message != null &&
                    message.Contains("previously changed network adapter") &&
                    !message.Contains("interface 12")),
                "Confirm DNS Restore");
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ResetDns_WhenUserDeclinesCapturedTarget_DoesNotMutate()
    {
        var (vm, _, dir, runner) = NewVm();
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CaptureResult()));
        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(
                Arg.Is<string>(message => message != null &&
                    message.Contains("interface 12")),
                "Confirm DNS Reset");
            await runner.DidNotReceive().RunAsync(
                Arg.Is<string>(script => script != null &&
                    script.Contains("Set-DnsClientServerAddress")),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
            Assert.Equal("DNS reset cancelled.", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
        }
    }

    [StaFact]
    public async Task ResetDns_DhcpWithEffectiveAddress_ConfirmationDescribesAutomaticMode()
    {
        var (vm, _, dir, runner) = NewVm();
        runner.RunAsync(
                Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(DhcpCaptureResult()));
        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            await vm.ResetDnsCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(
                Arg.Is<string>(message => message != null &&
                    message.Contains("IPv4 automatic (DHCP)") &&
                    !message.Contains("192.0.2.53")),
                "Confirm DNS Reset");
        }
        finally
        {
            DialogService.Instance = previousDialog;
            DeleteTestDirectory(dir);
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
            DeleteTestDirectory(dir);
        }
    }
}
