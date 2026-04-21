using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class CleanupViewModelExtendedTests
{
    [Fact]
    public void IsTempRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.False(vm.IsTempRunning);
    }

    [Fact]
    public void IsBinRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.False(vm.IsBinRunning);
    }

    [Fact]
    public void IsSfcRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.False(vm.IsSfcRunning);
    }

    [Fact]
    public void IsDismRunning_DefaultsFalse()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.False(vm.IsDismRunning);
    }

    [Fact]
    public void IsAnyRunning_FalseByDefault()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenSfcRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner()) { IsSfcRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenDismRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner()) { IsDismRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenTempRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner()) { IsTempRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_TrueWhenBinRuns()
    {
        var vm = new CleanupViewModel(new PowerShellRunner()) { IsBinRunning = true };
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_RaisesPropertyChanged()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.IsAnyRunning)) fired = true; };
        vm.IsSfcRunning = true;
        Assert.True(fired);
    }

    [Fact]
    public void SfcStatus_DefaultsIdle()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.Equal("Idle", vm.SfcStatus);
    }

    [Fact]
    public void DismStatus_DefaultsIdle()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.Equal("Idle", vm.DismStatus);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("CleanTempCommand")]
    [InlineData("EmptyRecycleBinCommand")]
    [InlineData("RunSfcCommand")]
    [InlineData("RunDismCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void CommandExists(string propName)
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        var p = vm.GetType().GetProperty(propName);
        Assert.NotNull(p);
        Assert.NotNull(p!.GetValue(vm));
    }

    [Fact]
    public void Console_Present()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void ToggleStates_Independently()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        vm.IsSfcRunning = true;
        vm.IsDismRunning = true;
        Assert.True(vm.IsSfcRunning && vm.IsDismRunning);
        vm.IsSfcRunning = false;
        Assert.False(vm.IsSfcRunning);
        Assert.True(vm.IsDismRunning);
    }

    [Fact]
    public void IsElevated_IsBoolean()
    {
        var vm = new CleanupViewModel(new PowerShellRunner());
        Assert.IsType<bool>(vm.IsElevated);
    }
}
