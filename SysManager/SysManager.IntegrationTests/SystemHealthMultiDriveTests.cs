// SysManager · SystemHealthMultiDriveTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.IntegrationTests;

public class SystemHealthMultiDriveTests
{
    private static SystemHealthViewModel Build() => new(new SystemInfoService(), new DiskHealthService(), new MemoryTestService(), new FixedDriveService(), new PowerShellRunner(), new BiosService());

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
        // Await the CONSTRUCTOR's own drive refresh, not just the explicit command. Without this the
        // constructor's refresh could land after the deselect below and re-tick C:, and then this test —
        // named NoneSelected — ran a real chkdsk. It did, once: 5m36s, which took the whole non-blocking
        // integration job from ~5 minutes to 25m43s (#2300). The re-ticking is fixed in the view model;
        // this await removes the race that made it reachable at all.
        await vm.InitializationComplete;
        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;
        foreach (var d in vm.ChkdskDrives) d.IsSelected = false;

        var t2 = vm.RunChkdskOnSelectedCommand.ExecuteAsync(null);
        if (t2 is Task tt2) await tt2;
        Assert.Contains("Select", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshDrives_KeepsEverythingDeselected_RatherThanReTickingC()
    {
        // THE regression test for #2300. RefreshDrivesAsync rebuilt the collection and hard-coded
        // IsSelected to C: every time, and it runs on far more than first load — ScanAsync calls it and
        // RefreshOnF5 is ScanCommand. So a user who unticked C: and pressed F5 got it silently re-ticked,
        // and the next "Run chkdsk on selected" ran on a drive they had deselected.
        //
        // Deselecting everything is the case that works on any machine, including a CI runner with one
        // drive, and it is the one that distinguishes the fix: preserving a NON-EMPTY selection looks
        // identical to the old behaviour whenever the selection happens to be C:.
        var vm = Build();
        await vm.InitializationComplete;
        Assert.NotEmpty(vm.ChkdskDrives);

        foreach (var d in vm.ChkdskDrives) d.IsSelected = false;

        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;

        Assert.NotEmpty(vm.ChkdskDrives);   // the refresh really repopulated, so this is not vacuous
        Assert.DoesNotContain(vm.ChkdskDrives, d => d.IsSelected);
    }

    [Fact]
    public async Task RefreshDrives_KeepsANonDefaultSelection()
    {
        // The other half: a tick the user placed must survive. On a single-drive runner this degenerates
        // to "C: stays ticked", which the old code also did — so the assertion below is written to be
        // meaningful either way by comparing against what was selected BEFORE the refresh rather than
        // against the letter C:.
        var vm = Build();
        await vm.InitializationComplete;
        Assert.NotEmpty(vm.ChkdskDrives);

        // Tick the last drive and untick the rest. With one drive that is C:; with two or more it is
        // deliberately not the default.
        foreach (var d in vm.ChkdskDrives) d.IsSelected = false;
        vm.ChkdskDrives[^1].IsSelected = true;
        var chosen = vm.ChkdskDrives[^1].Letter;

        var t = vm.RefreshDrivesCommand.ExecuteAsync(null);
        if (t is Task tt) await tt;

        var selected = vm.ChkdskDrives.Where(d => d.IsSelected).Select(d => d.Letter).ToList();
        Assert.Equal([chosen], selected);
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

    // DriveTarget_Display_IncludesSize / _WhenLabelEqualsLetter_Simpler were removed with DriveTarget.Display
    // (#2100). No view bound it — the chkdsk row composes that line from the individual properties — so these
    // two, plus three more in the unit suite, were what made an unreachable property look covered.

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
