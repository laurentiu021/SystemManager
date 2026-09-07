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
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.NotNull(vm.Console);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void CleanupVm_AllCommandsExist()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.NotNull(vm.CleanTempCommand);
        Assert.NotNull(vm.EmptyRecycleBinCommand);
        Assert.NotNull(vm.RunSfcCommand);
        Assert.NotNull(vm.RunDismCommand);
        Assert.NotNull(vm.CancelCommand);
    }

    [Fact]
    public void CleanupVm_CancelBeforeStart_IsSafe()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
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

    /// <summary>
    /// Constructing the Windows Update view model wires its console and does no work.
    /// </summary>
    /// <remarks>
    /// The second assertion was <c>Assert.False(vm.ModuleAvailable)</c>, which held while the constructor
    /// probed for PSWindowsUpdate. PR #611 ("install Windows Update via WUA COM API", merged 2026-06-03)
    /// deferred that probe to the History view and made the constructor set <c>ModuleAvailable = true</c>
    /// optimistically — so the test has asserted the opposite of the documented behaviour ever since,
    /// invisibly, because this project was compile-checked in the pipeline and never executed.
    /// <para><c>IsBusy</c> is the property that actually encodes what this test's name claims. The view
    /// model's own comment promises it: "This keeps the constructor side-effect-free and IsBusy stays false
    /// until the user triggers an action." Asserting that is asserting the contract, and it cannot rot the
    /// way an optimistic default did. It also puts this test on the same shape as every other
    /// <c>*_Ctor_IsSafe</c> in this file — console wired, not busy — instead of leaving it the one that
    /// reached for an unrelated flag.</para>
    /// </remarks>
    [Fact]
    public void WindowsUpdateVm_Ctor_IsSafe()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());
        Assert.NotNull(vm.Console);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void WindowsUpdateVm_AllCommandsExist()
    {
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());
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
        var vm = new WindowsUpdateViewModel(new PowerShellRunner(), new WindowsUpdateService(), new WindowsUpdatePolicyService());
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
