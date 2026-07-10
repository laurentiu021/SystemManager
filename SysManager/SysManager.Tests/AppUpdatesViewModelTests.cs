// SysManager · AppUpdatesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class AppUpdatesViewModelTests
{
    private static readonly PowerShellRunner _sharedRunner = new();
    private static AppUpdatesViewModel NewVm() => new(new WingetService(_sharedRunner));

    // ---------- construction ----------

    [Fact]
    public void Constructor_PackagesEmpty()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Packages);
        Assert.Empty(vm.Packages);
    }

    [Fact]
    public void Constructor_ConsoleNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_SelectAllDefaultsTrue()
    {
        var vm = NewVm();
        Assert.True(vm.SelectAll);
    }

    [Fact]
    public void Constructor_IsElevated_MatchesAdminHelper()
    {
        // The VM seeds IsElevated from AdminHelper.IsElevated(); assert it reflects that
        // source of truth rather than the old tautological Assert.IsType<bool> (which
        // always passed on a bool property).
        var vm = NewVm();
        Assert.Equal(SysManager.Helpers.AdminHelper.IsElevated(), vm.IsElevated);
    }

    [Fact]
    public void Constructor_IsBusyFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Constructor_StatusMessageEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    // ---------- commands ----------

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("UpgradeSelectedCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_WithLiveCts_RequestsCancellation()
    {
        var vm = NewVm();
        var cts = new CancellationTokenSource();
        typeof(AppUpdatesViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);
        vm.CancelCommand.Execute(null);
        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- SelectAll toggle ----------

    [Fact]
    public void SelectAll_False_DeselectsAllPackages()
    {
        var vm = NewVm();
        var p1 = new AppPackage { Name = "A", Id = "a", CurrentVersion = "1", AvailableVersion = "2", IsSelected = true };
        var p2 = new AppPackage { Name = "B", Id = "b", CurrentVersion = "1", AvailableVersion = "2", IsSelected = true };
        vm.Packages.Add(p1);
        vm.Packages.Add(p2);

        vm.SelectAll = false;

        Assert.False(p1.IsSelected);
        Assert.False(p2.IsSelected);
    }

    [Fact]
    public void SelectAll_True_SelectsAllPackages()
    {
        var vm = NewVm();
        var p1 = new AppPackage { Name = "A", Id = "a", CurrentVersion = "1", AvailableVersion = "2", IsSelected = false };
        vm.Packages.Add(p1);

        // SelectAll defaults to true, so we must flip to false first to trigger the change.
        vm.SelectAll = false;
        vm.SelectAll = true;

        Assert.True(p1.IsSelected);
    }

    // ---------- UpgradeSelected guard ----------

    [Fact]
    public async Task UpgradeSelected_NothingSelected_SetsStatusMessage()
    {
        var vm = NewVm();
        vm.Packages.Add(new AppPackage { Name = "A", Id = "a", CurrentVersion = "1", AvailableVersion = "2", IsSelected = false });

        await vm.UpgradeSelectedCommand.ExecuteAsync(null);

        Assert.Contains("No packages", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- re-entrancy guard (regression: shared CTS disposed mid-flight) ----------

    [Fact]
    public void LongRunningCommands_DisabledWhileBusy()
    {
        // Scan and UpgradeSelected both recreate the shared _cts. Without the NotBusy gate,
        // triggering one while the other runs would dispose the CTS still being awaited
        // (ObjectDisposedException). Cancel must stay enabled so an in-flight run can stop.
        var vm = NewVm();
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.UpgradeSelectedCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.UpgradeSelectedCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.UpgradeSelectedCommand.CanExecute(null));
    }

    // ---------- console subscription is op-scoped (regression: cross-tab winget output leak) ----------

    [Fact]
    public async Task WingetLineReceived_DuringScan_AppendsToConsole()
    {
        // While THIS tab's scan runs, its own winget output must reach its console. The scan
        // subscribes to LineReceived for the operation, so a line raised during the underlying
        // ListUpgradableAsync call is captured.
        var winget = Substitute.For<IWingetService>();
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                winget.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("scan line"));
                return Task.FromResult(new List<AppPackage>());
            });
        var vm = new AppUpdatesViewModel(winget);

        await vm.ScanCommand.ExecuteAsync(null);

        Assert.Contains(vm.Console.Lines, l => l.Text == "scan line");
    }

    [Fact]
    public void WingetLineReceived_OutsideOperation_DoesNotAppend()
    {
        // Regression: WingetService is a singleton shared with other tabs (e.g. the Dashboard's
        // "Update All Apps"). If this VM stayed subscribed to LineReceived for its whole lifetime,
        // another tab's winget output would bleed into the App Updates console. With no scan or
        // upgrade running, firing LineReceived must NOT touch this console.
        var winget = Substitute.For<IWingetService>();
        var vm = new AppUpdatesViewModel(winget);

        winget.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output("from another tab"));

        Assert.Empty(vm.Console.Lines);
    }

    // ---------- winget-unavailable friendly message (regression P2 #41) ----------

    [Fact]
    public async Task Scan_WhenWingetMissing_ShowsFriendlyMessage_NotRawError()
    {
        // Regression (P2 #41): winget.exe missing (App Installer absent / execution alias
        // off) makes Process.Start throw Win32Exception. Scan is the tab's first action;
        // before the fix that exception escaped the AsyncRelayCommand to the global
        // dispatcher handler and popped a raw OS-error dialog. Now it must be caught and
        // shown as the plain-language "install App Installer" status.
        var winget = Substitute.For<IWingetService>();
        winget.ListUpgradableAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<AppPackage>>>(_ => throw new System.ComponentModel.Win32Exception(2)); // ERROR_FILE_NOT_FOUND
        var vm = new AppUpdatesViewModel(winget);

        var ex = await Record.ExceptionAsync(() => vm.ScanCommand.ExecuteAsync(null));

        Assert.Null(ex); // the command must not fault
        Assert.Equal(AppUpdatesViewModel.WingetUnavailableMessage, vm.StatusMessage);
    }

    [Fact]
    public async Task Upgrade_WhenWingetMissing_ShowsFriendlyMessage_AndStops()
    {
        // The batch summary must NOT overwrite the friendly message with "Updated 0 of N".
        var winget = Substitute.For<IWingetService>();
        winget.UpgradeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<WingetResult>>(_ => throw new System.ComponentModel.Win32Exception(2));
        var vm = new AppUpdatesViewModel(winget);
        vm.Packages.Add(new AppPackage { Name = "A", Id = "a", CurrentVersion = "1", AvailableVersion = "2", IsSelected = true });

        var ex = await Record.ExceptionAsync(() => vm.UpgradeSelectedCommand.ExecuteAsync(null));

        Assert.Null(ex);
        Assert.Equal(AppUpdatesViewModel.WingetUnavailableMessage, vm.StatusMessage);
    }
}
