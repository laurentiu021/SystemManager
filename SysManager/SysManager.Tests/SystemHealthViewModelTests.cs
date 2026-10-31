// SysManager · SystemHealthViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Reflection;
using System.Text;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the chkdsk gate tests replace AdminHelper.ElevationProbe, which is process-wide state.
// Required by ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection, which caught this.
[Collection("ProcessWideStatics")]
public class SystemHealthViewModelTests
{
    private static SystemHealthViewModel NewVm(MemoryTestService? memTest = null) => new(new SystemInfoService(), new DiskHealthService(), memTest ?? new MemoryTestService(), new FixedDriveService(), new PowerShellRunner(), new BiosService());

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
    public void Constructor_MemoryHealthColorHex_IsAThemeResourceKey()
    {
        // Inverted deliberately: this used to require a leading '#'. The verdict colour now names a
        // theme brush so it follows the active preset — a hex literal is exactly what made these
        // verdicts illegible on the light themes.
        var vm = NewVm();
        Assert.DoesNotMatch("^#", vm.MemoryHealthColorHex);
        Assert.False(string.IsNullOrWhiteSpace(vm.MemoryHealthColorHex));
    }

    [Fact]
    public void Constructor_WheaMemoryErrors_Zero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.WheaMemoryErrors);
    }

    [Fact]
    public void Constructor_MemoryDiagnosticErrors_Zero()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.MemoryDiagnosticErrors);
    }

    [Fact]
    public void Constructor_OsCpuMemory_Null()
    {
        var vm = NewVm();
        Assert.Null(vm.Os);
        Assert.Null(vm.Cpu);
        Assert.Null(vm.Memory);
    }

    // ---------- the memory check that could not read the log (#2479) ----------

    [Fact]
    public async Task CheckMemoryErrors_WhenTheLogCannotBeRead_SaysItCouldNotCheck()
    {
        // The service returned zero errors for a log it could not read, so the verdict read "No memory errors
        // reported in the last 30 days." in green. It now throws, and the verdict says the check could not run,
        // in the warning colour, instead of keeping whatever it said before.
        var vm = NewVm(new MemoryTestService(MemoryTestServiceTests.NoSuchLog));

        await vm.CheckMemoryErrorsCommand.ExecuteAsync(null);

        Assert.Contains("could not be checked", vm.MemoryHealthVerdict, StringComparison.Ordinal);
        Assert.DoesNotContain("No memory errors", vm.MemoryHealthVerdict, StringComparison.Ordinal);
        Assert.Equal(StatusColors.Warning, vm.MemoryHealthColorHex);
        Assert.False(vm.IsBusy);
    }

    // ---------- commands ----------

    [Theory]
    [InlineData("ScanCommand")]
    [InlineData("RefreshDrivesCommand")]
    [InlineData("CheckDiskHealthCommand")]
    [InlineData("CheckMemoryErrorsCommand")]
    [InlineData("ScheduleMemoryTestCommand")]
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
        await vm.InitializationComplete;
        await vm.RunChkdskCommand.ExecuteAsync(null);
        Assert.Contains("No drive", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChkdsk_EmptyDrive_SetsStatusMessage()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        await vm.RunChkdskCommand.ExecuteAsync("");
        Assert.Contains("No drive", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChkdskOnSelected_NoneSelected_SetsStatusMessage()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
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

    // DriveTarget_Display_WithLabel / _WithoutLabel / _LabelSameAsLetter were removed with the property
    // they tested (#2100). All three asserted the composition of a string no view ever bound — the chkdsk
    // row builds that line from Letter, Label, SizeGB and FileSystem individually — so they were the reason
    // an unreachable property survived three audits: a property with tests looks covered.
    // What the row actually shows is pinned by ArchitectureTests.EveryFieldTheChkdskPickerFillsIn_IsShownInItsRow.

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

    // ---------- ParseChkdskVerdict ----------

    [Fact]
    public void ParseChkdskVerdict_FoundNoProblems_ReturnsHealthy()
    {
        var lines = new[] { "Windows has scanned the file system and found no problems." };
        Assert.Equal("Healthy", SystemHealthViewModel.ParseChkdskVerdict(lines, 0));
    }

    [Fact]
    public void ParseChkdskVerdict_FoundNoProblems_NonZeroExit_StillHealthy()
    {
        var lines = new[] { "Windows has scanned the file system and found no problems.", "No further action is required." };
        Assert.Equal("Healthy", SystemHealthViewModel.ParseChkdskVerdict(lines, 1));
    }

    [Fact]
    public void ParseChkdskVerdict_NoErrors_ReturnsHealthy()
    {
        var lines = new[] { "Scan complete. No errors found." };
        Assert.Equal("Healthy", SystemHealthViewModel.ParseChkdskVerdict(lines, 0));
    }

    [Fact]
    public void ParseChkdskVerdict_MadeCorrections_ReturnsRepaired()
    {
        var lines = new[] { "Windows has made corrections to the file system." };
        Assert.Equal("Repaired", SystemHealthViewModel.ParseChkdskVerdict(lines, 1));
    }

    [Fact]
    public void ParseChkdskVerdict_NotSupported_ReturnsNotSupported()
    {
        var lines = new[] { "The /scan option is not supported for FAT32 volumes." };
        Assert.Equal("Not supported", SystemHealthViewModel.ParseChkdskVerdict(lines, 1));
    }

    [Fact]
    public void ParseChkdskVerdict_EmptyOutput_ExitZero_ReturnsHealthy()
    {
        string[] lines = [];
        Assert.Equal("Healthy", SystemHealthViewModel.ParseChkdskVerdict(lines, 0));
    }

    [Fact]
    public void ParseChkdskVerdict_EmptyOutput_NonZeroExit_ReturnsExitCode()
    {
        string[] lines = [];
        Assert.Equal("Exit 3", SystemHealthViewModel.ParseChkdskVerdict(lines, 3));
    }

    // ── Elevation gate on chkdsk ─────────────────────────────────────────────
    //
    // Both chkdsk entry points refuse without administrator rights, and neither refusal was asserted
    // (#2171). Only the refusal is covered, deliberately: unlike the other gated commands in the app
    // these have NO confirmation dialog to decline, so past the gate they start a real chkdsk scan on a
    // real volume. These are built on the real runner, so they enter only the refusal; the tests at the end of
    // this file that enter the elevated branch hand the view model a substitute runner instead.
    //
    // The per-drive status is part of the assertion. Marking each selected drive "Needs admin" is what
    // tells the user WHICH drives were skipped.

    /// <summary>
    /// The batch command refuses before it starts, so the tab never claims a scan finished.
    /// </summary>
    /// <remarks>
    /// <c>ChkdskStatus</c> is the assertion that matters here, and the first draft did not have it. There
    /// are TWO gates on this path — one in <c>RunChkdskOnSelectedAsync</c> and one in the per-drive
    /// <c>RunChkdskCoreAsync</c> it calls — so asserting only the message and the drive's status passed
    /// with the outer gate DELETED: the inner one set both. The mutation proving that is what found it.
    /// <para>What the outer gate actually prevents is the report. Without it the command sets
    /// <c>IsChkdskRunning</c>, walks the selection, has every drive refused one by one, and then writes
    /// "All scans finished." — telling the user their disks were checked when nothing was read. The inner
    /// gate cannot prevent that, because by then the loop is already running.</para>
    /// </remarks>
    [StaFact]
    public async Task RunChkdskOnSelected_WhenNotElevated_RefusesWithoutClaimingAScanRan()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm();
        await vm.InitializationComplete;
        var drive = new DriveTarget { Letter = "C:", Label = "Windows", IsSelected = true };
        vm.ChkdskDrives.Add(drive);

        await vm.RunChkdskOnSelectedCommand.ExecuteAsync(null);

        Assert.Contains("admin", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Needs admin", drive.Status);
        Assert.False(vm.IsChkdskRunning, "the refusal must not leave the tab looking busy");
        Assert.Equal("", vm.ChkdskStatus);
        Assert.DoesNotContain("finished", vm.ChkdskStatus, StringComparison.OrdinalIgnoreCase);
    }

    [StaFact]
    public async Task RunChkdsk_SingleDrive_WhenNotElevated_SaysSo()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm();
        await vm.InitializationComplete;

        await vm.RunChkdskCommand.ExecuteAsync("D:");

        Assert.Contains("admin", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("D:", vm.StatusMessage);
        Assert.False(vm.IsChkdskRunning);
    }

    // ---------- Escape (#2597) ----------

    [Fact]
    public async Task Escape_CancelsChkdsk_AndNotTheSystemScan_WhichCancelDoesNotStop()
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        Assert.Null(vm.EscapeCancel);

        vm.IsBusy = true;
        Assert.Null(vm.EscapeCancel);

        vm.IsChkdskRunning = true;
        Assert.Same(vm.CancelScanCommand, vm.EscapeCancel);
    }

    // ---------- a running chkdsk on the sidebar, the taskbar and the exit prompts (#2876) ----------
    // chkdsk set only IsChkdskRunning, so a run of several minutes showed on neither the sidebar nor the taskbar
    // button, and took no lock, so closing SysManager meanwhile asked nothing and cut it off.
    //
    // A run waiting for its chkdsk holds the Disk lock, which is process-wide. So each test ends its own run even when
    // an assertion fails, or every test after it would find the lock taken, and every wait is bounded, so a run that
    // never ends fails the test instead of hanging the suite.

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A view model over the real read-only services and a runner whose chkdsk waits for <paramref name="chkdsk"/>.
    /// </summary>
    private static SystemHealthViewModel WithChkdsk(TaskCompletionSource<int> chkdsk, IPowerShellRunner? runner = null)
    {
        runner ??= Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync("chkdsk.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<Encoding?>())
              .Returns(chkdsk.Task);
        return new(new SystemInfoService(), new DiskHealthService(), new MemoryTestService(), new FixedDriveService(),
                   runner, new BiosService());
    }

    [Fact]
    public async Task AChkdskRun_ShowsTheTabBusy_AndAsksBeforeSysManagerCloses()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var chkdsk = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = WithChkdsk(chkdsk);
        await vm.InitializationComplete.WaitAsync(Bound);

        var run = vm.RunChkdskCommand.ExecuteAsync("C:");
        try
        {
            Assert.True(vm.IsBusy);
            Assert.True(vm.IsProgressIndeterminate);
            Assert.Contains("Disk check (chkdsk)", QuitGuard.ActiveWorkWarning(), StringComparison.Ordinal);
        }
        finally
        {
            chkdsk.TrySetResult(0);
            await run.WaitAsync(Bound);
        }

        Assert.False(vm.IsBusy);
        Assert.False(OperationLockService.Instance.IsLocked(OperationCategory.Disk));
    }

    [Fact]
    public async Task AReadThatEndsBesideARunningChkdsk_LeavesTheTabBusy()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var chkdsk = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = WithChkdsk(chkdsk);
        await vm.InitializationComplete.WaitAsync(Bound);
        var run = vm.RunChkdskCommand.ExecuteAsync("C:");
        try
        {
            await vm.CheckMemoryErrorsCommand.ExecuteAsync(null).WaitAsync(Bound);

            Assert.True(vm.IsBusy);   // chkdsk still runs
        }
        finally
        {
            chkdsk.TrySetResult(0);
            await run.WaitAsync(Bound);
        }

        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Chkdsk_WhileAnotherDiskJobRuns_SaysSo_AndDoesNotStart()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var chkdsk = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = WithChkdsk(chkdsk, runner);
        await vm.InitializationComplete.WaitAsync(Bound);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Large File Scan");
        Assert.NotNull(held);

        // A refusal ends before anything is awaited. A run that started anyway waits for chkdsk, so it is ended here.
        var run = vm.RunChkdskCommand.ExecuteAsync("C:");
        try
        {
            Assert.Equal("Cannot start — Large File Scan is already running.", vm.StatusMessage);
            Assert.False(vm.IsChkdskRunning);
        }
        finally
        {
            chkdsk.TrySetResult(0);
            await run.WaitAsync(Bound);
        }

        await runner.DidNotReceive().RunProcessAsync("chkdsk.exe", Arg.Any<string>(), Arg.Any<CancellationToken>(),
                                                     Arg.Any<Encoding?>());
    }

    // ---------- the memory verdict (#2715) ----------
    // A memory test result that found errors read "Memory diagnostic has run N time(s) recently. Check results." in the
    // warning colour, and it was counting tests that found none.

    [Fact]
    public void TheMemoryVerdict_ForATestThatFoundErrors_SaysSo_InTheBadColour()
    {
        var (verdict, colour) = SystemHealthViewModel.DescribeMemoryHealth(new MemoryTestService.MemoryErrorSummary(0, 1, null));

        Assert.Equal("The memory test found errors in the last 30 days. Your RAM may be faulty.", verdict);
        Assert.Equal(StatusColors.Bad, colour);
    }

    [Fact]
    public void TheMemoryVerdict_ForMemoryHardwareErrors_SaysToTestTheRam()
    {
        var (verdict, colour) = SystemHealthViewModel.DescribeMemoryHealth(new MemoryTestService.MemoryErrorSummary(2, 0, null));

        Assert.Equal("2 memory hardware error event(s) in the last 30 days. Test your RAM.", verdict);
        Assert.Equal(StatusColors.Bad, colour);
    }

    [Fact]
    public void TheMemoryVerdict_WithNoErrors_IsGood()
    {
        var (verdict, colour) = SystemHealthViewModel.DescribeMemoryHealth(new MemoryTestService.MemoryErrorSummary(0, 0, null));

        Assert.Equal("No memory errors reported in the last 30 days.", verdict);
        Assert.Equal(StatusColors.Good, colour);
    }
}
