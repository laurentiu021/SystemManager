// SysManager · DefenderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;
using Xunit;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="DefenderViewModel"/> — focuses on the re-entrancy guard
/// (NotBusy) that serialises the mutating Defender commands so two overlapping
/// Set-MpPreference operations can't race the read-back verification.
/// </summary>
[Collection("ProcessWideStatics")]
public sealed class DefenderViewModelTests : IDisposable
{
    // Pinned, not read from the host: every change refuses to start without administrator rights (#2730), so a test
    // that read the host would pass only where the suite runs elevated. A test about a standard user forces that
    // inside its own scope.
    private readonly IDisposable _elevated = AdminHelper.ForceElevation(true);

    public void Dispose() => _elevated.Dispose();

    /// <summary>
    /// A seam that answers "no point was created" — the common case, since System Restore ships
    /// disabled on many consumer PCs and Windows grants roughly one point per 24 hours.
    /// </summary>
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
    /// A view model over a status that was read, PUA and Controlled Folder Access off, without touching real
    /// PowerShell or Defender. A status nobody read refuses every change, so it has tests of its own.
    /// </summary>
    private static DefenderViewModel NewVm(IDialogService? dialog = null)
    {
        var service = new DefenderService(RunnerReportingPua(0), checksTheSignedInAccountsFolders: true);
        var vm = new DefenderViewModel(service, NoRestorePoint(), dialog);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>A runner every read of which fails to return Defender's status.</summary>
    private static IPowerShellRunner RunnerThatReadsNothing()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>());
        return ps;
    }

    /// <summary>
    /// A runner whose first read returns PUA at <paramref name="pua"/>, and which returns nothing after that: the
    /// change runs, and the read after it fails.
    /// </summary>
    private static IPowerShellRunner RunnerReadingOnceThenNothing(int pua)
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", pua));
        var ps = Substitute.For<IPowerShellRunner>();
        var calls = 0;
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => ++calls == 1 ? new Collection<PSObject> { projection } : new Collection<PSObject>());
        return ps;
    }

    [Fact]
    public void AfterInit_NotBusy_IsTrue()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
        Assert.True(vm.NotBusy);
    }

    [Fact]
    public void MutatingCommands_AreGatedOnNotBusy()
    {
        var vm = NewVm();

        // While not busy, the always-available mutating commands can run.
        Assert.True(vm.TogglePuaCommand.CanExecute(null));
        Assert.True(vm.ToggleCfaCommand.CanExecute(null));
        Assert.True(vm.AddExclusionCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));

        // Simulate an in-flight operation: every mutating command must refuse to start.
        vm.IsBusy = true;
        Assert.False(vm.NotBusy);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
        Assert.False(vm.ToggleCfaCommand.CanExecute(null));
        Assert.False(vm.AddExclusionCommand.CanExecute(null));
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));

        // Clearing busy re-enables them.
        vm.IsBusy = false;
        Assert.True(vm.TogglePuaCommand.CanExecute(null));
    }

    [Fact]
    public void RemoveExclusion_RequiresBothSelectionAndNotBusy()
    {
        var vm = NewVm();

        // No selection -> cannot remove even when idle.
        Assert.Null(vm.SelectedExclusion);
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));

        // With a selection and idle -> can remove.
        vm.SelectedExclusion = @"C:\some\folder";
        Assert.True(vm.RemoveExclusionCommand.CanExecute(null));

        // Busy overrides the selection -> cannot remove.
        vm.IsBusy = true;
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));
    }

    /// <summary>
    /// When the read after a change fails, the tab says it cannot tell what happened, and claims neither that the
    /// change worked nor that it did not.
    /// </summary>
    /// <remarks>
    /// A disable targets PUA 0, and the status of a read that failed carries 0 too, so it must never count as
    /// "updated". It said "was not changed: Windows did not apply it" instead, a claim from a read that failed,
    /// about a change that may well have been made.
    /// </remarks>
    [Fact]
    public async Task AGoodReadThenAnUnreadReadBack_SaysTheOutcomeIsUnknown()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReadingOnceThenNothing(pua: 1)), NoRestorePoint());
        await vm.InitializationComplete;
        Assert.True(vm.PuaEnabled);   // so the toggle asks for off, the dangerous case
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Equal("SysManager could not read Defender's settings back, so it cannot tell whether it managed to "
            + "change PUA protection. Press Refresh to see.", vm.StatusMessage);
        Assert.Equal("—", vm.PuaDisplay);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
    }

    /// <summary>A read after the change that faults leaves the same unknown, never the cards from before it.</summary>
    [Fact]
    public async Task AReadBackThatFaults_AfterTheChange_DropsTheCards_AndTurnsTheChangesOff()
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", 1));
        var ps = Substitute.For<IPowerShellRunner>();
        var calls = 0;
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => ++calls switch
          {
              1 => new Collection<PSObject> { projection },
              2 => new Collection<PSObject>(),
              _ => throw new System.InvalidOperationException("runspace is broken"),
          });
        var vm = new DefenderViewModel(new DefenderService(ps), NoRestorePoint());
        await vm.InitializationComplete;
        Assert.Equal("Enabled", vm.PuaDisplay);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Contains("cannot tell whether it managed to change PUA protection", vm.StatusMessage,
            StringComparison.Ordinal);
        Assert.Equal("—", vm.PuaDisplay);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
    }

    // ---------- a status nobody read ----------
    // A failed read used to show as fact: real-time "Off", PUA and Controlled Folder Access "Disabled", "No exclusion
    // folders", and toggles that asked "Enable …?" from the zeros of a status nobody read.

    [Fact]
    public async Task AFailedRead_TheExclusionsCardSaysTheyCouldNotBeRead_AndTheStatusLineSaysWhy()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerThatReadsNothing()), NoRestorePoint());
        await vm.InitializationComplete;

        Assert.False(vm.IsAvailable);
        Assert.Equal("Exclusions could not be read", vm.ExclusionsEmptyTitle);
        Assert.DoesNotContain("Add a folder", vm.ExclusionsEmptyMessage, StringComparison.Ordinal);
        Assert.StartsWith("SysManager could not read Microsoft Defender's status", vm.StatusMessage,
            StringComparison.Ordinal);
        Assert.Equal(("—", "—", "—", "—"), (vm.RealtimeDisplay, vm.MapsDisplay, vm.PuaDisplay, vm.CfaDisplay));
    }

    [Fact]
    public async Task AFailedRead_TurnsEveryChangeOff_ButNotRefresh()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerThatReadsNothing()), NoRestorePoint());
        await vm.InitializationComplete;
        vm.SelectedExclusion = @"C:\Games";

        Assert.False(vm.TogglePuaCommand.CanExecute(null));
        Assert.False(vm.ToggleCfaCommand.CanExecute(null));
        Assert.False(vm.AddExclusionCommand.CanExecute(null));
        Assert.False(vm.RemoveExclusionCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    /// <summary>
    /// ExecuteAsync runs a command's body whatever CanExecute says, so each change refuses in its body too, before any
    /// question, folder picker or snapshot.
    /// </summary>
    [Theory]
    [InlineData("TogglePua", "Changing PUA protection")]
    [InlineData("ToggleCfa", "Changing Controlled Folder Access")]
    [InlineData("AddExclusion", "Adding an exclusion")]
    [InlineData("RemoveExclusion", "Removing an exclusion")]
    public async Task AfterAFailedRead_EveryChange_SaysTheStatusIsNeeded_BeforeAskingAnything(
        string change, string what)
    {
        var folder = Directory.CreateTempSubdirectory("smtest_defender_").FullName;
        try
        {
            var ps = RunnerThatReadsNothing();
            var restorePoint = RestorePointTaken();
            var picker = Substitute.For<IDialogService>();
            picker.PickFolder(Arg.Any<string>()).Returns(folder);
            var vm = new DefenderViewModel(new DefenderService(ps), restorePoint, picker);
            await vm.InitializationComplete;
            vm.SelectedExclusion = @"C:\Games";
            using var confirm = new DialogAnswer(confirm: true);

            var command = change switch
            {
                "TogglePua" => vm.TogglePuaCommand,
                "ToggleCfa" => vm.ToggleCfaCommand,
                "AddExclusion" => vm.AddExclusionCommand,
                _ => vm.RemoveExclusionCommand,
            };
            await command.ExecuteAsync(null);

            Assert.Equal($"{what} needs Defender's status, and SysManager has not been able to read it. Press Refresh "
                + "to try again.", vm.StatusMessage);
            Assert.Equal(0, confirm.Calls);
            picker.DidNotReceive().PickFolder(Arg.Any<string>());
            await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await ps.DidNotReceive().RunAsync(Arg.Is<string>(script => script != DefenderService.StatusScript),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ARunspaceFaultOnTheFirstRead_IsAFailedRead_NotAnEmptyDefender()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns<Collection<PSObject>>(_ => throw new System.InvalidOperationException("runspace is broken"));
        var vm = new DefenderViewModel(new DefenderService(ps), NoRestorePoint());
        await vm.InitializationComplete;

        Assert.False(vm.IsAvailable);
        Assert.Equal("Exclusions could not be read", vm.ExclusionsEmptyTitle);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
        Assert.Equal("Could not read Defender status: runspace is broken", vm.StatusMessage);
    }

    /// <summary>A Refresh that fails after a good read blanks the cards rather than leaving the old read up.</summary>
    [Fact]
    public async Task ARefreshThatFails_AfterAGoodRead_BlanksTheCards()
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", 1));
        var ps = Substitute.For<IPowerShellRunner>();
        var calls = 0;
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => ++calls == 1
              ? new Collection<PSObject> { projection }
              : throw new System.InvalidOperationException("runspace is broken"));
        var vm = new DefenderViewModel(new DefenderService(ps), NoRestorePoint());
        await vm.InitializationComplete;
        Assert.Equal("Enabled", vm.PuaDisplay);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("—", vm.PuaDisplay);
        Assert.False(vm.IsAvailable);
        Assert.False(vm.TogglePuaCommand.CanExecute(null));
    }

    /// <summary>Until the first read is in, nothing has been read either, so a change asks nothing.</summary>
    [Fact]
    public async Task WhileTheFirstReadIsStillGoing_AChangeAsksNothing()
    {
        var first = new TaskCompletionSource<Collection<PSObject>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(first.Task);
        var restorePoint = RestorePointTaken();
        var vm = new DefenderViewModel(new DefenderService(ps), restorePoint);
        using var confirm = new DialogAnswer(confirm: false);

        await vm.TogglePuaCommand.ExecuteAsync(null);
        first.SetResult(new Collection<PSObject>());
        await vm.InitializationComplete;

        Assert.Equal(0, confirm.Calls);
        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---------- the modes between off and fully on ----------
    // PUA has an audit mode (2), and Controlled Folder Access has audit (2) and two modes that cover only changes to
    // the disk itself (3 blocks them, 4 audits them). The toggles counted only 1 as on, so from any of those they
    // turned the protection fully on, and the next toggle wrote 0: the mode that was there could not be put back.

    /// <summary>
    /// One Defender setting as Windows keeps it: the status read reports its mode, and a Set changes it the way the
    /// real cmdlet would, so a test can follow several toggles and the modes they wrote.
    /// </summary>
    private sealed class DefenderSetting
    {
        public DefenderSetting(string property, int mode)
        {
            Mode = mode;
            Runner = Substitute.For<IPowerShellRunner>();
            Runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var script = call.ArgAt<string>(0);
                    if (script == DefenderService.StatusScript)
                    {
                        var projection = new PSObject();
                        projection.Properties.Add(new PSNoteProperty(property, Mode));
                        return new Collection<PSObject> { projection };
                    }
                    if (script.Contains($"-{property} $Value", StringComparison.Ordinal))
                    {
                        var value = (int)call.ArgAt<IDictionary<string, object?>?>(1)!["Value"]!;
                        Written.Add(value);
                        if (!HeldByPolicy) Mode = value;
                    }
                    return new Collection<PSObject>();
                });
        }

        public int Mode { get; set; }

        /// <summary>A setting Group Policy holds: Windows takes the Set and keeps the mode it had.</summary>
        public bool HeldByPolicy { get; set; }

        public List<int> Written { get; } = [];

        public IPowerShellRunner Runner { get; }
    }

    private static async Task<DefenderViewModel> VmOver(DefenderSetting setting)
    {
        var vm = new DefenderViewModel(new DefenderService(setting.Runner), NoRestorePoint());
        await vm.InitializationComplete;
        return vm;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task TwoCfaToggles_LeaveTheModeThatWasThere(int mode)
    {
        var setting = new DefenderSetting("EnableControlledFolderAccess", mode);
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ToggleCfaCommand.ExecuteAsync(null);
        await vm.ToggleCfaCommand.ExecuteAsync(null);

        Assert.Equal(new[] { mode == 1 ? 0 : 1, mode }, setting.Written);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TwoPuaToggles_LeaveTheModeThatWasThere(int mode)
    {
        var setting = new DefenderSetting("PUAProtection", mode);
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);
        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Equal(new[] { mode == 1 ? 0 : 1, mode }, setting.Written);
    }

    /// <summary>
    /// Once a mode is put back, the toggle turns the protection fully on again and remembers the mode again: while
    /// SysManager stays open, off is reached through Windows Security, as the questions say.
    /// </summary>
    [Fact]
    public async Task ThreeToggles_FromAuditMode_GoOnThenBackThenOn()
    {
        var setting = new DefenderSetting("EnableControlledFolderAccess", 2);
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: true);

        for (var i = 0; i < 3; i++) await vm.ToggleCfaCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 1, 2, 1 }, setting.Written);
    }

    [Fact]
    public async Task TurningCfaFullyOn_FromADiskOnlyMode_NamesTheMode_AndSaysTurningItOffPutsItBack()
    {
        var setting = new DefenderSetting("EnableControlledFolderAccess", 3);
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ToggleCfaCommand.ExecuteAsync(null);

        var message = Assert.Single(dialog.Messages);
        Assert.Contains("is set to \"Block disk modification only\"", message, StringComparison.Ordinal);
        Assert.Contains("only apps Windows trusts can change files in Documents", message, StringComparison.Ordinal);
        Assert.Contains("puts \"Block disk modification only\" back", message, StringComparison.Ordinal);
        Assert.Empty(setting.Written);
    }

    [Theory]
    [InlineData(0, "Disabled")]
    [InlineData(1, "Enabled")]
    [InlineData(2, "Audit mode")]
    [InlineData(3, "Block disk modification only")]
    [InlineData(4, "Audit disk modification only")]
    public async Task TheCfaCard_NamesEveryModeWindowsDocuments(int mode, string label)
    {
        var vm = await VmOver(new DefenderSetting("EnableControlledFolderAccess", mode));

        Assert.Equal(label, vm.CfaDisplay);
    }

    /// <summary>A mode changed in Windows Security after SysManager turned the protection on is not put back.</summary>
    [Fact]
    public async Task ACfaModeChangedOutsideTheTab_IsNotPutBack()
    {
        var setting = new DefenderSetting("EnableControlledFolderAccess", 3);
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ToggleCfaCommand.ExecuteAsync(null);
        setting.Mode = 0;
        await vm.RefreshCommand.ExecuteAsync(null);
        setting.Mode = 1;
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ToggleCfaCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 1, 0 }, setting.Written);
    }

    /// <summary>A turn-on the read-back does not show, held by Group Policy, remembers nothing to put back.</summary>
    [Fact]
    public async Task ATurnOnThatDidNotTakeHold_RemembersNoMode()
    {
        var setting = new DefenderSetting("EnableControlledFolderAccess", 3) { HeldByPolicy = true };
        var vm = await VmOver(setting);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ToggleCfaCommand.ExecuteAsync(null);
        setting.HeldByPolicy = false;
        setting.Mode = 1;
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.ToggleCfaCommand.ExecuteAsync(null);

        Assert.Equal(new[] { 1, 0 }, setting.Written);
    }

    [Fact]
    public async Task TogglePua_WhenRunspaceFaults_SurfacesStatus_DoesNotThrow()
    {
        // Regression (idx 74): a PowerShell runspace-level fault during a mutating
        // command (not the script RuntimeException the service catches) must be caught
        // and surfaced as a status message, like RefreshAsync — not escape the async
        // command to the global handler. The first RunAsync (GetStatus at init) succeeds;
        // the Set call faults.
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", 0));
        var ps = Substitute.For<IPowerShellRunner>();
        var calls = 0;
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(_ => ++calls <= 1
              ? new Collection<PSObject> { projection }
              : throw new System.InvalidOperationException("runspace is broken"));
        var vm = new DefenderViewModel(new DefenderService(ps), NoRestorePoint());
        await vm.InitializationComplete;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            // Must NOT throw — the fix adds the catch clauses.
            await vm.TogglePuaCommand.ExecuteAsync(null);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }

        // The fault came once the change had started, so whether Windows took it is unknown.
        Assert.False(vm.IsBusy);
        Assert.Contains("cannot tell whether it managed to change PUA protection", vm.StatusMessage,
            System.StringComparison.Ordinal);
    }

    // ---------- session restore point (#1500) ----------
    // All four Defender changes run through one funnel, so the snapshot happens in exactly one
    // place. These pin both halves: that it is taken before the change, and that it is only ever
    // claimed when Windows actually made one.

    /// <summary>
    /// A runner whose Get-MpPreference projection is readable, so ParseStatus reports Available and
    /// the read-back verification can succeed — the empty-collection runner above always yields the
    /// all-zeros Unavailable status, which only exercises the failure path.
    /// </summary>
    private static IPowerShellRunner RunnerReportingPua(int pua)
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", pua));
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { projection });
        return ps;
    }

    [Fact]
    public async Task TogglePua_TakesTheRestorePointBeforeChangingDefender()
    {
        var restorePoint = RestorePointTaken();
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), restorePoint);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        await restorePoint.Received(1).EnsureAsync("SysManager Defender Tweaks", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglePua_WhenTheUserDeclines_TakesNoRestorePoint()
    {
        // Declining changes nothing, so it must not spend the single point Windows grants per day.
        var restorePoint = RestorePointTaken();
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), restorePoint);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TogglePua_WhileSystemModificationLocked_RefusesAndTakesNoRestorePoint()
    {
        // #2510. A change that is cancelled with nothing to name it when SysManager closes mid-run now
        // shares the lock the other tabs that take a restore point before changing the system already do.
        var restorePoint = RestorePointTaken();
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), restorePoint);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Edge/OneDrive Remover");
        Assert.NotNull(held);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal("Cannot start — Edge/OneDrive Remover is already running.", vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EveryChange_GoesThroughTheOneFunnel()
    {
        // The reason for a funnel rather than four private calls: a command that skips it changes
        // Defender with no snapshot, and nothing else would notice.
        var restorePoint = RestorePointTaken();
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), restorePoint);
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);
        await vm.ToggleCfaCommand.ExecuteAsync(null);

        await restorePoint.Received(2).EnsureAsync("SysManager Defender Tweaks", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("TogglePua")]
    [InlineData("ToggleCfa")]
    [InlineData("RemoveExclusion")]
    public async Task EveryChange_SaysWhatTheRestorePointDoesToSystemProtection(string change)
    {
        // #2483. Every change goes through the funnel that takes the session restore point, which turns System
        // Protection back on when it is off, and no confirmation said so. Adding an exclusion asks through the
        // same Confirm, after a folder picker a test cannot answer.
        var restorePoint = NoRestorePoint();
        restorePoint.ConfirmationNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), restorePoint);
        await vm.InitializationComplete;
        vm.SelectedExclusion = @"C:\Games";
        using var dialog = new DialogAnswer(confirm: false);

        var command = change switch
        {
            "TogglePua" => vm.TogglePuaCommand,
            "ToggleCfa" => vm.ToggleCfaCommand,
            _ => vm.RemoveExclusionCommand,
        };
        await command.ExecuteAsync(null);

        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, Assert.Single(dialog.Messages), System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task TogglePua_WhenAPointWasCreated_SaysSo()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), RestorePointTaken());
        await vm.InitializationComplete;
        vm.PuaEnabled = false;   // so the toggle asks for ON (target 1), which the read-back confirms
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Contains("updated", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore point created.", vm.StatusMessage, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task TogglePua_WhenNoPointWasCreated_ClaimsNone()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), NoRestorePoint());
        await vm.InitializationComplete;
        vm.PuaEnabled = false;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Contains("updated", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restore point", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TogglePua_WhenTheChangeFails_ClaimsNoRestorePointEvenThoughOneWasMade()
    {
        // A point really was created here, but Defender rejected the change (Tamper Protection or
        // no admin). Mentioning the snapshot next to "was not changed" would read as though
        // something happened and could be undone. Nothing changed, so nothing is claimed.
        // The read-back still says on, so the change asked for (off) did not happen.
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), RestorePointTaken());
        await vm.InitializationComplete;
        Assert.True(vm.PuaEnabled);
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Contains("was not changed", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restore point", vm.StatusMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---------- exclusions Windows withholds from a standard user (#2476) ----------
    // For a standard user, Windows puts "N/A: Must be an administrator to view exclusions" where each exclusion list
    // should be. Shown as it arrives, that sentence would be listed as an excluded folder, and hidden without a
    // word the card would say "No exclusion folders", a claim about the machine that nobody had read. The service
    // decides on the session's elevation when it reads the status, so elevation is pinned for the whole test.

    private static IPowerShellRunner RunnerReportingExclusions(params string[] paths)
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("ExclusionPath", paths));
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { projection });
        return ps;
    }

    [Fact]
    public async Task ForAStandardUser_TheExclusionsCardSaysTheyAreHidden()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = new DefenderViewModel(
            new DefenderService(RunnerReportingExclusions("N/A: Must be an administrator to view exclusions")),
            NoRestorePoint());
        await vm.InitializationComplete;

        Assert.Empty(vm.ExclusionPaths);
        Assert.False(vm.ExclusionsReadable);
        Assert.Equal("Exclusions are hidden", vm.ExclusionsEmptyTitle);
        Assert.Contains("administrator", vm.ExclusionsEmptyMessage, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForAnAdministrator_TheExclusionsAreListed()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingExclusions(@"C:\Games")), NoRestorePoint());
        await vm.InitializationComplete;

        Assert.Equal(new[] { @"C:\Games" }, vm.ExclusionPaths);
        Assert.True(vm.ExclusionsReadable);
        Assert.Equal("No exclusion folders", vm.ExclusionsEmptyTitle);   // what an empty list would then say
    }

    // ---------- what the Controlled Folder Access confirmation says (#2505) ----------

    [Fact]
    public async Task EnablingControlledFolderAccess_Confirmation_SaysWhatItBlocksAndWhereToAllowAnApp()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), NoRestorePoint());
        await vm.InitializationComplete;
        vm.CfaEnabled = false;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ToggleCfaCommand.ExecuteAsync(null);

        var message = Assert.Single(dialog.Messages);
        Assert.Contains("only apps Windows trusts can change files in Documents", message, StringComparison.Ordinal);
        Assert.Contains("Allow an app through Controlled folder access", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingControlledFolderAccess_Confirmation_SaysTheFoldersAreOpenAgain()
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReportingPua(1)), NoRestorePoint());
        await vm.InitializationComplete;
        vm.CfaEnabled = true;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ToggleCfaCommand.ExecuteAsync(null);

        Assert.Contains("and so would ransomware", Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithAnotherAntivirusInCharge_TheTabSaysRealtimeIsOff_AndWhy()
    {
        // The card read the setting, so it said "On" while Defender was not scanning (#2867).
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("DisableRealtimeMonitoring", false));
        projection.Properties.Add(new PSNoteProperty("IsTamperProtected", true));
        projection.Properties.Add(new PSNoteProperty("RealTimeProtectionEnabled", false));
        projection.Properties.Add(new PSNoteProperty("AMRunningMode", "Passive Mode"));
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { projection });
        var vm = new DefenderViewModel(new DefenderService(ps), NoRestorePoint());
        await vm.InitializationComplete;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Off", vm.RealtimeDisplay);
        Assert.Equal("Real-time protection is off: another antivirus is in charge, and Windows runs Defender in passive mode.",
            vm.StatusMessage);
    }

    // ── AddExclusionAsync: the picker, not just the confirmation (#2860) ──

    [Fact]
    public async Task AddExclusionAsync_WhenCancelled_NeverAsksToConfirm()
    {
        var picker = Substitute.For<IDialogService>();
        picker.PickFolder(Arg.Any<string>()).Returns((string?)null);
        var vm = NewVm(picker);
        using var confirm = new DialogAnswer(confirm: true);

        await vm.AddExclusionCommand.ExecuteAsync(null);

        Assert.Equal(0, confirm.Calls);
    }

    [Fact]
    public async Task AddExclusionAsync_WhenAFolderIsPicked_AsksToConfirmThatFolder()
    {
        var folder = Directory.CreateTempSubdirectory("smtest_defender_").FullName;
        try
        {
            var picker = Substitute.For<IDialogService>();
            picker.PickFolder(Arg.Any<string>()).Returns(folder);
            var vm = NewVm(picker);
            using var confirm = new DialogAnswer(confirm: false);

            await vm.AddExclusionCommand.ExecuteAsync(null);

            Assert.Equal(1, confirm.Calls);
            Assert.Contains(confirm.Messages, m => m.Contains(folder, StringComparison.Ordinal));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// The folder picker offers a drive, and one confirmation used to turn off scanning for the whole system drive
    /// (#2773). Refused before the confirmation, saying why.
    /// </summary>
    [Fact]
    public async Task AddExclusionAsync_ForAWholeDrive_IsRefused_WithoutAsking()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var picker = Substitute.For<IDialogService>();
        picker.PickFolder(Arg.Any<string>()).Returns(drive);
        var vm = NewVm(picker);
        using var confirm = new DialogAnswer(confirm: true);

        await vm.AddExclusionCommand.ExecuteAsync(null);

        Assert.Equal(0, confirm.Calls);
        Assert.Equal($"SysManager won't exclude \"{drive}\" from scanning: it is a whole drive or network share, so the "
            + "files in it would no longer be checked for malware.", vm.StatusMessage);
    }

    // ---------- elevated as another account ----------
    // A standard account elevated with an administrator's password runs as that administrator: the folders the
    // exclusion rules protect (Downloads, Documents, the profile, the temp folder) were the administrator's, so the
    // signed-in person's own Downloads passed them. Removing an exclusion makes Defender scan a folder again, so it
    // stays available.

    [Fact]
    public async Task ElevatedAsAnotherAccount_AddingAnExclusion_IsRefused_BeforeThePicker()
    {
        var ps = RunnerReportingPua(0);
        var restorePoint = RestorePointTaken();
        var picker = Substitute.For<IDialogService>();
        picker.PickFolder(Arg.Any<string>()).Returns(@"C:\Games");
        var service = new DefenderService(ps, checksTheSignedInAccountsFolders: false);
        var vm = new DefenderViewModel(service, restorePoint, picker);
        await vm.InitializationComplete;
        using var confirm = new DialogAnswer(confirm: true);

        await vm.AddExclusionCommand.ExecuteAsync(null);

        Assert.StartsWith("SysManager won't add an exclusion here", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("sign in to Windows as that administrator", vm.StatusMessage, StringComparison.Ordinal);
        picker.DidNotReceive().PickFolder(Arg.Any<string>());
        Assert.Equal(0, confirm.Calls);
        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await ps.DidNotReceive().RunAsync(Arg.Is<string>(script => script != DefenderService.StatusScript),
            Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A scope guard: removing an exclusion strengthens protection, so the account check stays off.</summary>
    [Fact]
    public async Task ElevatedAsAnotherAccount_RemovingAnExclusion_StillAsks()
    {
        var service = new DefenderService(RunnerReportingPua(0), checksTheSignedInAccountsFolders: false);
        var vm = new DefenderViewModel(service, NoRestorePoint());
        await vm.InitializationComplete;
        vm.SelectedExclusion = @"C:\Games";
        using var confirm = new DialogAnswer(confirm: false);

        await vm.RemoveExclusionCommand.ExecuteAsync(null);

        Assert.Equal(1, confirm.Calls);
    }

    // ---------- without administrator rights (#2730) ----------
    // Windows lets a standard user read Defender but not change it, and on a default install that read says Tamper
    // Protection is on. Every change asked to confirm, tried for a restore point, failed, and then told the user to
    // turn Tamper Protection off, which changed nothing.

    private static IPowerShellRunner RunnerReporting(int pua, bool tamperProtected)
    {
        var projection = new PSObject();
        projection.Properties.Add(new PSNoteProperty("PUAProtection", pua));
        projection.Properties.Add(new PSNoteProperty("IsTamperProtected", tamperProtected));
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject> { projection });
        return ps;
    }

    [Theory]
    [InlineData("TogglePua", "Changing PUA protection")]
    [InlineData("ToggleCfa", "Changing Controlled Folder Access")]
    [InlineData("AddExclusion", "Adding an exclusion")]
    [InlineData("RemoveExclusion", "Removing an exclusion")]
    public async Task ForAStandardUser_EveryChange_SaysItNeedsAdministrator_BeforeAskingAnything(string change, string what)
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        // A folder the exclusion rules accept, so only the missing rights can stop the add before it asks.
        var folder = Directory.CreateTempSubdirectory("smtest_defender_").FullName;
        try
        {
            var ps = RunnerReporting(pua: 1, tamperProtected: true);
            var restorePoint = RestorePointTaken();
            var picker = Substitute.For<IDialogService>();
            picker.PickFolder(Arg.Any<string>()).Returns(folder);
            var vm = new DefenderViewModel(new DefenderService(ps), restorePoint, picker);
            await vm.InitializationComplete;
            Assert.True(vm.IsTamperProtected);   // the read that used to be blamed
            vm.SelectedExclusion = @"C:\Games";
            using var confirm = new DialogAnswer(confirm: true);

            var command = change switch
            {
                "TogglePua" => vm.TogglePuaCommand,
                "ToggleCfa" => vm.ToggleCfaCommand,
                "AddExclusion" => vm.AddExclusionCommand,
                _ => vm.RemoveExclusionCommand,
            };
            await command.ExecuteAsync(null);

            Assert.Equal($"{what} needs administrator rights — use \"Run as administrator\" above.", vm.StatusMessage);
            Assert.Equal(0, confirm.Calls);
            picker.DidNotReceive().PickFolder(Arg.Any<string>());
            await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await ps.DidNotReceive().RunAsync(Arg.Is<string>(script => script != DefenderService.StatusScript),
                Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// With administrator rights, a change the read-back does not show was put down to missing rights whenever
    /// Tamper Protection was off, to a session that had them.
    /// </summary>
    [Theory]
    [InlineData(false, "PUA protection was not changed — Windows did not apply it.")]
    [InlineData(true, "PUA protection was not changed — Tamper Protection blocked it. Turn it off in Windows Security first.")]
    public async Task AnElevatedChange_TheReadBackDoesNotShow_IsNotPutDownToRights(bool tamperProtected, string expected)
    {
        var vm = new DefenderViewModel(new DefenderService(RunnerReporting(pua: 1, tamperProtected)), NoRestorePoint());
        await vm.InitializationComplete;
        Assert.True(vm.PuaEnabled);   // so the toggle asks for off, and the read-back still says on
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TogglePuaCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.StatusMessage);
    }

    [Theory]
    [InlineData(false, true, "PUA protection was not changed — this needs administrator rights. Use \"Run as administrator\" above.")]
    [InlineData(false, false, "PUA protection was not changed — this needs administrator rights. Use \"Run as administrator\" above.")]
    [InlineData(true, true, "PUA protection was not changed — Tamper Protection blocked it. Turn it off in Windows Security first.")]
    [InlineData(true, false, "PUA protection was not changed — Windows did not apply it.")]
    public void NotChangedMessage_NamesTamperProtectionOnlyWithAdministratorRights(bool elevated, bool tamperProtected, string expected)
        => Assert.Equal(expected, DefenderViewModel.NotChangedMessage("PUA protection", elevated, tamperProtected));
}
