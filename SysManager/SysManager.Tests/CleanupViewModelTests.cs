// SysManager · CleanupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="CleanupViewModel"/> that don't touch the
/// real PowerShell runner, the WPF dispatcher, or spawn any processes.
/// Heavier end-to-end scenarios live in SysManager.IntegrationTests.
/// <para>Serialized because <c>SystemModificationLock_IsMutuallyExclusive</c> acquires the process-wide
/// <see cref="OperationLockService"/> and asserts which operation holds it. Without the attribute this
/// class ran fully in parallel with the other classes that take the same lock, so it could observe a
/// foreign operation's name — or be observed holding one.</para>
/// </summary>
[Collection("ProcessWideStatics")]
public class CleanupViewModelTests
{
    /// <summary>
    /// A pre-scan that answers instantly with fixed numbers.
    /// </summary>
    /// <remarks>
    /// Every test in this file builds a view-model, and the constructor fires the pre-scan. While that work
    /// was inline it meant 30 recursive walks of both temp folders and every per-SID Recycle Bin folder per
    /// run of this file, and one test asserted the walk finished inside fifteen seconds — a claim about the
    /// machine, not the code, which duly failed on a normally-used desktop while passing on a CI runner with
    /// an empty profile.
    /// </remarks>
    private static ICleanupPreScanService StubPreScan(
        string temp = "12.3 MB can be freed", string bin = "4.5 MB in Recycle Bin")
    {
        var preScan = Substitute.For<ICleanupPreScanService>();
        preScan.MeasureAsync().Returns(Task.FromResult(new CleanupPreScan(temp, bin)));
        return preScan;
    }

    /// <summary>A view-model over a substitute Recycle Bin unless a test hands it one, so none empties the real bin.</summary>
    private static CleanupViewModel NewVm(ICleanupPreScanService? preScan = null, IRecycleBin? recycleBin = null) =>
        new(new PowerShellRunner(), preScan ?? StubPreScan(), recycleBin ?? Substitute.For<IRecycleBin>());

    // ---------- construction & defaults ----------

