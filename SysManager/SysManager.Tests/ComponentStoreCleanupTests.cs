// SysManager · ComponentStoreCleanupTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// The two component-store operations (#1577): what the analysis decides, and the gates standing between
/// the user and the cleanup.
/// </summary>
/// <remarks>
/// WinSxS is the one Deep Cleanup target that must not be touched as files — deleting component-store files
/// directly corrupts servicing — so this goes through DISM, which makes the interesting logic the parsing
/// and the gating rather than any traversal.
/// <para>The single most important assertion in the file is that the analysis is what enables the cleanup.
/// The mainstream cleaners run component cleanup as one opaque click; the value here is that the read-only
/// report comes first and states the cost, and a cleanup button that were clickable without it would erase
/// exactly that difference.</para>
/// </remarks>
[Collection("ProcessWideStatics")]
public sealed class ComponentStoreCleanupTests
{
    /// <summary>What `/AnalyzeComponentStore` prints on a machine where a cleanup is worth doing.</summary>
    private static string[] RecommendedOutput() =>
    [
        "Deployment Image Servicing and Management tool",
        "Version: 10.0.26100.1",
        "Component Store (WinSxS) Information:",
        "Windows Explorer Reported Size of Component Store : 8.19 GB",
        "Actual Size of Component Store : 7.90 GB",
        "    Shared with Windows : 5.99 GB",
        "    Backups and Disabled Features : 1.35 GB",
        "    Cache and Temporary Data : 556.00 MB",
        "Date of Last Cleanup : 2026-08-01 03:12:44",
        "Number of Reclaimable Packages : 12",
        "Component Store Cleanup Recommended : Yes",
        "The operation completed successfully.",
    ];

    private static string[] NotRecommendedOutput() =>
    [
        "Component Store (WinSxS) Information:",
        "Actual Size of Component Store : 6.11 GB",
        "Number of Reclaimable Packages : 0",
        "Component Store Cleanup Recommended : No",
        "The operation completed successfully.",
    ];

    private static CleanupViewModel NewVm(IPowerShellRunner? runner = null)
        => new(runner ?? Substitute.For<IPowerShellRunner>(), Substitute.For<ICleanupPreScanService>());

    // ── What the analysis decides ────────────────────────────────────────────

