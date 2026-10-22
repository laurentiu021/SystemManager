// SysManager · ViewModelBaseTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
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
        var changed = vm.RecordPropertyChanges();
        vm.IsBusy = true;
        Assert.Contains("IsBusy", changed);
    }

    [Fact]
    public void StatusMessage_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = vm.RecordPropertyChanges();
        vm.StatusMessage = "Loading...";
        Assert.Contains("StatusMessage", changed);
    }

    [Fact]
    public void Progress_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = vm.RecordPropertyChanges();
        vm.Progress = 50;
        Assert.Contains("Progress", changed);
    }

    [Fact]
    public void IsProgressIndeterminate_RaisesPropertyChanged()
    {
        var vm = new TestVm();
        var changed = vm.RecordPropertyChanges();
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

    // ---------- InitializationComplete ----------

    /// <summary>
    /// A view model whose load starts a second load on another thread before its own <c>InitializeAsync</c> call
    /// returns, as a load does that resumes off the UI thread when no dispatcher queues it, which is how the tests run.
    /// </summary>
    private sealed class NestingVm : ViewModelBase
    {
        public TaskCompletionSource Second { get; } = new();

        public NestingVm() => InitializeAsync(() =>
        {
            var other = new Thread(() => InitializeAsync(() => Second.Task));
            other.Start();
            other.Join();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task InitializationComplete_IsTheNewestLoad_WhenItStartedBeforeTheFirstReturned()
    {
        // The first call set the property to its own task as it returned, over the second's, so a test waiting on it
        // went on while the second load was still running (#2947).
        var vm = new NestingVm();

        Assert.False(vm.InitializationComplete.IsCompleted);
        vm.Second.SetResult();
        await vm.InitializationComplete;
    }
}