    [Fact]
    public void Constructor_SetsConsoleInstance()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Console);
    }

    [Fact]
    public void Constructor_DefaultsAllRunningFlagsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsTempRunning);
        Assert.False(vm.IsBinRunning);
        Assert.False(vm.IsStoreRunning);
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void Constructor_DefaultsStatusStringsToIdle()
    {
        var vm = NewVm();
        Assert.Equal("Idle", vm.StoreStatus);
    }

    [Fact]
    public void Constructor_InitialStatusMessageIsEmpty()
    {
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void Constructor_IsElevated_ReturnsBoolean()
    {
        var vm = NewVm();
        // On CI and on most dev boxes this is false. We only assert the type
        // is stable so the UI binding never gets a null boxed value.
        Assert.IsType<bool>(vm.IsElevated);
    }

    // ---------- IsAnyRunning aggregation ----------

    [Theory]
    [InlineData(nameof(CleanupViewModel.IsTempRunning))]
    [InlineData(nameof(CleanupViewModel.IsBinRunning))]
    [InlineData(nameof(CleanupViewModel.IsStoreRunning))]
    public void IsAnyRunning_TurnsTrueWhenAnyFlagFlipsOn(string propName)
    {
        var vm = NewVm();
        var p = typeof(CleanupViewModel).GetProperty(propName)!;
        p.SetValue(vm, true);
        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void IsAnyRunning_FiresPropertyChangedOnEveryFlag()
    {
        var vm = NewVm();
        var seen = vm.RecordPropertyChanges();

        vm.IsTempRunning = true;
        vm.IsBinRunning = true;
        vm.IsStoreRunning = true;

        // OnIs*RunningChanged partial methods should have raised IsAnyRunning
        // each time — we only assert at least one fire here because the flag
        // does not flip false after staying true, and the first flip already
        // covers the partial method.
        Assert.Contains("IsAnyRunning", seen);
    }

    [Fact]
    public void IsAnyRunning_RemainsTrueWhileOneFlagStaysSet()
    {
        var vm = NewVm();
        vm.IsTempRunning = true;
        vm.IsBinRunning = true;
        vm.IsTempRunning = false;
        Assert.True(vm.IsAnyRunning);
        vm.IsBinRunning = false;
        Assert.False(vm.IsAnyRunning);
    }

    // ---------- commands exist ----------

    [Theory]
    [InlineData("CleanTempCommand")]
    [InlineData("EmptyRecycleBinCommand")]
    [InlineData("AnalyzeComponentStoreCommand")]
    [InlineData("CleanComponentStoreCommand")]
    [InlineData("CancelCommand")]
    [InlineData("RelaunchAsAdminCommand")]
    public void Command_IsExposedAndNotNull(string name)
    {
        var vm = NewVm();
        var prop = vm.GetType().GetProperty(name);
        Assert.NotNull(prop);
        Assert.NotNull(prop!.GetValue(vm));
    }

    // ---------- cancel behaviour ----------

    [Fact]
    public void CancelCommand_OnIdleVm_DoesNotThrow()
    {
        var vm = NewVm();
        var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CancelCommand_CanBeCalledRepeatedly()
    {
        var vm = NewVm();
        for (int i = 0; i < 5; i++)
        {
            var ex = Record.Exception(() => vm.CancelCommand.Execute(null));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void CancelCommand_RequestsCancellationOnLiveTokenSource()
    {
        var vm = NewVm();
        // Inject a live CTS through reflection so Cancel has something to hit.
        // This mirrors what the async commands do right before awaiting.
        var field = typeof(CleanupViewModel)
            .GetField("_tempCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var cts = new CancellationTokenSource();
        field.SetValue(vm, cts);

        vm.CancelCommand.Execute(null);

        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task CleanTemp_WhenAlreadyRunning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsTempRunning = true;
        vm.StatusMessage = "marker";

        await vm.CleanTempCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
    }

    [Fact]
    public async Task EmptyRecycleBin_WhenAlreadyRunning_ReturnsImmediately()
    {
        var vm = NewVm();
        vm.IsBinRunning = true;
        vm.StatusMessage = "marker";

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
    }

    // ---------- runner plumbing ----------

    [Fact]
    public void RunnerLineReceived_AppendsToConsole()
    {
        var runner = new PowerShellRunner();
        var vm = new CleanupViewModel(runner, StubPreScan(), Substitute.For<IRecycleBin>());
        var before = vm.Console.Lines.Count;

        // Simulate the runner emitting a line. The VM subscribes in its
        // constructor, so this should flow through to the console.
        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.LineReceived), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        // Event-backed field is generated by the compiler with the same name.
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);
        del!.DynamicInvoke(PowerShellLine.Output("hello from test"));

        Assert.Equal(before + 1, vm.Console.Lines.Count);
        Assert.Equal("hello from test", vm.Console.Lines[^1].Text);
    }

    [Fact]
    public void RunnerProgressChanged_UpdatesProgressProperty()
    {
        var runner = new PowerShellRunner();
        var vm = new CleanupViewModel(runner, StubPreScan(), Substitute.For<IRecycleBin>());

        var ev = typeof(PowerShellRunner)
            .GetField(nameof(PowerShellRunner.ProgressChanged), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var del = (MulticastDelegate?)ev?.GetValue(runner);
        Assert.NotNull(del);

        del!.DynamicInvoke(42);
        Assert.Equal(42, vm.Progress);

        del.DynamicInvoke(100);
        Assert.Equal(100, vm.Progress);
    }

    // ---------- shared-runner mutual exclusion ----------

    // Temp Cleanup, SFC, and DISM all stream through the single shared PowerShellRunner
    // into the one Console. Their per-category OperationLockService locks (Temp = Disk,
    // SFC/DISM = SystemModification) don't exclude Temp from SFC/DISM, so an intra-VM guard
    // does. This pins that guard's mutual-exclusion contract directly; the full command path
    // is gated behind elevation (SFC/DISM) or a live disk lock — both skipped or contended
    // under CI — so testing the primitive is the deterministic sibling of the
    // SystemModificationLock_IsMutuallyExclusive test below.
    [Fact]
    public void ConsoleRunnerGuard_IsMutuallyExclusive()
    {
        var vm = NewVm();
        Assert.True(vm.TryBeginConsoleOp());   // e.g. SFC claims the shared runner
        Assert.False(vm.TryBeginConsoleOp());  // Temp / DISM blocked while it is held
        vm.EndConsoleOp();
        Assert.True(vm.TryBeginConsoleOp());    // released — free to claim again
        vm.EndConsoleOp();
    }

    // ---------- base class properties (exercise ViewModelBase setters) ----------

    [Fact]
    public void StatusMessage_Setter_RaisesPropertyChanged()
    {
        var vm = NewVm();
        var fired = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusMessage)) fired = true;
        };
        vm.StatusMessage = "hello";
        Assert.True(fired);
        Assert.Equal("hello", vm.StatusMessage);
    }

    // Progress_AcceptsFullRange was removed: it looped five values through a bare
    // [ObservableProperty] and read each back. The name implied a 0-100 contract that nothing
    // enforces (ViewModelBase._progress is unclamped; the ProgressBar clamps visually).

    [Fact]
    public void IsProgressIndeterminate_TogglesCleanly()
    {
        var vm = NewVm();
        Assert.False(vm.IsProgressIndeterminate);
        vm.IsProgressIndeterminate = true;
        Assert.True(vm.IsProgressIndeterminate);
        vm.IsProgressIndeterminate = false;
        Assert.False(vm.IsProgressIndeterminate);
    }

    // ---------- pre-scan labels (added in v0.12.2) ----------

    /// <summary>
    /// Both size labels read "Scanning…" until the measurement lands, then hold what it measured.
    /// </summary>
    /// <remarks>
    /// One test where there were three, and it asserts more than they did.
    /// <para><c>TempSizeLabel_DefaultIsScanning</c> and <c>RecycleBinLabel_DefaultIsScanning</c> read the
    /// label straight after construction and were only ever right by luck: the pre-scan is fire-and-forget,
    /// so they were racing it and would have flipped the moment the measurement got fast.</para>
    /// <para><c>PreScan_EventuallyPopulatesLabels</c> polled <c>Task.Delay(500)</c> thirty times and then
    /// asserted the labels had changed — which is the assertion "this machine can enumerate the whole temp
    /// tree and Recycle Bin in fifteen seconds". A property of the machine, not the code. It failed on a
    /// normally-used desktop and passed on a CI runner with an empty profile (#2084).</para>
    /// <para>The gate replaces the clock: the measurement completes exactly when this test says so, and
    /// <c>InitializationComplete</c> — which <see cref="ViewModelBase"/> exposes for precisely this — replaces
    /// the polling. It also asserts the labels hold the MEASURED values, where the old test only asserted
    /// they were no longer "Scanning…" and would have passed on any garbage.</para>
    /// </remarks>
    [Fact]
    public async Task SizeLabels_ReadScanning_UntilTheMeasurementLands()
    {
        var gate = new TaskCompletionSource<CleanupPreScan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preScan = Substitute.For<ICleanupPreScanService>();
        preScan.MeasureAsync().Returns(gate.Task);
        var vm = NewVm(preScan);

        Assert.Equal("Scanning…", vm.TempSizeLabel);
        Assert.Equal("Scanning…", vm.RecycleBinLabel);

        gate.SetResult(new CleanupPreScan("412.7 MB can be freed", "18.0 MB in Recycle Bin"));
        await vm.InitializationComplete;

        Assert.Equal("412.7 MB can be freed", vm.TempSizeLabel);
        Assert.Equal("18.0 MB in Recycle Bin", vm.RecycleBinLabel);
    }

    // A "…_CanBeSetDirectly" round-trip was removed here: it set a bare [ObservableProperty]
    // and read it straight back, so only the source generator could fail it. The labels' real
    // behaviour is covered by SizeLabels_ReadScanning_UntilTheMeasurementLands above.
    // ---------- progress feedback (regression) ----------
    // CleanupView.xaml binds a progress bar to IsBusy and the sidebar spinner reads the same flag,
    // but this VM never assigned it — so nothing appeared while SFC or DISM ran, which is minutes of
    // work. IsBusy is now DERIVED from the four per-operation flags, so overlapping operations cannot
    // clear the bar out from under one another.

    [Fact]
    public void IsBusy_TracksAnyRunningOperation()
    {
        var vm = NewVm();
        vm.IsTempRunning = true;
        Assert.True(vm.IsBusy);

        vm.IsTempRunning = false;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void IsBusy_StaysSetWhileASecondOperationIsStillRunning()
    {
        // The failure a per-command `finally { IsBusy = false; }` would cause: two operations run,
        // the first finishes, and the bar disappears while the second is still going.
        var vm = NewVm();
        vm.IsTempRunning = true;
        vm.IsBinRunning = true;

        vm.IsTempRunning = false;

        Assert.True(vm.IsBusy);
        vm.IsBinRunning = false;
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void IsBusy_IsIndeterminateForTempButDeterminateForTheComponentStore()
    {
        // Temp/Recycle-Bin report no percentage, so the bar must be marquee. The component-store
        // operations DO report one (DISM's decimal percentage, parsed off the runner's output), and a
        // marquee bar there would throw that real number away.
        var vm = NewVm();

        vm.IsTempRunning = true;
        Assert.True(vm.IsProgressIndeterminate);
        vm.IsTempRunning = false;

        vm.IsStoreRunning = true;
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
        vm.IsStoreRunning = false;
    }

    [Fact]
    public void IsBusy_IsClearOnceEveryOperationHasFinished()
    {
        var vm = NewVm();
        vm.IsTempRunning = true;
        vm.IsBinRunning = true;
        vm.IsStoreRunning = true;

        vm.IsTempRunning = false;
        vm.IsBinRunning = false;
        vm.IsStoreRunning = false;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void Construction_LeavesTheProgressBarOff()
    {
        // The startup pre-scan runs with reportProgress: false, so it never touches the flag and
        // construction has no visible side effect. That is the contract the older
        // IsProgressIndeterminate_TogglesCleanly test depends on, restated here so the reason is
        // discoverable from the progress-feedback tests too.
        //
        // It matters that this holds UNCONDITIONALLY rather than by timing: the scan is
        // fire-and-forget from the constructor, so an approach that raised the flag "after the first
        // yield" left the observed value depending on whether that continuation resumed before the
        // constructor returned — it passed locally and failed on CI. The startup scan's progress is
        // already visible in the "Scanning…" size labels; only the user-pressed Rescan drives the bar.
        var vm = NewVm();

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    // ---------- the Recycle Bin must not claim a success it did not get ----------
    //
    // Driven through a substitute bin. These were a single check on the source, because the command called the shell
    // itself and driving it would have emptied the Recycle Bin of the PC running the tests.

    /// <summary>A bin whose empty reports <paramref name="result"/>.</summary>
    private static IRecycleBin Bin(RecycleBinEmptyResult result)
    {
        var bin = Substitute.For<IRecycleBin>();
        bin.Empty().Returns(result);
        return bin;
    }

    [Fact]
    public async Task EmptyRecycleBin_WhenWindowsRefuses_SaysSo_AndRecordsNothing()
    {
        // A refusal (the bin open in Explorer, a file still locked) comes back in the result, never as an exception.
        // The result used to be discarded, so the status read "Done" over a bin that was still full.
        using var activity = new ActivityLogScope();
        using var confirm = new DialogAnswer(confirm: true);
        var vm = NewVm(recycleBin: Bin(new RecycleBinEmptyResult(Emptied: false, new RecycleBinContents(4096, 2))));

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        Assert.Equal("Could not empty the Recycle Bin — Windows refused the request.", vm.StatusMessage);
        Assert.Empty(ActivityLogService.Instance.GetRecent(10));
    }

    [Fact]
    public async Task EmptyRecycleBin_OfAnEmptyBin_SaysItWasAlreadyEmpty()
    {
        // It used to ask the shell to empty it anyway, and the code the shell returns for an empty bin is not one it
        // documents, so this could end in "Windows refused the request" (#2800).
        using var activity = new ActivityLogScope();
        using var confirm = new DialogAnswer(confirm: true);
        var vm = NewVm(recycleBin: Bin(new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(0, 0))));

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        Assert.Equal("The Recycle Bin was already empty.", vm.StatusMessage);
        Assert.Empty(ActivityLogService.Instance.GetRecent(10));
    }

    [Fact]
    public async Task EmptyRecycleBin_RecordsWhatWentInRecentActivity()
    {
        // Nothing recorded it, so someone looking later for a file they had deleted found no trace that SysManager
        // had emptied the bin.
        using var activity = new ActivityLogScope();
        using var confirm = new DialogAnswer(confirm: true);
        var vm = NewVm(recycleBin: Bin(new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(3L << 30, 12))));

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        Assert.Equal("Done", vm.StatusMessage);
        var entry = Assert.Single(ActivityLogService.Instance.GetRecent(10));
        Assert.Equal(("Quick Cleanup", "Permanently emptied the Recycle Bin (12 items, 3.0 GB)"), (entry.Action, entry.Detail));
    }

    [Fact]
    public void TheRecordOfAnEmptiedBin_StillSaysSo_WhenTheShellCouldNotSayWhatItHeld()
        => Assert.Equal("Permanently emptied the Recycle Bin", CleanupViewModel.DescribeEmptied(null));

    // ---------- Empty Recycle Bin takes the Disk lock (#2891) ----------

    [Fact]
    public async Task EmptyRecycleBin_WhileAnotherDeletionHoldsTheDiskLock_EmptiesNothing()
    {
        using var confirm = new DialogAnswer(confirm: true);
        var bin = Substitute.For<IRecycleBin>();
        var vm = NewVm(recycleBin: bin);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.Disk, "Deep Cleanup");
        Assert.NotNull(held);

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        bin.DidNotReceive().Empty();
        Assert.Equal("Cannot start — Deep Cleanup is already running.", vm.StatusMessage);
        Assert.False(vm.IsBinRunning);
    }

    [Fact]
    public async Task EmptyRecycleBin_HoldsTheDiskLockWhileItEmpties_SoClosingNamesIt()
    {
        // Without the lock, closing SysManager while the bin was being emptied asked nothing and stopped it part-way.
        using var confirm = new DialogAnswer(confirm: true);
        string? heldBy = null;
        var warning = "";
        var bin = Substitute.For<IRecycleBin>();
        bin.Empty().Returns(_ =>
        {
            heldBy = OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk);
            warning = QuitGuard.ActiveWorkWarning();
            return new RecycleBinEmptyResult(Emptied: true, new RecycleBinContents(0, 0));
        });
        var vm = NewVm(recycleBin: bin);

        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);

        Assert.Equal("Empty Recycle Bin", heldBy);
        Assert.Contains("Empty Recycle Bin", warning, StringComparison.Ordinal);
        Assert.Null(OperationLockService.Instance.GetActiveOperationName(OperationCategory.Disk));
    }

    // ---------- Escape (#2597) ----------

    [Theory]
    [InlineData(nameof(CleanupViewModel.IsTempRunning))]
    [InlineData(nameof(CleanupViewModel.IsBinRunning))]
    [InlineData(nameof(CleanupViewModel.IsStoreRunning))]
    public async Task Escape_CancelsWhileAnyOfTheThreeRuns(string running)
    {
        var vm = NewVm();
        await vm.InitializationComplete;
        Assert.Null(vm.EscapeCancel);

        typeof(CleanupViewModel).GetProperty(running)!.SetValue(vm, true);

        Assert.Same(vm.CancelCommand, vm.EscapeCancel);
    }
}
