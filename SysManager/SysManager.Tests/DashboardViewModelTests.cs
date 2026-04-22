// SysManager · DashboardViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="DashboardViewModel"/>.
/// RefreshAsync hits real WMI so it lives in IntegrationTests.
/// </summary>
public class DashboardViewModelTests
{
    private static DashboardViewModel NewVm() => new(new SystemInfoService());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SnapshotIsNull()
    {
        var vm = NewVm();
        Assert.Null(vm.Snapshot);
    }

    [Fact]
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        Assert.IsType<bool>(vm.IsElevated);
    }

    [Fact]
    public void Constructor_StringProperties_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.OsLine);
        Assert.Equal("", vm.CpuLine);
        Assert.Equal("", vm.MemLine);
        Assert.Equal("", vm.DiskLine);
        Assert.Equal("", vm.UptimeLine);
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

    // ---------- commands exist ----------

    [Theory]
    [InlineData("RefreshCommand")]
    [InlineData("RequestElevationCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- setters ----------

    [Fact]
    public void OsLine_Setter_Works()
    {
        var vm = NewVm();
        vm.OsLine = "Windows 11 Pro";
        Assert.Equal("Windows 11 Pro", vm.OsLine);
    }

    [Fact]
    public void CpuLine_Setter_Works()
    {
        var vm = NewVm();
        vm.CpuLine = "i7-12700K";
        Assert.Equal("i7-12700K", vm.CpuLine);
    }

    [Fact]
    public void MemLine_Setter_Works()
    {
        var vm = NewVm();
        vm.MemLine = "16 / 32 GB";
        Assert.Equal("16 / 32 GB", vm.MemLine);
    }

    [Fact]
    public void DiskLine_Setter_Works()
    {
        var vm = NewVm();
        vm.DiskLine = "NVMe 1TB Healthy";
        Assert.Equal("NVMe 1TB Healthy", vm.DiskLine);
    }

    [Fact]
    public void UptimeLine_Setter_Works()
    {
        var vm = NewVm();
        vm.UptimeLine = "Uptime: 3d 5h";
        Assert.Equal("Uptime: 3d 5h", vm.UptimeLine);
    }

    // ---------- PropertyChanged ----------

    [Theory]
    [InlineData(nameof(DashboardViewModel.OsLine), "test")]
    [InlineData(nameof(DashboardViewModel.CpuLine), "test")]
    [InlineData(nameof(DashboardViewModel.MemLine), "test")]
    [InlineData(nameof(DashboardViewModel.DiskLine), "test")]
    [InlineData(nameof(DashboardViewModel.UptimeLine), "test")]
    public void Setter_FiresPropertyChanged(string propName, string value)
    {
        var vm = NewVm();
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == propName) fired = true; };
        typeof(DashboardViewModel).GetProperty(propName)!.SetValue(vm, value);
        Assert.True(fired);
    }
}
