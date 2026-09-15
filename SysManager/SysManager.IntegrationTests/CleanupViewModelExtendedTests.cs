// SysManager · CleanupViewModelExtendedTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class CleanupViewModelExtendedTests
{
    [Fact]
    public void IsTempRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.False(vm.IsTempRunning);
    }

    [Fact]
    public void IsBinRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.False(vm.IsBinRunning);
    }

    [Fact]
    public void IsStoreRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.False(vm.IsStoreRunning);
    }

    [Fact]
    public void IsAnyRunning_FalseByDefault()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenComponentStoreRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService()) { IsStoreRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenTempRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService()) { IsTempRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenBinRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService()) { IsBinRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_RaisesPropertyChanged()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsAnyRunning)) fired = true; };
        vm.IsStoreRunning = true;
        Assert.True(fired);
    }

    [Fact]
    public void StoreStatus_DefaultsIdle()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.Equal("Idle", vm.StoreStatus);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("CleanTempCommand")]
    [InlineData("EmptyRecycleBinCommand")]
    [InlineData("AnalyzeComponentStoreCommand")]
    [InlineData("CleanComponentStoreCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void CommandExists(string propName)
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        var p = vm.GetType().GetProperty(propName);
        Assert.NotNull(p);
        Assert.NotNull(p!.GetValue(vm));
    }

    [Fact]
    public void Console_Present()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void ToggleStates_Independently()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        vm.IsTempRunning = true;
        vm.IsStoreRunning = true;
        Assert.True(vm.IsTempRunning && vm.IsStoreRunning);
        vm.IsTempRunning = false;
        Assert.False(vm.IsTempRunning);
        Assert.True(vm.IsStoreRunning);
    }

    [Fact]
    public void IsElevated_IsBoolean()
    {
        var vm = new CleanupViewModel(new PowerShellRunner(), new CleanupPreScanService());
        Assert.IsType<bool>(vm.IsElevated);
    }
}
