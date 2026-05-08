// SysManager · PlaceholderViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.ViewModels;

namespace SysManager.Tests;

public class PlaceholderViewModelTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var vm = new PlaceholderViewModel("Test Feature", "A description", "123");
        Assert.Equal("Test Feature", vm.FeatureName);
        Assert.Equal("A description", vm.Description);
        Assert.Equal("123", vm.IssueNumber);
    }

    [Fact]
    public void InheritsViewModelBase()
    {
        var vm = new PlaceholderViewModel("X", "Y", "1");
        Assert.IsAssignableFrom<ViewModelBase>(vm);
    }

    [Fact]
    public void IsBusy_DefaultFalse()
    {
        var vm = new PlaceholderViewModel("X", "Y", "1");
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var vm = new PlaceholderViewModel("X", "Y", "1");
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }
}
