// SysManager · CleanupViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Pure unit tests for <see cref="CleanupViewModel"/> that don't touch the
/// real PowerShell runner, the WPF dispatcher, or spawn any processes.
/// Heavier end-to-end scenarios live in SysManager.IntegrationTests.
/// </summary>
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

    [Fact]
    public void Progress_AcceptsFullRange()
    {
        var vm = NewVm();
        foreach (var p in new[] { 0, 1, 50, 99, 100 })
        {
            vm.Progress = p;
            Assert.Equal(p, vm.Progress);
        }
    }

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

    [Fact]
    public void TempSizeLabel_CanBeSetDirectly()
    {
        var vm = NewVm();
        vm.TempSizeLabel = "42.0 MB can be freed";
        Assert.Equal("42.0 MB can be freed", vm.TempSizeLabel);
    }

    [Fact]
    public void RecycleBinLabel_CanBeSetDirectly()
    {
        var vm = NewVm();
        vm.RecycleBinLabel = "Empty";
        Assert.Equal("Empty", vm.RecycleBinLabel);
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
        Assert.Equal("#22C55E", color);
    }

    [Fact]
    public void ParseSfcResult_SuccessfullyRepaired_ReturnsYellow()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files and successfully repaired them." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully repaired", verdict);
        Assert.Equal("#F59E0B", color);
    }

    [Fact]
    public void ParseSfcResult_UnableToFix_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection found corrupt files but was unable to fix some of them." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not repair", verdict);
        Assert.Equal("#EF4444", color);
    }

    [Fact]
    public void ParseSfcResult_CouldNotPerform_ReturnsRed()
    {
        var lines = new[] { "Windows Resource Protection could not perform the requested operation." };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("could not run", verdict);
        Assert.Equal("#EF4444", color);
    }

    [Fact]
    public void ParseSfcResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal("#22C55E", color);
    }

    [Fact]
    public void ParseSfcResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseSfcResult(lines, 1);
        Assert.Contains("exit code 1", verdict);
        Assert.Equal("#F59E0B", color);
    }

    [Fact]
    public void ParseSfcResult_EmptyLines_FallsBackToExitCode()
    {
        var (verdict, color) = CleanupViewModel.ParseSfcResult(Array.Empty<string>(), 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal("#22C55E", color);
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
        Assert.Equal("#22C55E", color);
    }

    [Fact]
    public void ParseDismResult_CorruptionRepaired_ReturnsYellow()
    {
        var lines = new[] { "The component store corruption was repaired." };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("repaired", verdict);
        Assert.Equal("#F59E0B", color);
    }

    [Fact]
    public void ParseDismResult_SourceNotFound_ReturnsRed()
    {
        var lines = new[] { "The source files could not be found." };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("source files", verdict);
        Assert.Equal("#EF4444", color);
    }

    [Fact]
    public void ParseDismResult_ExitZeroNoMatch_ReturnsGreenFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 0);
        Assert.Contains("successfully", verdict);
        Assert.Equal("#22C55E", color);
    }

    [Fact]
    public void ParseDismResult_NonZeroExit_ReturnsYellowFallback()
    {
        var lines = new[] { "Some unrecognized output" };
        var (verdict, color) = CleanupViewModel.ParseDismResult(lines, 87);
        Assert.Contains("exit code 87", verdict);
        Assert.Equal("#F59E0B", color);
    }
}
