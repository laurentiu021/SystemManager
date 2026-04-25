// SysManager · DriversViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DriversViewModel"/>.
/// Async commands that spawn real PowerShell are tested in IntegrationTests.
/// </summary>
public class DriversViewModelTests
{
    private static DriversViewModel NewVm() => new(new PowerShellRunner());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SetsConsole()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
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

    [Fact]
    public void Constructor_IsProgressIndeterminateFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsProgressIndeterminate);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("ListDriversCommand")]
    [InlineData("CheckWindowsUpdateDriversCommand")]
    [InlineData("CancelCommand")]
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
        typeof(DriversViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);

        vm.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- runner plumbing ----------

    [Fact]
    public void RunnerLineReceived_AppendsToConsole()
    {
        var runner = new PowerShellRunner();
        var vm = new DriversViewModel(runner);

        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.LineReceived),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(Models.PowerShellLine.Output("driver test line"));

        Assert.Single(vm.Console.Lines);
        Assert.Equal("driver test line", vm.Console.Lines[0].Text);
    }
}

// ---------- sorting ----------

public class DriversViewModelSortTests
{
    private static DriversViewModel NewVm() => new(new PowerShellRunner());

    [Fact]
    public void SortOptions_ContainsFourEntries()
    {
        Assert.Equal(4, DriversViewModel.SortOptions.Length);
        Assert.Contains("Name", DriversViewModel.SortOptions);
        Assert.Contains("Manufacturer", DriversViewModel.SortOptions);
        Assert.Contains("Version", DriversViewModel.SortOptions);
        Assert.Contains("Date", DriversViewModel.SortOptions);
    }

    [Fact]
    public void SortBy_DefaultIsName()
    {
        var vm = NewVm();
        Assert.Equal("Name", vm.SortBy);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Manufacturer")]
    [InlineData("Version")]
    [InlineData("Date")]
    public void SortBy_CanBeSet(string value)
    {
        var vm = NewVm();
        vm.SortBy = value;
        Assert.Equal(value, vm.SortBy);
    }

    [Fact]
    public void SortBy_RaisesPropertyChanged()
    {
        var vm = NewVm();
        bool raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == "SortBy") raised = true; };
        vm.SortBy = "Date";
        Assert.True(raised);
    }
}