    [Fact]
    public void Analysis_WhenWindowsRecommendsCleanup_SaysSoAndUnlocksIt()
    {
        var (verdict, color, recommended) =
            CleanupViewModel.ParseComponentStoreResult(RecommendedOutput(), 0, analyzing: true);

        Assert.True(recommended);
        Assert.Equal(StatusColors.Warning, color);
        Assert.Contains("recommends", verdict, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The size is quoted from Windows' own "Actual Size of Component Store", not computed — showing a
    /// saving this app cannot promise would be worse than showing none.
    /// </summary>
    [Fact]
    public void Analysis_QuotesTheSizeWindowsReported()
    {
        var (verdict, _, _) =
            CleanupViewModel.ParseComponentStoreResult(RecommendedOutput(), 0, analyzing: true);

        Assert.Contains("7.90 GB", verdict, StringComparison.Ordinal);
        // Not the Explorer-reported size, which is a different (larger) number on the same output.
        Assert.DoesNotContain("8.19 GB", verdict, StringComparison.Ordinal);
    }

    /// <summary>
    /// "No" is a real and common answer. A store already cleaned must not present a button that would
    /// spend half an hour reclaiming nothing.
    /// </summary>
    [Fact]
    public void Analysis_WhenNoCleanupIsNeeded_SaysSoAndLeavesItLocked()
    {
        var (verdict, color, recommended) =
            CleanupViewModel.ParseComponentStoreResult(NotRecommendedOutput(), 0, analyzing: true);

        Assert.False(recommended);
        Assert.Equal(StatusColors.Good, color);
        Assert.Contains("No cleanup needed", verdict, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("6.11 GB", verdict, StringComparison.Ordinal);
    }

    /// <summary>
    /// Output DISM never printed must not unlock the cleanup. A localised machine prints its own wording,
    /// and defaulting to "recommended" there would offer a destructive operation on no evidence at all.
    /// </summary>
    [Fact]
    public void Analysis_WhenTheOutputSaysNeither_LeavesTheCleanupLocked()
    {
        var (_, _, recommended) = CleanupViewModel.ParseComponentStoreResult(
            ["Komponentenspeicher (WinSxS)-Informationen:", "Der Vorgang wurde erfolgreich beendet."],
            0, analyzing: true);

        Assert.False(recommended);
    }

    [Fact]
    public void Analysis_WithNoSizeInTheOutput_OmitsItRatherThanInventingOne()
    {
        var (verdict, _, recommended) = CleanupViewModel.ParseComponentStoreResult(
            ["Component Store Cleanup Recommended : Yes"], 0, analyzing: true);

        Assert.True(recommended);
        Assert.DoesNotContain("holds", verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analysis_WithANonZeroExitAndNoVerdictLine_ReportsTheExitCode()
    {
        var (verdict, color, recommended) =
            CleanupViewModel.ParseComponentStoreResult(["Error: 87"], 87, analyzing: true);

        Assert.False(recommended);
        Assert.Equal(StatusColors.Warning, color);
        Assert.Contains("87", verdict, StringComparison.Ordinal);
    }

    // ── What the cleanup reports ─────────────────────────────────────────────

    /// <summary>
    /// A completed CLEANUP never leaves the button enabled: the analysis it was based on is now stale, and
    /// offering another run on the strength of it would quote a size that no longer exists.
    /// </summary>
    [Fact]
    public void Cleanup_HoweverItEnds_NeverReportsThatAnotherCleanupIsRecommended()
    {
        foreach (var (lines, exit) in new (string[], int)[]
        {
            (["The operation completed successfully."], 0),
            (["Error: 0x800f081f"], 2),
            ([], 0),
        })
        {
            var (_, _, recommended) = CleanupViewModel.ParseComponentStoreResult(lines, exit, analyzing: false);
            Assert.False(recommended);
        }
    }

    [Fact]
    public void Cleanup_WhenItSucceeds_SaysTheSpaceIsFree()
    {
        var (verdict, color, _) = CleanupViewModel.ParseComponentStoreResult(
            ["The operation completed successfully."], 0, analyzing: false);

        Assert.Equal(StatusColors.Good, color);
        Assert.Contains("free", verdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cleanup_WithANonZeroExit_ReportsTheExitCode()
    {
        var (verdict, color, _) = CleanupViewModel.ParseComponentStoreResult(
            ["Error: 0x800f081f"], 2, analyzing: false);

        Assert.Equal(StatusColors.Warning, color);
        Assert.Contains("2", verdict, StringComparison.Ordinal);
    }

    // ── The gates between the user and the cleanup ───────────────────────────

    [Fact]
    public void TheCleanupCommand_IsNotClickableBeforeAnAnalysisRecommendsIt()
    {
        var vm = NewVm();

        Assert.False(vm.CleanComponentStoreCommand.CanExecute(null));
        Assert.True(vm.AnalyzeComponentStoreCommand.CanExecute(null));
    }

    [Fact]
    public void TheCleanupCommand_BecomesClickableOnceAnAnalysisRecommendsIt()
    {
        var vm = NewVm();
        vm.CanCleanStore = true;

        Assert.True(vm.CleanComponentStoreCommand.CanExecute(null));
    }

    [Fact]
    public void NeitherOperation_IsClickableWhileOneIsRunning()
    {
        var vm = NewVm();
        vm.CanCleanStore = true;
        vm.IsStoreRunning = true;

        Assert.False(vm.AnalyzeComponentStoreCommand.CanExecute(null));
        Assert.False(vm.CleanComponentStoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task Analyze_WhenNotElevated_DoesNotReachTheRunner()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.False(vm.IsElevated, "the scope must reach the view-model's constructor");

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);   // decline the restart
        DialogService.Instance = dialog;
        try
        {
            await vm.AnalyzeComponentStoreCommand.ExecuteAsync(null);

            Assert.Contains("admin", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            await runner.DidNotReceive().RunProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            Assert.False(vm.IsStoreRunning);
        }
        finally { DialogService.Instance = prevDialog; }
    }

    /// <summary>
    /// The mirror image: elevated, the analysis must actually reach DISM — and with the READ-ONLY argument.
    /// </summary>
    [Fact]
    public async Task Analyze_WhenElevated_RunsTheReadOnlyAnalysis()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        Assert.True(vm.IsElevated);

        await vm.AnalyzeComponentStoreCommand.ExecuteAsync(null);

        await runner.Received(1).RunProcessAsync(
            "DISM.exe", "/Online /Cleanup-Image /AnalyzeComponentStore",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    /// <summary>
    /// Declining the confirmation must not run the cleanup. This is the last gate before something
    /// irreversible, so it is asserted on the runner and not only on the status message.
    /// </summary>
    [Fact]
    public async Task Clean_WhenTheUserDeclinesTheConfirmation_RunsNothing()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        vm.CanCleanStore = true;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            await vm.CleanComponentStoreCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            await runner.DidNotReceive().RunProcessAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally { DialogService.Instance = prevDialog; }
    }

    /// <summary>
    /// Confirming runs `/StartComponentCleanup` — and the assertion is on the exact argument string,
    /// because the one variant that must never appear (`/ResetBase`) differs from it by a suffix.
    /// </summary>
    [Fact]
    public async Task Clean_WhenTheUserConfirms_RunsStartComponentCleanupAndNothingElse()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var runner = Substitute.For<IPowerShellRunner>();
        var vm = NewVm(runner);
        vm.CanCleanStore = true;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            await vm.CleanComponentStoreCommand.ExecuteAsync(null);

            await runner.Received(1).RunProcessAsync(
                "DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup",
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally { DialogService.Instance = prevDialog; }
    }

    /// <summary>
    /// The confirmation has to say what is given up, not merely ask. "Are you sure?" on an operation whose
    /// cost is "you can no longer uninstall your updates" is not consent.
    /// </summary>
    [Fact]
    public async Task Clean_TheConfirmation_StatesWhatTheUserGivesUp()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var vm = NewVm();
        vm.CanCleanStore = true;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        string? shown = null;
        dialog.Confirm(Arg.Do<string>(m => shown = m), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            await vm.CleanComponentStoreCommand.ExecuteAsync(null);

            Assert.NotNull(shown);
            Assert.Contains("uninstall", shown!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("10 to 30 minutes", shown!, StringComparison.OrdinalIgnoreCase);
        }
        finally { DialogService.Instance = prevDialog; }
    }

    [Fact]
    public void AStoreOperation_CountsAsAnyRunning_SoCancelIsEnabled()
    {
        var vm = NewVm();
        Assert.False(vm.IsAnyRunning);

        vm.IsStoreRunning = true;

        Assert.True(vm.IsAnyRunning);
        Assert.True(vm.CancelCommand.CanExecute(null));
    }
}
