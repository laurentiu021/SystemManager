using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class WindowsUpdateAutoCheckTests
{
    private static WindowsUpdateViewModel Build() => new(new PowerShellRunner());

    [Fact]
    public void ModuleStatus_HasInitialMessage()
    {
        var vm = Build();
        Assert.False(string.IsNullOrWhiteSpace(vm.ModuleStatus));
    }

    [Fact]
    public void ModuleAvailable_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.ModuleAvailable);
    }

    [Fact]
    public void IsElevated_IsBoolean()
    {
        var vm = Build();
        Assert.IsType<bool>(vm.IsElevated);
    }

    [Theory]
    [InlineData("CheckModuleCommand")]
    [InlineData("InstallModuleCommand")]
    [InlineData("ListUpdatesCommand")]
    [InlineData("ListFeatureUpdatesCommand")]
    [InlineData("ShowHistoryCommand")]
    [InlineData("CheckPendingRebootCommand")]
    [InlineData("InstallUpdatesCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void CommandExists(string propName)
    {
        var vm = Build();
        var p = vm.GetType().GetProperty(propName);
        Assert.NotNull(p);
        Assert.NotNull(p!.GetValue(vm));
    }

    [Fact]
    public void Console_Wired()
    {
        var vm = Build();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void CancelCommand_DoesNotThrow()
    {
        var vm = Build();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void Progress_StartsZero()
    {
        var vm = Build();
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public void IsProgressIndeterminate_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var vm = Build();
        vm.ModuleAvailable = true;
        Assert.True(vm.ModuleAvailable);
        vm.ModuleStatus = "Available";
        Assert.Equal("Available", vm.ModuleStatus);
    }
}
