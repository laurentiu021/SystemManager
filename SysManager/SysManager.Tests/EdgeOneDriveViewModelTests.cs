// SysManager · EdgeOneDriveViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using Microsoft.Win32;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="EdgeOneDriveViewModel"/> — the re-entrancy guard (NotBusy) that
/// serialises the four mutating commands, and the pure state-text derivation the status panel
/// binds to. The service is constructed over redirected HKCU roots and a substituted runner, so
/// no process, scheduled task, or real machine key is touched.
/// </summary>
[Collection("ProcessWideStatics")]
public sealed class EdgeOneDriveViewModelTests : IDisposable
{
    private readonly string _rootName = @"Software\SysManagerTests\EdgeOneDriveVm_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public EdgeOneDriveViewModelTests()
        => _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { /* best-effort cleanup */ }
    }

    private EdgeOneDriveViewModel NewVm()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>());
        var vm = new EdgeOneDriveViewModel(new EdgeOneDriveService(ps, hkcuRoot: _root, hklmRoot: _root),
                                      NoRestorePoint());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
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

        Assert.True(vm.RemoveOneDriveCommand.CanExecute(null));
        Assert.True(vm.RestoreOneDriveCommand.CanExecute(null));
        Assert.True(vm.DisableEdgeCommand.CanExecute(null));
        Assert.True(vm.RestoreEdgeCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.NotBusy);
        Assert.False(vm.RemoveOneDriveCommand.CanExecute(null));
        Assert.False(vm.RestoreOneDriveCommand.CanExecute(null));
        Assert.False(vm.DisableEdgeCommand.CanExecute(null));
        Assert.False(vm.RestoreEdgeCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.RemoveOneDriveCommand.CanExecute(null));
    }

    // ── State-text derivation ───────────────────────────────────────────────

    [Fact]
    public void OneDriveStateText_ReflectsInstalledAndRunning()
    {
        var vm = NewVm();

        vm.OneDriveInstalled = false;
        Assert.Contains("not installed", vm.OneDriveStateText);

        vm.OneDriveInstalled = true;
        vm.OneDriveRunning = false;
        Assert.Contains("installed", vm.OneDriveStateText);
        Assert.DoesNotContain("running", vm.OneDriveStateText);

        vm.OneDriveRunning = true;
        Assert.Contains("running", vm.OneDriveStateText);
    }

    [Fact]
    public void EdgeStateText_ReflectsInstalledAndDeintegratedState()
    {
        var vm = NewVm();

        vm.EdgeInstalled = false;
        Assert.Contains("not installed", vm.EdgeStateText);

        vm.EdgeInstalled = true;
        vm.EdgeBackgroundDisabled = false;
        Assert.Contains("active", vm.EdgeStateText);

        vm.EdgeBackgroundDisabled = true;
        Assert.Contains("de-integrated", vm.EdgeStateText);
    }

    // ── Confirm gate: a declined dialog performs no work ────────────────────

    [Fact]
    public async Task RemoveOneDrive_WhenUserDeclines_DoesNotInvokeService()
    {
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            var ps = Substitute.For<IPowerShellRunner>();
            ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
            var vm = new EdgeOneDriveViewModel(new EdgeOneDriveService(ps, hkcuRoot: _root, hklmRoot: _root),
                                      NoRestorePoint());
            await vm.InitializationComplete;
            // Force the "installed" precondition so the guard reaches the confirm dialog.
            vm.OneDriveInstalled = true;
            ps.ClearReceivedCalls();

            await vm.RemoveOneDriveCommand.ExecuteAsync(null);

            Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            // No process launch happened — the decline short-circuited before any service call.
            await ps.DidNotReceive().RunProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally { DialogService.Instance = prevDialog; }
    }

    [Fact]
    public async Task RemoveOneDrive_WhileSystemModificationLocked_RefusesAndTakesNoRestorePoint()
    {
        // #2510. A change that is cancelled with nothing to name it when SysManager closes mid-run now
        // shares the lock the other tabs that take a restore point before changing the system already do.
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Services");
        Assert.NotNull(held);
        try
        {
            var ps = Substitute.For<IPowerShellRunner>();
            ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
              .Returns(new Collection<PSObject>());
            var restorePoint = NoRestorePoint();
            var vm = new EdgeOneDriveViewModel(new EdgeOneDriveService(ps, hkcuRoot: _root, hklmRoot: _root),
                                      restorePoint);
            await vm.InitializationComplete;
            vm.OneDriveInstalled = true;
            ps.ClearReceivedCalls();

            await vm.RemoveOneDriveCommand.ExecuteAsync(null);

            await ps.DidNotReceive().RunProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            Assert.Equal("Cannot start — Services is already running.", vm.StatusMessage);
            Assert.False(vm.IsBusy);
        }
        finally { DialogService.Instance = prevDialog; }
    }

    [Theory]
    [InlineData("RemoveOneDrive")]
    [InlineData("RestoreOneDrive")]
    [InlineData("DisableEdge")]
    [InlineData("RestoreEdge")]
    public async Task EveryChange_SaysWhatTheRestorePointDoesToSystemProtection(string change)
    {
        // #2483. All four changes take the session restore point first, which turns System Protection back on
        // when it is off, and none of their confirmations said so.
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>());
        var restorePoint = NoRestorePoint();
        restorePoint.ConfirmationNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        var vm = new EdgeOneDriveViewModel(new EdgeOneDriveService(ps, hkcuRoot: _root, hklmRoot: _root), restorePoint);
        await vm.InitializationComplete;
        // The installed precondition, so each guard reaches its confirmation.
        vm.OneDriveInstalled = true;
        vm.EdgeInstalled = true;
        using var dialog = new DialogAnswer(confirm: false);

        var command = change switch
        {
            "RemoveOneDrive" => vm.RemoveOneDriveCommand,
            "RestoreOneDrive" => vm.RestoreOneDriveCommand,
            "DisableEdge" => vm.DisableEdgeCommand,
            _ => vm.RestoreEdgeCommand,
        };
        await command.ExecuteAsync(null);

        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }

    /// <summary>
    /// A session restore point that never materialises — the honest default on a machine with System
    /// Restore off, and what keeps these tests about Edge/OneDrive rather than about snapshots.
    /// </summary>
    private static ISessionRestorePoint NoRestorePoint()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return rp;
    }

    // ---------- what the Edge confirmation says (#2505) ----------

    [Fact]
    public async Task DisablingEdge_Confirmation_SaysItsSecurityFixesCanArriveLate()
    {
        var ps = Substitute.For<IPowerShellRunner>();
        ps.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>())
          .Returns(new Collection<PSObject>());
        var vm = new EdgeOneDriveViewModel(new EdgeOneDriveService(ps, hkcuRoot: _root, hklmRoot: _root), NoRestorePoint());
        await vm.InitializationComplete;
        vm.EdgeInstalled = true;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.DisableEdgeCommand.ExecuteAsync(null);

        Assert.Contains("security fixes can arrive late", Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }
}
