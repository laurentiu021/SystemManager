// SysManager · ViewModelBaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ViewModelBase"/> — the abstract base class for all VMs.
/// </summary>
public class ViewModelBaseTests
{
    private sealed class TestVm : ViewModelBase { }

    [Fact]
    public void IsBusy_DefaultsFalse()
        => Assert.False(new TestVm().IsBusy);

    [Fact]
    public void StatusMessage_DefaultsEmpty()
        => Assert.Equal(string.Empty, new TestVm().StatusMessage);

    [Fact]
    public void Progress_DefaultsZero()
        => Assert.Equal(0, new TestVm().Progress);

    [Fact]
    public void IsProgressIndeterminate_DefaultsFalse()
        => Assert.False(new TestVm().IsProgressIndeterminate);

    [Fact]
    public void IsBusy_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.IsBusy = true;
        Assert.Contains("IsBusy", changed);
    }

    [Fact]
    public void StatusMessage_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.StatusMessage = "Loading...";
        Assert.Contains("StatusMessage", changed);
    }

    [Fact]
    public void Progress_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.Progress = 50;
        Assert.Contains("Progress", changed);
    }

    [Fact]
    public void IsProgressIndeterminate_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.IsProgressIndeterminate = true;
        Assert.Contains("IsProgressIndeterminate", changed);
    }

    [Fact]
    public void AllProperties_Settable()
    {
        var vm = new TestVm
        {
            IsBusy = true,
            StatusMessage = "Working",
            Progress = 75,
            IsProgressIndeterminate = true
        };
        Assert.True(vm.IsBusy);
        Assert.Equal("Working", vm.StatusMessage);
        Assert.Equal(75, vm.Progress);
        Assert.True(vm.IsProgressIndeterminate);
    }
}
