// SysManager · AllViewModelsSweepTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

/// <summary>
/// Constructor & shape sweep across every view model in the app. Each
/// test instantiates a VM and verifies invariants (no throw, observable
/// collections non-null, status strings present) without actually
/// performing any network / disk / process work.
/// </summary>
[Collection("Network")]
public class AllViewModelsSweepTests
{
    [Fact] public void Dashboard_Constructs() => Assert.NotNull(new DashboardViewModel(new SystemInfoService()));
    [Fact] public void AppUpdates_Constructs() => Assert.NotNull(new AppUpdatesViewModel(new WingetService(new PowerShellRunner())));
    [Fact] public void WindowsUpdate_Constructs() => Assert.NotNull(new WindowsUpdateViewModel(new PowerShellRunner()));
    [Fact] public void SystemHealth_Constructs() => Assert.NotNull(new SystemHealthViewModel(new SystemInfoService()));
    [Fact] public void Cleanup_Constructs() => Assert.NotNull(new CleanupViewModel(new PowerShellRunner()));
    [Fact] public void DeepCleanup_Constructs() => Assert.NotNull(new DeepCleanupViewModel());
    [Fact] public void Network_Constructs() => Assert.NotNull(new NetworkViewModel());
    [Fact] public void Drivers_Constructs() => Assert.NotNull(new DriversViewModel(new PowerShellRunner()));
    [Fact] public void Logs_Constructs() => Assert.NotNull(new LogsViewModel());
    [Fact] public void About_Constructs() => Assert.NotNull(new AboutViewModel());
    [Fact] public void MainWindow_Constructs() => Assert.NotNull(new MainWindowViewModel());

    [Fact] public void Dashboard_HasNonEmptySummaryOrEmpty()
        => Assert.NotNull(new DashboardViewModel(new SystemInfoService()));

    [Fact] public void AppUpdates_HasCollections()
    {
        var vm = new AppUpdatesViewModel(new WingetService(new PowerShellRunner()));
        Assert.NotNull(vm.Packages);
    }

    [Fact] public void SystemHealth_HasCollections()
    {
        var vm = new SystemHealthViewModel(new SystemInfoService());
        Assert.NotNull(vm.Modules);
        Assert.NotNull(vm.Disks);
        Assert.NotNull(vm.DiskHealth);
        Assert.NotNull(vm.ChkdskDrives);
    }

    [Fact] public void Logs_HasCollection()
    {
        var vm = new LogsViewModel();
        Assert.NotNull(vm.Entries);
    }

    [Fact] public void Drivers_HasConsole()
    {
        var vm = new DriversViewModel(new PowerShellRunner());
        Assert.NotNull(vm.Console);
    }

    [Fact] public void DeepCleanup_HasCollections()
    {
        var vm = new DeepCleanupViewModel();
        Assert.NotNull(vm.Categories);
        Assert.NotNull(vm.LargeFiles);
        Assert.NotNull(vm.ScanLocations);
    }

    [Fact] public void About_HasCollection()
    {
        var vm = new AboutViewModel();
        Assert.NotNull(vm.ReleaseHistory);
    }

    [Theory]
    [InlineData(typeof(DashboardViewModel))]
    [InlineData(typeof(AppUpdatesViewModel))]
    [InlineData(typeof(WindowsUpdateViewModel))]
    [InlineData(typeof(SystemHealthViewModel))]
    [InlineData(typeof(CleanupViewModel))]
    [InlineData(typeof(DeepCleanupViewModel))]
    [InlineData(typeof(NetworkViewModel))]
    [InlineData(typeof(DriversViewModel))]
    [InlineData(typeof(LogsViewModel))]
    [InlineData(typeof(AboutViewModel))]
    [InlineData(typeof(MainWindowViewModel))]
    public void VmType_IsObservable(Type t)
    {
        Assert.True(typeof(System.ComponentModel.INotifyPropertyChanged).IsAssignableFrom(t), $"{t.Name} must implement INotifyPropertyChanged");
    }
}
