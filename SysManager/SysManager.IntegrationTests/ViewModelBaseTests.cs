// SysManager · ViewModelBaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class ViewModelBaseTests
{
    private class Concrete : ViewModelBase { }

    [Fact]
    public void Defaults_AreSafe()
    {
        var vm = new Concrete();
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.StatusMessage);
        Assert.Equal(0, vm.Progress);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void IsBusy_RaisesPropertyChanged()
    {
        var vm = new Concrete();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.IsBusy = true;
        Assert.Contains(nameof(ViewModelBase.IsBusy), raised);
    }

    [Fact]
    public void StatusMessage_RaisesPropertyChanged()
    {
        var vm = new Concrete();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.StatusMessage = "hello";
        Assert.Contains(nameof(ViewModelBase.StatusMessage), raised);
    }

    [Fact]
    public void Progress_AcceptsFullRange()
    {
        var vm = new Concrete();
        for (int i = 0; i <= 100; i += 10)
        {
            vm.Progress = i;
            Assert.Equal(i, vm.Progress);
        }
    }

    [Fact]
    public void Progress_AcceptsOutOfRange_NoClamping()
    {
        // Clamping is consumer responsibility. We document current behavior.
        var vm = new Concrete();
        vm.Progress = -50;
        Assert.Equal(-50, vm.Progress);
        vm.Progress = 250;
        Assert.Equal(250, vm.Progress);
    }

    [Fact]
    public void IsProgressIndeterminate_Toggles()
    {
        var vm = new Concrete();
        vm.IsProgressIndeterminate = true;
        Assert.True(vm.IsProgressIndeterminate);
        vm.IsProgressIndeterminate = false;
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void InheritsObservableObject()
    {
        Assert.IsAssignableFrom<ObservableObject>(new Concrete());
    }

    [Fact]
    public void StatusMessage_AcceptsNull_AndConverts()
    {
        var vm = new Concrete();
        vm.StatusMessage = null!;
        Assert.Null(vm.StatusMessage);
    }
}
