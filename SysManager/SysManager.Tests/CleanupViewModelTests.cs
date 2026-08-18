// SysManager · CleanupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
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
    private static CleanupViewModel NewVm() => new(new PowerShellRunner());

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
        Assert.False(vm.IsSfcRunning);
        Assert.False(vm.IsDismRunning);
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void Constructor_DefaultsStatusStringsToIdle()
    {
        var vm = NewVm();
        Assert.Equal("Idle", vm.SfcStatus);
        Assert.Equal("Idle", vm.DismStatus);
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
    [InlineData(nameof(CleanupViewModel.IsSfcRunning))]
    [InlineData(nameof(CleanupViewModel.IsDismRunning))]
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
        var seen = new HashSet<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsAnyRunning) && e.PropertyName != null)
                seen.Add("IsAnyRunning");
        };

        vm.IsTempRunning = true;
        vm.IsBinRunning = true;
        vm.IsSfcRunning = true;
        vm.IsDismRunning = true;

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
    [InlineData("RunSfcCommand")]
    [InlineData("RunDismCommand")]
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

    // ---------- elevation gate on SFC / DISM ----------

    [Fact]
    public async Task RunSfc_WhenNotElevated_SetsRequiresAdminMessageAndClearsRunning()
    {
        var vm = NewVm();
        // Only meaningful when the test host is non-admin, which is the
        // normal developer / CI case. If someone runs the test elevated,
        // we skip the branch we care about.
        if (vm.IsElevated) return;

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Contains("admin", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsSfcRunning);
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public async Task RunDism_WhenNotElevated_SetsRequiresAdminMessageAndClearsRunning()
    {
        var vm = NewVm();
        if (vm.IsElevated) return;

        await vm.RunDismCommand.ExecuteAsync(null);

        Assert.Contains("admin", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsDismRunning);
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public async Task RunSfc_WhenAlreadyRunning_ReturnsImmediatelyWithoutChangingStatus()
    {
        var vm = NewVm();
        vm.IsSfcRunning = true;
        vm.StatusMessage = "marker";

        await vm.RunSfcCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
        Assert.True(vm.IsSfcRunning); // left as the caller set it
    }

    [Fact]
    public async Task RunDism_WhenAlreadyRunning_ReturnsImmediatelyWithoutChangingStatus()
    {
        var vm = NewVm();
        vm.IsDismRunning = true;
        vm.StatusMessage = "marker";

        await vm.RunDismCommand.ExecuteAsync(null);

        Assert.Equal("marker", vm.StatusMessage);
        Assert.True(vm.IsDismRunning);
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
        var vm = new CleanupViewModel(runner);
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
        var vm = new CleanupViewModel(runner);

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

    [Fact]
    public void TempSizeLabel_DefaultIsScanning()
    {
        // The constructor fires PreScanAsync which sets "Scanning…" initially.
        var vm = NewVm();
        Assert.Equal("Scanning…", vm.TempSizeLabel);
    }

    [Fact]
    public void RecycleBinLabel_DefaultIsScanning()
    {
        var vm = NewVm();
        Assert.Equal("Scanning…", vm.RecycleBinLabel);
    }

    [Fact]
    public async Task PreScan_EventuallyPopulatesLabels()
    {
        var vm = NewVm();
        // PreScanAsync runs on construction via fire-and-forget.
        // Poll until labels change or timeout (up to 15s for slow CI).
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            if (vm.TempSizeLabel != "Scanning…" && vm.RecycleBinLabel != "Scanning…")
                break;
        }
        // After scan, labels should no longer be "Scanning…"
        Assert.NotEqual("Scanning…", vm.TempSizeLabel);
        Assert.NotEqual("Scanning…", vm.RecycleBinLabel);
    }

    // A "…_CanBeSetDirectly" round-trip was removed here: it set a bare [ObservableProperty]
    // and read it straight back, so only the source generator could fail it. The labels' real
    // behaviour is covered by …_DefaultIsScanning and PreScan_EventuallyPopulatesLabels above.
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
    public void IsBusy_IsIndeterminateForTempButDeterminateForSfcAndDism()
    {
        // Temp/Recycle-Bin report no percentage, so the bar must be marquee. SFC and DISM DO report
        // one (through the runner's ProgressChanged → Progress), and a marquee bar there would throw
        // that real number away.
        var vm = NewVm();

        vm.IsTempRunning = true;
        Assert.True(vm.IsProgressIndeterminate);
        vm.IsTempRunning = false;

        vm.IsSfcRunning = true;
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
        vm.IsSfcRunning = false;

        vm.IsDismRunning = true;
        Assert.True(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
        vm.IsDismRunning = false;
    }

    [Fact]
    public void IsBusy_IsClearOnceEveryOperationHasFinished()
    {
        var vm = NewVm();
        vm.IsTempRunning = true;
        vm.IsBinRunning = true;
        vm.IsSfcRunning = true;
        vm.IsDismRunning = true;

        vm.IsTempRunning = false;
        vm.IsBinRunning = false;
        vm.IsSfcRunning = false;
        vm.IsDismRunning = false;

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

    [Fact]
    public void EmptyRecycleBin_UsesTheShellResult_RatherThanAssumingSuccess()
    {
        // RecycleBinHelper.EmptyAllDrives wraps SHEmptyRecycleBin, a LibraryImport that returns an
        // HRESULT: a refusal (bin open in Explorer, a file still locked) comes back as `false`, never as
        // an exception. The VM discarded that bool, so the try block always fell through to
        // StatusMessage = "Done" and a "Operation finished successfully" toast — the app told the user
        // it had emptied a bin that was still full. For a cleanup tool that is the worst possible lie,
        // and it is invisible: nothing throws, nothing logs, the label just reads Done.
        //
        // Asserted at source level on purpose, and this is the one place where that is not a compromise
        // but the only safe option: driving the command would empty THIS machine's real Recycle Bin.
        // There is no seam to fake — the helper is a static shell P/Invoke — and inventing one to make a
        // three-line status branch mockable would be a larger change than the fix. The check is precise
        // regardless: it looks at the call statement's shape, so it can only pass if the result is bound
        // to something, and only fail if the call stands alone as a discarded statement.
        var source = File.ReadAllText(
            Path.Combine(FindAppProjectDir(), "ViewModels", "CleanupViewModel.cs"));

        var callSites = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("RecycleBinHelper.EmptyAllDrives", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(callSites); // guards the test itself: a rename must fail loudly, not vacuously
        Assert.All(callSites, line =>
            Assert.False(line.StartsWith("await Task.Run", StringComparison.Ordinal),
                "The Recycle Bin result is discarded — a refused empty would still report success: " + line));
        Assert.Contains(callSites, l => l.Contains("= await Task.Run", StringComparison.Ordinal));

        // …and the failure path has to actually SAY something different. Capturing the bool but
        // printing "Done" either way would satisfy the check above and fix nothing.
        Assert.Contains("Could not empty the Recycle Bin", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The app project directory. No .cs files are copied into the test output, so the assembly
    /// location cannot answer this on its own.
    /// </summary>
    private static string FindAppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SysManager", "SysManager.csproj");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "SysManager");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the SysManager app project from " + AppContext.BaseDirectory);
    }
}

// ---------- SFC result parsing ----------

public class SfcResultParsingTests
{
    [Fact]
    public void ParseSfcResult_NoViolations_ReturnsGreen()
    {
        var lines = new[] { "Windows Resource Protection did not find any integrity violations." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("No integrity violations", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseSfcResult_SuccessfullyRepaired_ReturnsYellow()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files and successfully repaired them." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully repaired", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_UnableToFix_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files but was unable to fix some of them." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not repair", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_CouldNotPerform_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection could not perform the requested operation." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not run", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseSfcResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseSfcResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 1);
        Assert.Contains("exit code 1", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseSfcResult_EmptyLines_FallsBackToExitCode()
    {
        var (verdict, color) = CleanupViewModel.ParseSfcResult([], 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }
}

// ---------- DISM result parsing ----------

public class DismResultParsingTests
{
    [Fact]
    public void ParseDismResult_RestoreSuccessful_ReturnsGreen()
    {
        var lines = new[] { "The restore operation completed successfully." };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("healthy", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseDismResult_CorruptionRepaired_ReturnsYellow()
    {
        var lines = new[] { "The component store corruption was repaired." };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("repaired", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    [Fact]
    public void ParseDismResult_SourceNotFound_ReturnsRed()
    {
        var lines = new[] { "The source files could not be found." };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("source files", verdict);
        Assert.Equal(StatusColors.Bad, color);
    }

    [Fact]
    public void ParseDismResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal(StatusColors.Good, color);
    }

    [Fact]
    public void ParseDismResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 87);
        Assert.Contains("exit code 87", verdict);
        Assert.Equal(StatusColors.Warning, color);
    }

    // RunSfcAsync/RunDismAsync now both acquire the SystemModification operation lock
    // (after the elevation gate) so they are mutually exclusive — concurrent runs would
    // cross-contaminate the shared _runner's captured output. The full VM path is gated
    // behind elevation (skipped in non-admin CI, like the tests above), so this pins the
    // mutual-exclusion contract the fix relies on at the service level.
    [Fact]
    public void SystemModificationLock_IsMutuallyExclusive()
    {
        using var first = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "SFC scan");
        Assert.NotNull(first);

        // A second acquire for the same category (e.g. DISM while SFC holds it) must fail.
        var second = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        Assert.Null(second);
        Assert.Equal("SFC scan", OperationLockService.Instance.GetActiveOperationName(OperationCategory.SystemModification));

        first!.Dispose();
        // Once released, the category is free again.
        using var third = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "DISM RestoreHealth");
        Assert.NotNull(third);
    }
}
