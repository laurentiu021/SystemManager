// SysManager · AppUpdatesViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
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
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        Assert.IsType<bool>(vm.IsElevated);
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

    // ---------- runner plumbing ----------

    [Fact]
    public void WingetLineReceived_AppendsToConsole()
    {
        var runner = new PowerShellRunner();
        var winget = new WingetService(runner);
        var vm = new AppUpdatesViewModel(winget);

        // WingetService.LineReceived delegates to runner.LineReceived,
        // so firing the runner event should propagate to the VM console.
        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.LineReceived),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(PowerShellLine.Output("winget test"));

        Assert.True(vm.Console.Lines.Count >= 1);
        Assert.Equal("winget test", vm.Console.Lines[^1].Text);
    }
}
