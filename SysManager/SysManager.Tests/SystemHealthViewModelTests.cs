// SysManager · SystemHealthViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class SystemHealthViewModelTests
{
    private static SystemHealthViewModel NewVm() => new(new SystemInfoService());

    // ---------- construction ----------

    [Fact]
    public void Constructor_ConsoleNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_CollectionsNotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Modules);
        Assert.NotNull(vm.Disks);
        Assert.NotNull(vm.DiskHealth);
        Assert.NotNull(vm.ChkdskDrives);
    }

    [Fact]
    public void Constructor_IsElevated_IsBoolean()
    {
        var vm = NewVm();
        Assert.IsType<bool>(vm.IsElevated);
    }

    [Fact]
    public void Constructor_Summary_NonEmpty()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.Summary));
    }

    [Fact]
    public void Constructor_IsChkdskRunning_False()
    {
        var vm = NewVm();
        Assert.False(vm.IsChkdskRunning);
    }

    [Fact]
    public void Constructor_ChkdskStatus_Empty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.ChkdskStatus);
    }

    [Fact]
    public void Constructor_MemoryHealthVerdict_NonEmpty()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrWhiteSpace(vm.MemoryHealthVerdict));
    }

    [Fact]
    public void Constructor_MemoryHealthColorHex_IsHexColor()
    {
        var vm = NewVm();
        Assert.StartsWith("#", vm.MemoryHealthColorHex);
    }

    [Fact]
    public void Constructor_WheaMemoryErrors_Zero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.WheaMemoryErrors);
    }

    [Fact]
    public void Constructor_MemoryDiagnosticResults_Zero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.MemoryDiagnosticResults);
    }

    [Fact]
    public void Constructor_OsCpuMemory_Null()
    {
        var vm = NewVm();
        Assert.Null(vm.Os);
        Assert.Null(vm.Cpu);
        Assert.Null(vm.Memory);
    }

    // ---------- commands ----------

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("RefreshDrivesCommand")]
    [InlineData("CheckDiskHealthCommand")]
    [InlineData("CheckMemoryErrorsCommand")]
    [InlineData("ScheduleMemoryTestCommand")]
    [InlineData("OpenMemTest86Command")]
    [InlineData("RunChkdskCommand")]
    [InlineData("RunChkdskOnSelectedCommand")]
    [InlineData("CancelScanCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel ----------

    [Fact]
    public void CancelScanCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelScanCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelScanCommand_WithLiveCts_RequestsCancellation()
    {
        var vm = NewVm();
        var cts = new CancellationTokenSource();
        typeof(SystemHealthViewModel)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, cts);
        vm.CancelScanCommand.Execute(null);
        Assert.True(cts.IsCancellationRequested);
    }

    // ---------- RunChkdsk guard ----------

    [Fact]
    public async Task RunChkdsk_NullDrive_SetsStatusMessage()
    {
        var vm = NewVm();
        await vm.RunChkdskCommand.ExecuteAsync(null);
        Assert.Contains("No drive", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChkdsk_EmptyDrive_SetsStatusMessage()
    {
        var vm = NewVm();
        await vm.RunChkdskCommand.ExecuteAsync("");
        Assert.Contains("No drive", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChkdskOnSelected_NoneSelected_SetsStatusMessage()
    {
        var vm = NewVm();
        vm.ChkdskDrives.Clear();
        await vm.RunChkdskOnSelectedCommand.ExecuteAsync(null);
        Assert.Contains("Select at least", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- DriveTarget model ----------

    [Fact]
    public void DriveTarget_Defaults()
    {
        var dt = new DriveTarget();
        Assert.Equal("C:", dt.Letter);
        Assert.Equal("Idle", dt.Status);
        Assert.False(dt.IsSelected);
    }

    [Fact]
    public void DriveTarget_Display_WithLabel()
    {
        var dt = new DriveTarget { Letter = "D:", Label = "Data", SizeGB = 500, FileSystem = "NTFS" };
        Assert.Contains("D:", dt.Display);
        Assert.Contains("Data", dt.Display);
        Assert.Contains("500", dt.Display);
    }

    [Fact]
    public void DriveTarget_Display_WithoutLabel()
    {
        var dt = new DriveTarget { Letter = "C:", Label = "", SizeGB = 250, FileSystem = "NTFS" };
        Assert.Contains("C:", dt.Display);
        Assert.Contains("250", dt.Display);
    }

    [Fact]
    public void DriveTarget_Display_LabelSameAsLetter()
    {
        var dt = new DriveTarget { Letter = "C:", Label = "C:", SizeGB = 100, FileSystem = "NTFS" };
        // Should use the short format (no duplicate label)
        var display = dt.Display;
        Assert.DoesNotContain("C:  C:", display);
    }

    [Fact]
    public void DriveTarget_StatusSetter_FiresPropertyChanged()
    {
        var dt = new DriveTarget();
        var fired = false;
        dt.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(dt.Status)) fired = true; };
        dt.Status = "Running...";
        Assert.True(fired);
    }

    [Fact]
    public void DriveTarget_IsSelectedSetter_FiresPropertyChanged()
    {
        var dt = new DriveTarget();
        var fired = false;
        dt.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(dt.IsSelected)) fired = true; };
        dt.IsSelected = true;
        Assert.True(fired);
    }
}
