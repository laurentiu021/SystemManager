// SysManager · ViewModelBase dispose and state transition tests
using SysManager.ViewModels;

namespace SysManager.Tests;

public class ViewModelBaseExtendedTests
{
    private sealed class TestViewModel : ViewModelBase
    {
        public int DisposeCallCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeCallCount++;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Dispose_CallsDisposeTrue()
    {
        var vm = new TestViewModel();
        vm.Dispose();
        Assert.Equal(1, vm.DisposeCallCount);
    }

    [Fact]
    public void Dispose_CalledTwice_GCSuppressFinalizeCalledButNoThrow()
    {
        var vm = new TestViewModel();
        vm.Dispose();
        // Calling Dispose again should not throw
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void IsBusy_DefaultFalse()
    {
        var vm = new TestViewModel();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void StatusMessage_DefaultEmpty()
    {
        var vm = new TestViewModel();
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void Progress_DefaultZero()
    {
        var vm = new TestViewModel();
        Assert.Equal(0, vm.Progress);
    }

    [Fact]
    public void IsProgressIndeterminate_DefaultFalse()
    {
        var vm = new TestViewModel();
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void IsBusy_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.IsBusy = true;

        Assert.Contains("IsBusy", changed);
    }

    [Fact]
    public void StatusMessage_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.StatusMessage = "Working...";

        Assert.Contains("StatusMessage", changed);
    }

    [Fact]
    public void Progress_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Progress = 50;

        Assert.Contains("Progress", changed);
    }

    [Fact]
    public void IsProgressIndeterminate_RaisesPropertyChanged()
    {
        var vm = new TestViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.IsProgressIndeterminate = true;

        Assert.Contains("IsProgressIndeterminate", changed);
    }

    [Fact]
    public void Progress_SetSameValue_DoesNotRaisePropertyChanged()
    {
        var vm = new TestViewModel();
        vm.Progress = 50;
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Progress = 50;

        Assert.DoesNotContain("Progress", changed);
    }

    [Fact]
    public void IsBusy_SetSameValue_DoesNotRaisePropertyChanged()
    {
        var vm = new TestViewModel();
        vm.IsBusy = false;
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.IsBusy = false;

        Assert.DoesNotContain("IsBusy", changed);
    }
}
