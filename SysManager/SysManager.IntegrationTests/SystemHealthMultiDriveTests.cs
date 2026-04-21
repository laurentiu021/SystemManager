using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class SystemHealthMultiDriveTests
{
    private static SystemHealthViewModel Build() => new(new SystemInfoService());

    [Fact]
    public void ChkdskDrives_Collection_Exists()
    {
        var vm = Build();
        Assert.NotNull(vm.ChkdskDrives);
    }

    [Fact]
    public void IsChkdskRunning_DefaultsFalse()
    {
        var vm = Build();
        Assert.False(vm.IsChkdskRunning);
    }

    [Fact]
    public void ChkdskStatus_DefaultsEmpty()
    {
        var vm = Build();
        Assert.Equal(string.Empty, vm.ChkdskStatus);
    }

    [Theory]
    [InlineData("RunChkdskCommand")]
    [InlineData("RunChkdskOnSelectedCommand")]
    [InlineData("RefreshDrivesCommand")]
    [InlineData("CancelScanCommand")]
    public void ChkdskCommands_Exist(string propName)
    {
        var vm = Build();
        var p = vm.GetType().GetProperty(propName);
        Assert.NotNull(p);
        Assert.NotNull(p!.GetValue(vm));
    }

    [Fact]
    public async Task RefreshDrives_PopulatesList()
    {
        var vm = Build();
        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        Assert.NotEmpty(vm.ChkdskDrives);
    }

    [Fact]
    public async Task RefreshDrives_CSelectedByDefault()
    {
        var vm = Build();
        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        var c = vm.ChkdskDrives.FirstOrDefault(d => string.Equals(d.Letter, "C:", StringComparison.OrdinalIgnoreCase));
        if (c != null) Assert.True(c.IsSelected);
    }

    [Fact]
    public async Task RunChkdskOnSelected_NoneSelected_SetsMessage()
    {
        var vm = Build();
        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        foreach (var d in vm.ChkdskDrives) d.IsSelected = false;

        var t2 = vm.RunChkdskOnSelectedCommand.ExecuteAsync(null);
        if (t2 is Task tt2) await tt2;
        Assert.Contains("Select", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChkdsk_NullDriveLetter_NoOp()
    {
        var vm = Build();
        var t = vm.RunChkdskCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        Assert.False(vm.IsChkdskRunning);
    }

    [Fact]
    public void DriveTarget_Defaults()
    {
        var d = new DriveTarget();
        Assert.Equal("C:", d.Letter);
        Assert.Equal("Idle", d.Status);
        Assert.Equal("NTFS", d.FileSystem);
        Assert.False(d.IsSelected);
    }

    [Fact]
    public void DriveTarget_Display_IncludesSize()
    {
        var d = new DriveTarget { Letter = "D:", Label = "Data", SizeGB = 500, FileSystem = "NTFS" };
        Assert.Contains("500", d.Display);
        Assert.Contains("NTFS", d.Display);
    }

    [Fact]
    public void DriveTarget_Display_WhenLabelEqualsLetter_Simpler()
    {
        var d = new DriveTarget { Letter = "C:", Label = "C:", SizeGB = 500, FileSystem = "NTFS" };
        Assert.DoesNotContain("  C:  ", d.Display); // no duplicated label
    }

    [Fact]
    public void DriveTarget_Mutable_Status()
    {
        var d = new DriveTarget { Status = "Running..." };
        Assert.Equal("Running...", d.Status);
    }

    [Fact]
    public void DriveTarget_Selection_RaisesChange()
    {
        var d = new DriveTarget();
        var fired = false;
        d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(d.IsSelected)) fired = true; };
        d.IsSelected = true;
        Assert.True(fired);
    }

    [Fact]
    public void DriveTarget_Status_RaisesChange()
    {
        var d = new DriveTarget();
        var fired = false;
        d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(d.Status)) fired = true; };
        d.Status = "Done";
        Assert.True(fired);
    }
}
