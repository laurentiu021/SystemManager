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
    public async Task ToggleFeature_MovesTheBar_WhileItRuns()
    {
        // It set IsBusy alone, so the footer's bar sat empty and still through a DISM run of minutes (#2812).
        var dism = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(dism.Task);
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(runner, NoRestorePoint());

        var toggle = vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });
        try
        {
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsProgressIndeterminate);
        }
        finally
        {
            // Ended either way: a change left running holds the lock every later toggle in this class needs.
            dism.SetResult(0);
            await toggle;
        }

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
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
    public async Task ToggleFeature_SaysWhatTheRestorePointDoesToSystemProtection()
    {
        // #2483. The toggle takes the shared restore point first, which turns System Protection back on when it
        // is off, and this confirmation never said so.
        var rp = NoRestorePoint();
        rp.ConfirmationNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        using var dialog = new DialogAnswer(confirm: false);
        var vm = ElevatedVm(RunnerThatSucceeds(), rp);

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleFeature_WhileAnotherSystemChangeRuns_ChangesNothing()
    {
        // #2484. A feature change is DISM servicing of the running image, and it could run in the middle of an
        // SFC or DISM repair SysManager itself had started. Refused before the restore point too, which is a
        // system change of its own.
        var runner = RunnerThatSucceeds();
        var rp = RestorePointTaken();
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(runner, rp);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(held);

        await vm.ToggleFeatureCommand.ExecuteAsync(
            new WindowsFeature { Name = "TelnetClient", DisplayName = "Telnet Client" });

        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
        await rp.DidNotReceiveWithAnyArgs().EnsureAsync(default!, default);
        Assert.Equal("Cannot start — SFC scan is already running.", vm.StatusMessage);
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

    // ── A refused scan is a failure, not "Found 0 features" (#2451) ────────
    //
    // Get-WindowsOptionalFeature -Online refuses an unelevated caller: it prints nothing and exits 1 (measured).
    // The exit code was discarded, so the empty output parsed to an empty list and the tab reported a finished
    // scan. A normal launch is unelevated.

    private static IPowerShellRunner RunnerThatExits(int exitCode)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(Task.FromResult(exitCode));
        return runner;
    }

    [Fact]
    public async Task ListFeatures_WhenTheQueryFails_Throws_InsteadOfReturningAnEmptyList()
    {
        var service = new WindowsFeaturesService(RunnerThatExits(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListFeaturesAsync());
    }

    [Fact]
    public async Task Scan_Unelevated_WhenWindowsRefusesTheQuery_SaysItNeedsAdministrator_NotFoundZero()
    {
        var vm = new WindowsFeaturesViewModel(new WindowsFeaturesService(RunnerThatExits(1)), NoRestorePoint())
        {
            IsElevated = false,
        };

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal(WindowsFeaturesViewModel.ListNeedsAdminMessage, vm.StatusMessage);
        Assert.DoesNotContain("Found 0", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_Elevated_WhenTheQueryStillFails_ReportsTheFailure()
    {
        var vm = ElevatedVm(RunnerThatExits(5), NoRestorePoint());

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.StartsWith("Error: Windows could not list the optional features", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_WhenTheQuerySucceeds_ReportsWhatItFound()
    {
        // The success half, so a scan that always "fails" cannot pass the pair above.
        var runner = RunnerThatExits(0);
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("TelnetClient|Disabled"));
                  runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("NetFx3|Enabled"));
                  return Task.FromResult(0);
              });
        var vm = ElevatedVm(runner, NoRestorePoint());

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Equal("Found 2 features (1 enabled).", vm.StatusMessage);
    }

    // ── What Windows answered, and what the row shows afterwards (#2834, #2739) ──
    //
    // The change scripts printed nothing when the cmdlet refused, so every failure said "Check permissions". The row
    // was written from the request rather than read back, and a switch the change did not move stayed turned over.

    /// <summary>
    /// A runner that answers the change script with <paramref name="changeExit"/> and <paramref name="changeLines"/>,
    /// and the read-back of the feature with <paramref name="readLine"/>, or with nothing when it is null.
    /// </summary>
    private static IPowerShellRunner FakeDism(int changeExit, string[] changeLines, string? readLine)
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  var read = call.ArgAt<string>(1).Contains("Get-WindowsOptionalFeature", StringComparison.Ordinal);
                  string[] lines = read ? (readLine is null ? [] : [readLine]) : changeLines;
                  foreach (var line in lines)
                      runner.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(line));
                  return Task.FromResult(read ? 0 : changeExit);
              });
        return runner;
    }

    private static string Refused(string hexCode, string message) =>
        $"{WindowsFeaturesService.FailureMarker}|{hexCode}|{message}";

    private static WindowsFeature Telnet(bool enabled = false) =>
        new() { Name = "TelnetClient", DisplayName = "Telnet Client", IsEnabled = enabled };

    [Fact]
    public async Task ToggleFeature_WhenWindowsCannotGetTheFilesItNeeds_SaysSo_NotCheckPermissions()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(
            FakeDism(1, [Refused("800F081F", "The source files could not be found.")], "TelnetClient|Disabled"),
            NoRestorePoint());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.Equal("Failed to enable Telnet Client (0x800F081F). Windows could not find the files it needs. It gets "
                     + "them from Windows Update, so check that this PC is online and that Windows Update works, "
                     + "then try again.", vm.StatusMessage);
        Assert.Equal("Failed", feature.Status);
    }

    [Fact]
    public async Task ToggleFeature_WhenDismRefusesForAnotherReason_SaysDismsOwnReasonWithItsCode()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(
            FakeDism(1, [Refused("800F0922", "Processing of advanced installers and generic commands failed.")],
                "TelnetClient|Disabled"),
            NoRestorePoint());

        await vm.ToggleFeatureCommand.ExecuteAsync(Telnet());

        Assert.Equal("Failed to enable Telnet Client (0x800F0922). Processing of advanced installers and generic "
                     + "commands failed.", vm.StatusMessage);
    }

    [Theory]
    [InlineData(0x800F0906u, "Windows could not download the files it needs from Windows Update.")]
    [InlineData(0x800F081Fu, "Windows could not find the files it needs.")]
    [InlineData(0x800F0907u, "A Windows policy on this PC stops it from downloading the files it needs")]
    [InlineData(0x80070422u, "A Windows service it needs is turned off")]
    public void DescribeFailure_ACodeWithAKnownWayOut_GetsItsOwnSentence_AndKeepsTheCode(uint code, string sentence)
    {
        var said = WindowsFeaturesViewModel.DescribeFailure("enable", "NetFx3",
            new FeatureChangeResult(false, false, unchecked((int)code), "DISM's own words"));

        Assert.StartsWith($"Failed to enable NetFx3 (0x{code:X8}). {sentence}", said, StringComparison.Ordinal);
        Assert.DoesNotContain("DISM's own words", said, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_WithNothingFromWindows_SaysWindowsDidNotSayWhy()
    {
        Assert.Equal("Failed to disable NetFx3. Windows did not say why.",
            WindowsFeaturesViewModel.DescribeFailure("disable", "NetFx3", new FeatureChangeResult(false, false)));
        Assert.Equal("Failed to disable NetFx3 (0x800F0922).",
            WindowsFeaturesViewModel.DescribeFailure("disable", "NetFx3",
                new FeatureChangeResult(false, false, unchecked((int)0x800F0922u))));
    }

    [Fact]
    public async Task ToggleFeature_ReadsTheFeatureBack_SoAChangeWaitingForARestartShowsAsOn()
    {
        // DISM said no restart was needed, and Windows still reports the feature as waiting for one.
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(FakeDism(0, ["False"], "TelnetClient|EnablePending"), NoRestorePoint());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.True(feature.IsEnabled);
        Assert.True(feature.RequiresReboot);
        Assert.Equal("Reboot required", feature.Status);
        Assert.True(vm.PendingReboot);
        Assert.Equal("Telnet Client enabled — reboot required.", vm.StatusMessage);
    }

    [Fact]
    public async Task ToggleFeature_WhenTheChangeFailsAfterDoingItsWork_TheRowShowsWhatWindowsReports()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(
            FakeDism(1, [Refused("800F0922", "Processing of advanced installers and generic commands failed.")],
                "TelnetClient|Enabled"),
            NoRestorePoint());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.True(feature.IsEnabled);
        Assert.Equal("Failed", feature.Status);
        Assert.StartsWith("Failed to enable Telnet Client (0x800F0922).", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleFeature_WhenAFailedChangeLeftARestartPending_SaysARestartIsPending()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(
            FakeDism(1, [Refused("800F0922", "Processing of advanced installers and generic commands failed.")],
                "TelnetClient|EnablePending"),
            NoRestorePoint());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.True(feature.RequiresReboot);
        Assert.True(vm.PendingReboot);
        Assert.Equal("Failed", feature.Status);
    }

    [Fact]
    public async Task ToggleFeature_WhenWindowsReportsSuccessButTheFeatureDidNotMove_SaysSo()
    {
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(FakeDism(0, ["False"], "TelnetClient|Disabled"), RestorePointTaken());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.False(feature.IsEnabled);
        Assert.Equal("Windows reported Telnet Client as enabled, but it still shows as disabled. Scan again to check.",
            vm.StatusMessage);
    }

    [Fact]
    public async Task ToggleFeature_WhenTheReadBackFails_GoesByWhatDismSaid()
    {
        // The read-back prints nothing, so it finds no feature, and the row goes by DISM's answer, as it always did.
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(FakeDism(0, ["True"], readLine: null), NoRestorePoint());
        var feature = Telnet();

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.True(feature.IsEnabled);
        Assert.Equal("Reboot required", feature.Status);
        Assert.Equal("Telnet Client enabled — reboot required.", vm.StatusMessage);
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("locked")]
    [InlineData("not elevated")]
    [InlineData("refused")]
    public async Task ToggleFeature_WhenTheChangeDoesNotHappen_PutsTheSwitchBack(string why)
    {
        // The switch had turned itself over on the click; only a change notification for IsEnabled turns it back.
        using var dialog = new DialogAnswer(confirm: why != "declined");
        var vm = ElevatedVm(
            FakeDism(1, [Refused("800F081F", "The source files could not be found.")], "TelnetClient|Disabled"),
            NoRestorePoint());
        vm.IsElevated = why != "not elevated";
        using var held = why == "locked"
            ? OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan")
            : null;
        var feature = Telnet();
        var told = 0;
        feature.PropertyChanged += (_, e) => told += e.PropertyName == nameof(WindowsFeature.IsEnabled) ? 1 : 0;

        await vm.ToggleFeatureCommand.ExecuteAsync(feature);

        Assert.False(feature.IsEnabled);
        Assert.True(told >= 1, $"the switch was not told to read the state again after the change was {why}");
    }

    [Fact]
    public async Task Escape_StopsAScan_ButNotAChange()
    {
        var listing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken changeToken = default;
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  var script = call.ArgAt<string>(1);
                  if (script.Contains("Enable-WindowsOptionalFeature", StringComparison.Ordinal))
                  {
                      changeToken = call.ArgAt<CancellationToken>(2);
                      return changing.Task;
                  }
                  return script.Contains("-FeatureName", StringComparison.Ordinal) ? Task.FromResult(0) : listing.Task;
              });
        using var dialog = new DialogAnswer(confirm: true);
        var vm = ElevatedVm(runner, NoRestorePoint());

        var scan = vm.ScanCommand.ExecuteAsync(null);
        Assert.Same(vm.CancelCommand, vm.EscapeCancel);
        listing.SetResult(0);
        await scan;
        Assert.Null(vm.EscapeCancel);

        var toggle = vm.ToggleFeatureCommand.ExecuteAsync(Telnet());
        try
        {
            Assert.True(vm.IsBusy);
            Assert.False(vm.IsScanning);
            Assert.Null(vm.EscapeCancel);
            vm.CancelCommand.Execute(null);
            Assert.False(changeToken.IsCancellationRequested);
        }
        finally
        {
            // Ended either way: a change left running holds the lock every later toggle in this class needs.
            changing.SetResult(0);
            await toggle;
        }
    }

    [Fact]
    public async Task Scan_WhenAFeatureWaitsForARestart_ShowsItOn_AndThatARestartIsPending()
    {
        var runner = RunnerThatExits(0);
        runner.RunProcessAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                               Arg.Any<System.Text.Encoding?>())
              .Returns(call =>
              {
                  runner.LineReceived +=
                      Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("TelnetClient|EnablePending"));
                  return Task.FromResult(0);
              });
        var vm = ElevatedVm(runner, NoRestorePoint());

        await vm.ScanCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.AllFeatures);
        Assert.True(row.IsEnabled);
        Assert.Equal("Reboot required", row.Status);
        Assert.True(vm.PendingReboot);
    }

    // ── the service's half ──

    [Theory]
    [InlineData("Enabled", true, false)]
    [InlineData("EnablePending", true, true)]
    [InlineData("DisablePending", false, true)]
    [InlineData("Disabled", false, false)]
    [InlineData("DisabledWithPayloadRemoved", false, false)]
    public void ParseFeatureList_ReadsAStateWaitingForARestart(string state, bool enabled, bool pending)
    {
        var row = Assert.Single(WindowsFeaturesService.ParseFeatureList([$"TelnetClient|{state}"]));

        Assert.Equal(enabled, row.IsEnabled);
        Assert.Equal(pending, row.RequiresReboot);
        Assert.Equal(pending ? "Reboot required" : enabled ? "Enabled" : "Disabled", row.Status);
    }

    [Fact]
    public void ParseChange_ReadsDismsCodeAndTheFirstLineOfItsMessage()
    {
        var result = WindowsFeaturesService.ParseChange(1,
            ["", Refused("800F081F", "The source files could not be found.")]);

        Assert.False(result.Success);
        Assert.Equal(unchecked((int)0x800F081Fu), result.ErrorCode);
        Assert.Equal("The source files could not be found.", result.ErrorMessage);
    }

    [Fact]
    public void ParseChange_ReadsTheRestartFlagOfAChangeThatWentThrough()
    {
        Assert.Equal(new FeatureChangeResult(true, true), WindowsFeaturesService.ParseChange(0, ["True"]));
        Assert.Equal(new FeatureChangeResult(true, false), WindowsFeaturesService.ParseChange(0, ["False"]));
    }

    [Fact]
    public void ParseChange_AFailureThatPrintedNoReason_HasNoCode()
    {
        Assert.Equal(new FeatureChangeResult(false, false), WindowsFeaturesService.ParseChange(1, ["something else"]));
        Assert.Equal(new FeatureChangeResult(false, false),
            WindowsFeaturesService.ParseChange(1, [Refused("not hex", "x")]));
    }

    [Fact]
    public void ChangeScript_EnablingTurnsOnTheParentsItNeeds_DisablingTouchesOnlyTheFeature()
    {
        Assert.Contains("Enable-WindowsOptionalFeature -Online -FeatureName 'TelnetClient' -NoRestart -All ",
            WindowsFeaturesService.ChangeScript(enable: true, "TelnetClient"), StringComparison.Ordinal);
        Assert.Contains("Disable-WindowsOptionalFeature -Online -FeatureName 'TelnetClient' -NoRestart -ErrorAction",
            WindowsFeaturesService.ChangeScript(enable: false, "TelnetClient"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TelnetClient'; Remove-Item C:\\x; '")]
    [InlineData("Telnet Client")]
    [InlineData("TelnetClient\n")]
    [InlineData("")]
    public async Task EveryCallThatPutsTheNameInAScript_RefusesANameThatIsNotOne(string name)
    {
        // The pattern is the only thing between the name and the script it is pasted into.
        var runner = Substitute.For<IPowerShellRunner>();
        var service = new WindowsFeaturesService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.EnableFeatureAsync(name));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DisableFeatureAsync(name));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadFeatureAsync(name));
        await runner.DidNotReceiveWithAnyArgs().RunProcessAsync(default!, default!, default, default);
    }
}
