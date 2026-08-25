// SysManager · WindowsFeaturesTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="WindowsFeaturesService"/> and <see cref="WindowsFeaturesViewModel"/>.
/// </summary>
// Serialized: the restore-point tests swap the static DialogService.Instance via DialogAnswer,
// which is process-wide shared state.
[Collection("ProcessWideStatics")]
public class WindowsFeaturesTests
{
    // ── ParseFeatureList ──

    [Fact]
    public void ParseFeatureList_EmptyInput_ReturnsEmpty()
    {
        var result = WindowsFeaturesService.ParseFeatureList(new List<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void ParseFeatureList_ValidLines_ParsesCorrectly()
    {
        var lines = new List<string>
        {
            "Microsoft-Hyper-V-All|Enabled",
            "TelnetClient|Disabled",
            "NetFx3|Enabled"
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);

        Assert.Equal(3, result.Count);
        var hyperV = result.First(f => f.Name == "Microsoft-Hyper-V-All");
        Assert.True(hyperV.IsEnabled);
        Assert.Equal("Virtualization", hyperV.Category);

        var telnet = result.First(f => f.Name == "TelnetClient");
        Assert.False(telnet.IsEnabled);
        Assert.Equal("Networking", telnet.Category);
    }

    [Fact]
    public void ParseFeatureList_SkipsBlankLines()
    {
        var lines = new List<string>
        {
            "",
            "  ",
            "SomeFeature|Disabled",
            ""
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);
        Assert.Single(result);
    }

    [Fact]
    public void ParseFeatureList_SkipsInvalidLines()
    {
        var lines = new List<string>
        {
            "NoSeparator",
            "|Enabled",
            "ValidFeature|Disabled"
        };

        var result = WindowsFeaturesService.ParseFeatureList(lines);
        Assert.Single(result);
        Assert.Equal("ValidFeature", result[0].Name);
    }

    // ── CategorizeFeature ──

    [Theory]
    [InlineData("Microsoft-Hyper-V-All", "Virtualization")]
    [InlineData("VirtualMachinePlatform", "Virtualization")]
    [InlineData("Microsoft-Windows-Subsystem-Linux", "Virtualization")]
    [InlineData("Containers", "Virtualization")]
    [InlineData("Windows-Sandbox", "Virtualization")]
    [InlineData("TelnetClient", "Networking")]
    [InlineData("IIS-WebServer", "Networking")]
    [InlineData("SMB1Protocol", "Networking")]
    [InlineData("NetFx3", "Development")]
    [InlineData("Microsoft-Windows-Developer-Mode", "Development")]
    [InlineData("OpenSSH-Client", "Development")]
    [InlineData("MediaPlayback", "Media & Print")]
    [InlineData("Printing-XPSServices-Features", "Media & Print")]
    [InlineData("DirectPlay", "Legacy")]
    [InlineData("WorkFolders-Client", "Legacy")]
    // Regression: a bare "WORK" substring used to drop any "...work..." feature into
    // Legacy. An unknown feature that merely contains "work" must NOT be Legacy now.
    [InlineData("SomeFrameworkThing", "Other")]
    [InlineData("SomeRandomFeature", "Other")]
    public void CategorizeFeature_AssignsCorrectCategory(string featureName, string expected)
    {
        Assert.Equal(expected, WindowsFeature.CategorizeFeature(featureName));
    }

    [Fact]
    public void CategorizeFeature_NullOrEmpty_ReturnsOther()
    {
        Assert.Equal("Other", WindowsFeature.CategorizeFeature(null!));
        Assert.Equal("Other", WindowsFeature.CategorizeFeature(""));
        Assert.Equal("Other", WindowsFeature.CategorizeFeature("   "));
    }

    // ── HumanizeName ──

    [Theory]
    [InlineData("Microsoft-Hyper-V-All", "Microsoft Hyper V All")]
    [InlineData("TelnetClient", "TelnetClient")]
    [InlineData("Some_Feature_Name", "Some Feature Name")]
    public void HumanizeName_ReplacesDelimiters(string input, string expected)
    {
        Assert.Equal(expected, WindowsFeaturesService.HumanizeName(input));
    }

    // ── ViewModel ──

    [Fact]
    public void ViewModel_InitialState_IsCorrect()
    {
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(new PowerShellRunner()), NoRestorePoint());
        Assert.Empty(vm.AllFeatures);
        Assert.Empty(vm.FilteredFeatures);
        Assert.Equal("", vm.FilterText);
        Assert.Equal(0, vm.FeatureCount);
        Assert.False(vm.PendingReboot);
    }

    // ── re-entrancy guard (regression: shared runner cross-contamination) ──

    [Fact]
    public void ViewModel_ScanAndToggle_DisabledWhileBusy()
    {
        // Scan and ToggleFeature both drive the shared WindowsFeaturesService PowerShell
        // runner. Running them concurrently would let both LineReceived handlers capture
        // the same output. The NotBusy/CanToggle gates make them mutually exclusive.
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(new PowerShellRunner()), NoRestorePoint());
        var feature = new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" };

        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.ToggleFeatureCommand.CanExecute(feature));

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.ToggleFeatureCommand.CanExecute(feature));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.ToggleFeatureCommand.CanExecute(feature));
    }

    // ── session restore point (#1966) ──────────────────────────────────────
    // The last tab making a servicing-level change with no snapshot. It matters more here than
    // anywhere else covered so far: the app cannot undo a DISM toggle by flipping the same switch
    // (re-enabling is a second servicing operation that can itself fail, possibly with a reboot
    // pending), and unlike removing a Store app, a restore point genuinely DOES cover this state.

    private static ISessionRestorePoint NoRestorePoint()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return rp;
    }

    private static ISessionRestorePoint RestorePointTaken()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return rp;
    }

    /// <summary>
    /// A runner whose powershell.exe invocation reports success (exit 0) without launching anything,
    /// so the toggle path can be exercised without a real DISM servicing operation.
    /// </summary>
    private static IPowerShellRunner RunnerThatSucceeds()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(Task.FromResult(0));
        return runner;
    }

    private static WindowsFeaturesViewModel ElevatedVm(IPowerShellRunner runner, ISessionRestorePoint rp)
    {
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(runner), rp);
        vm.IsElevated = true;   // the command refuses outright without elevation
        return vm;
    }

    [Fact]
    public async Task ToggleFeature_TakesTheRestorePointBeforeTouchingTheFeature()
    {
        var rp = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(RunnerThatSucceeds(), rp);

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        await rp.Received(1).EnsureAsync("SysManager Windows Features", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleFeature_WhenTheUserDeclines_TakesNoRestorePoint()
    {
        // Declining changes nothing, so it must not spend the one point Windows grants per day.
        var rp = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: false);
        var vm = ElevatedVm(RunnerThatSucceeds(), rp);

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        await rp.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToggleFeature_WithoutElevation_TakesNoRestorePoint()
    {
        // The command refuses before the confirmation when not elevated. Creating a point there would
        // burn the daily allowance on an operation that was never going to run.
        var rp = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: true);
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(RunnerThatSucceeds()), rp);
        vm.IsElevated = false;

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        await rp.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("Administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToggleFeature_WhenAPointWasCreated_SaysSoAfterTheRebootNote()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(RunnerThatSucceeds(), RestorePointTaken());

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        Assert.Contains("Telnet Client enabled", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Restore point created.", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleFeature_WhenNoPointWasCreated_ClaimsNone()
    {
        // System Restore is off on many consumer PCs, so this is the common case — and a safety net
        // the user does not have is worse than none, because she toggles the feature believing in it.
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(RunnerThatSucceeds(), NoRestorePoint());

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        Assert.Contains("Telnet Client enabled", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("restore point", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToggleFeature_WhenTheToggleFails_ClaimsNoRestorePointEvenThoughOneWasMade()
    {
        // DISM refused (non-zero exit). A point really was created, but saying so beside "Failed to
        // enable" reads as though something happened that could be rolled back. Nothing happened.
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(Task.FromResult(1));
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(runner, RestorePointTaken());

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        Assert.Contains("Failed to enable", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("restore point", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
