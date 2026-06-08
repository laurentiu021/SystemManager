// SysManager · LightTouchViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Tests that do NOT execute long-running external processes (winget, PSWU,
/// sfc, DISM). They verify commands exist, cancel safely, and expose
/// expected default state.
/// </summary>
public class LightTouchViewModelTests
{
    // ---------- Cleanup ----------

    [Fact]
    public void CleanupVm_Ctor_IsSafe()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.NotNull(vm.Console);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void CleanupVm_AllCommandsExist()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.NotNull(vm.CleanTempCommand);
        Assert.NotNull(vm.EmptyRecycleBinCommand);
        Assert.NotNull(vm.RunSfcCommand);
        Assert.NotNull(vm.RunDismCommand);
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void CleanupVm_CancelBeforeStart_IsSafe()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    // ---------- Drivers ----------

    [Fact]
    public void DriversVm_Ctor_IsSafe()
    {
        var vm = new DriversViewModel(new PowerShellRunner());
        Assert.NotNull(vm.Drivers);
    }

    [Fact]
    public void DriversVm_AllCommandsExist()
    {
        var vm = new DriversViewModel(new PowerShellRunner());
        Assert.NotNull(vm.ListDriversCommand);
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void DriversVm_CancelBeforeStart_IsSafe()
    {
        var vm = new DriversViewModel(new PowerShellRunner());
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    // ---------- Windows Update ----------

    [Fact]
    public void WindowsUpdateVm_Ctor_IsSafe()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService());
        Assert.NotNull(vm.Console);
        Assert.False(vm.ModuleAvailable);
    }

    [Fact]
    public void WindowsUpdateVm_AllCommandsExist()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService());
        Assert.NotNull(vm.CheckModuleCommand);
        Assert.NotNull(vm.InstallModuleCommand);
        Assert.NotNull(vm.ListUpdatesCommand);
        Assert.NotNull(vm.ShowHistoryCommand);
        Assert.NotNull(vm.CheckPendingRebootCommand);
        Assert.NotNull(vm.InstallUpdatesCommand);
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void WindowsUpdateVm_ModuleStatusDefault_IsSet()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService());
        // Since the WUA COM migration, updates use the COM API directly and
        // PSWindowsUpdate backs only the History view; the default status reflects that.
        Assert.False(string.IsNullOrWhiteSpace(vm.ModuleStatus));
    }

    // ---------- App updates ----------

    [Fact]
    public void AppUpdatesVm_Ctor_IsSafe()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        Assert.NotNull(vm.Console);
        Assert.True(vm.SelectAll);
        Assert.Empty(vm.Packages);
    }

    [Fact]
    public void AppUpdatesVm_AllCommandsExist()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.UpgradeSelectedCommand);
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void AppUpdatesVm_ToggleSelectAll_AffectsPackages()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        vm.Packages.Add(new Models.AppPackage { Name = "A" });
        vm.Packages.Add(new Models.AppPackage { Name = "B" });

        vm.SelectAll = false;
        Assert.All(vm.Packages, p => Assert.False(p.IsSelected));

        vm.SelectAll = true;
        Assert.All(vm.Packages, p => Assert.True(p.IsSelected));
    }

    [Fact]
    public void AppUpdatesVm_CancelBeforeStart_IsSafe()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public async Task AppUpdatesVm_UpgradeSelected_WithEmptyList_SetsStatus()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        await vm.UpgradeSelectedCommand.ExecuteAsync(null);
        Assert.Contains("selected", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
