// SysManager · PerformanceSnapshotRestartTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Regression coverage for the persisted Performance Mode recovery point.
/// Every test uses an isolated directory and never touches the real app profile.
/// </summary>
[Collection("ProcessWideStatics")]
public sealed class PerformanceSnapshotRestartTests
{
    private static IPowerShellRunner NewRunner()
    {
        var runner = Substitute.For<IPowerShellRunner>();
        runner.RunProcessAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>())
            .Returns(0);
        return runner;
    }

    /// <summary>A service over <paramref name="configDir"/>, whose process calls go to a substituted runner.</summary>
    /// <param name="configDir">The folder its record is kept in.</param>
    /// <param name="runner">The runner; one that answers every call with success unless a test passes its own.</param>
    /// <param name="card">
    /// What has become of the graphics card a record names: there unless a test says otherwise, so that what Restore
    /// All asks does not depend on the graphics card of the PC running the suite.
    /// </param>
    private static PerformanceService NewService(
        string configDir,
        IPowerShellRunner? runner = null,
        PerformanceService.RecordedAdapter card = PerformanceService.RecordedAdapter.Present)
    {
        runner ??= NewRunner();
        return new PerformanceService(runner, new RestorePointService(runner), configDir, findAdapter: _ => card);
    }

    /// <summary>
    /// No game profile is running — the normal case for these tests. Performance Mode consults this
    /// only to decide whether the settings on the machine right now are the user's own before it
    /// records them as the recovery baseline.
    /// </summary>
    private static IGamingProfileService NoGamingSession()
    {
        var gaming = Substitute.For<IGamingProfileService>();
        gaming.IsActive.Returns(false);
        return gaming;
    }

    private static PerformanceService.OriginalSnapshot ValidSnapshot(
        DateTimeOffset? capturedAtUtc = null) =>
        new(
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerPlanName: "Balanced",
            UiEffectsEnabled: true,
            GameModeEnabled: true,
            XboxGameBarEnabled: true,
            XboxGameDvrEnabled: true,
            GpuDynamicPstate: true,
            ProcessorMinPercentAc: 5,
            NvidiaSubKey: null,
            CapturedAtUtc: capturedAtUtc);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SysManagerPerformanceTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task Initialization_WithPersistedSnapshot_EnablesRestoreAndShowsCaptureTime()
    {
        var dir = CreateTempDirectory();
        var snapshot = ValidSnapshot(
            new DateTimeOffset(2026, 7, 31, 12, 34, 0, TimeSpan.Zero)) with
        {
            XboxGameDvrEnabled = false,
            GpuDynamicPstate = false,
            NvidiaSubKey = "0000"
        };

        try
        {
            using (var writer = NewService(dir))
                Assert.True(writer.SaveSnapshot(snapshot));

            using var service = NewService(dir);
            Assert.Equal(snapshot, service.LoadSnapshot());

            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            Assert.True(vm.HasSnapshot);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                dialog.Received(1).Confirm(
                    Arg.Is<string>(message =>
                        message != null
                        && message.Contains("Snapshot captured:", StringComparison.Ordinal)
                        && message.Contains("2026", StringComparison.Ordinal)
                        && message.Contains("Game DVR → OFF", StringComparison.Ordinal)
                        && message.Contains("GPU → Max performance", StringComparison.Ordinal)),
                    "Restore Original Settings — Confirm");
                Assert.DoesNotContain("Nothing to restore", vm.StatusMessage, StringComparison.Ordinal);
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAll_ForAGraphicsCardThatIsGone_SaysItIsLeftAsItIs()
    {
        // The restore leaves out a card that is no longer in the PC (#1525), so the question cannot promise its setting,
        // nor a restart for it.
        var dir = CreateTempDirectory();
        try
        {
            var snapshot = ValidSnapshot() with { GpuDynamicPstate = false, NvidiaSubKey = "0000" };
            using var service = NewService(dir, card: PerformanceService.RecordedAdapter.Gone);
            Assert.True(service.SaveSnapshot(snapshot));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                dialog.Received(1).Confirm(
                    Arg.Is<string>(message =>
                        message != null
                        && message.Contains("• GPU → left as it is: the NVIDIA card this was recorded on is no longer in "
                                            + "this PC", StringComparison.Ordinal)
                        && !message.Contains("reboot needed", StringComparison.Ordinal)),
                    "Restore Original Settings — Confirm");
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_PersistedSnapshotIsAvailableBeforeBlockedRefreshCompletes()
    {
        var dir = CreateTempDirectory();
        var refreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var snapshot = ValidSnapshot() with
            {
                NvidiaSubKey = "0000"
            };
            using (var writer = NewService(dir))
                Assert.True(writer.SaveSnapshot(snapshot));

            var runner = Substitute.For<IPowerShellRunner>();
            runner.RunProcessAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(_ =>
                {
                    refreshStarted.TrySetResult(true);
                    return releaseRefresh.Task;
                });

            using var service = NewService(dir, runner);
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            try
            {
                await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Restore hydration precedes the blocked live probe, so recovery is immediately visible.
                Assert.True(vm.HasSnapshot);

                var previousDialog = DialogService.Instance;
                var dialog = Substitute.For<IDialogService>();
                dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
                DialogService.Instance = dialog;
                try
                {
                    // A missing live profile cannot suppress the conservative GPU reboot warning.
                    await vm.RestoreAllCommand.ExecuteAsync(null);

                    dialog.Received(1).Confirm(
                        Arg.Is<string>(message =>
                            message != null
                            && message.Contains(
                                "GPU → Dynamic P-state (reboot needed)",
                                StringComparison.Ordinal)),
                        "Restore Original Settings — Confirm");
                }
                finally
                {
                    DialogService.Instance = previousDialog;
                }
            }
            finally
            {
                releaseRefresh.TrySetResult(0);
                await vm.InitializationComplete.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            releaseRefresh.TrySetResult(0);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Initialization_WithLegacySnapshot_EnablesRestoreWithUnknownCaptureTime()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            Assert.True(vm.HasSnapshot);
            Assert.Null(service.LoadSnapshot()!.CapturedAtUtc);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                dialog.Received(1).Confirm(
                    Arg.Is<string>(message =>
                        message != null
                        && message.Contains(
                            "Unknown (snapshot created by an earlier SysManager version)",
                            StringComparison.Ordinal)),
                    "Restore Original Settings — Confirm");
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreFailure_PreservesPersistedSnapshotForRetry()
    {
        var dir = CreateTempDirectory();
        var runner = NewRunner();

        try
        {
            using var service = NewService(dir, runner);
            Assert.True(service.SaveSnapshot(ValidSnapshot()));

            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;
            Assert.True(vm.HasSnapshot);

            runner.RunProcessAsync(
                    "powercfg.exe",
                    Arg.Is<string>(arguments =>
                        arguments != null
                        && arguments.StartsWith("/setactive ", StringComparison.Ordinal)),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(5);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);

                Assert.True(vm.HasSnapshot);
                Assert.NotNull(service.LoadSnapshot());
                Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_MissingRequiredProperty_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_WrongPropertyType_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "UiEffectsEnabled": "yes",
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_DuplicateProperty_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                """
                {
                  "PowerPlanGuid": "381b4222-f694-41f0-9685-ff5bb260df2e",
                  "PowerPlanName": "Balanced",
                  "PowerPlanName": "Spoofed",
                  "UiEffectsEnabled": true,
                  "GameModeEnabled": true,
                  "XboxGameBarEnabled": true,
                  "XboxGameDvrEnabled": true,
                  "GpuDynamicPstate": true,
                  "ProcessorMinPercentAc": 5,
                  "NvidiaSubKey": null
                }
                """);

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSnapshot_OversizedFile_ReturnsNull()
    {
        var dir = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "performance-snapshot.json"),
                new string('x', PerformanceService.MaxSnapshotBytes + 1));

            using var service = NewService(dir);
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveSnapshot_InvalidSnapshot_ReturnsFalseAndWritesNothing()
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var invalid = ValidSnapshot() with { ProcessorMinPercentAc = 101 };

            Assert.False(service.SaveSnapshot(invalid));
            Assert.False(File.Exists(Path.Combine(dir, "performance-snapshot.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── A snapshot that is there but cannot be used (#2521) ─────────────────────────────────────
    //
    // LoadSnapshot returned null for no file and for a file it could not read or use alike, so the first Apply
    // captured the settings on the machine and saved them over it. After an earlier Apply those settings are the
    // tweaks, and Restore All would then put the tweaks back and report "Original settings restored." The file is
    // held open with delete sharing only while a read must fail: the read fails, and a write would still succeed.

    private static async Task AnswerYesAsync(Func<Task> body)
    {
        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try { await body(); }
        finally { DialogService.Instance = previousDialog; }
    }

    [Fact]
    public void LoadSnapshot_TellsNoFileFromAnUnreadableOneFromAnInvalidOne()
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var file = Path.Combine(dir, "performance-snapshot.json");

            Assert.Null(service.LoadSnapshot(out var none));
            Assert.Equal(PerformanceService.SnapshotProblem.None, none);

            Assert.True(service.SaveSnapshot(ValidSnapshot()));
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                Assert.Null(service.LoadSnapshot(out var unreadable));
                Assert.Equal(PerformanceService.SnapshotProblem.Unreadable, unreadable);
            }

            File.WriteAllText(file, "{ not a snapshot");
            Assert.Null(service.LoadSnapshot(out var invalid));
            Assert.Equal(PerformanceService.SnapshotProblem.Invalid, invalid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_WhenTheSnapshotCannotBeRead_ChangesNothing_AndKeepsIt()
    {
        var dir = CreateTempDirectory();
        var runner = NewRunner();
        try
        {
            using var service = NewService(dir, runner);
            var original = ValidSnapshot();
            Assert.True(service.SaveSnapshot(original));

            using (new FileStream(Path.Combine(dir, "performance-snapshot.json"), FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                using var vm = new PerformanceViewModel(service, NoGamingSession());
                await vm.InitializationComplete;
                Assert.False(vm.HasSnapshot, "precondition: the first read failed as well");

                vm.SelectedPlan = "high";
                await AnswerYesAsync(() => vm.ApplyPowerPlanCommand.ExecuteAsync(null));

                Assert.Contains("could not read its record of your original settings", vm.StatusMessage, StringComparison.Ordinal);
            }

            Assert.Equal(original, service.LoadSnapshot());
            await runner.DidNotReceive().RunProcessAsync(
                "powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_WhenTheSnapshotIsDamaged_KeepsItAside_ChangesNothing_ThenRecordsANewOne()
    {
        var dir = CreateTempDirectory();
        var runner = NewRunner();
        try
        {
            using var service = NewService(dir, runner);
            var file = Path.Combine(dir, "performance-snapshot.json");
            File.WriteAllText(file, "{ not a snapshot");

            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;
            vm.SelectedPlan = "high";

            await AnswerYesAsync(() => vm.ApplyPowerPlanCommand.ExecuteAsync(null));

            Assert.Contains("set the damaged record aside", vm.StatusMessage, StringComparison.Ordinal);
            Assert.Equal("{ not a snapshot", File.ReadAllText(file + ".unreadable"));
            Assert.False(File.Exists(file));
            await runner.DidNotReceive().RunProcessAsync(
                "powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());

            // Told first, the user can go ahead: the next Apply records the current settings as the new original.
            await AnswerYesAsync(() => vm.ApplyPowerPlanCommand.ExecuteAsync(null));

            Assert.NotNull(service.LoadSnapshot());
            Assert.True(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_WhenADamagedSnapshotCannotBeSetAside_ChangesNothing_AndLeavesIt()
    {
        var dir = CreateTempDirectory();
        var runner = NewRunner();
        try
        {
            using var service = NewService(dir, runner);
            var file = Path.Combine(dir, "performance-snapshot.json");
            File.WriteAllText(file, "{ not a snapshot");

            // Held with read sharing only: the snapshot reads as damaged, and it cannot be moved.
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var vm = new PerformanceViewModel(service, NoGamingSession());
                await vm.InitializationComplete;
                vm.SelectedPlan = "high";

                await AnswerYesAsync(() => vm.ApplyPowerPlanCommand.ExecuteAsync(null));

                Assert.Contains("could not be set aside", vm.StatusMessage, StringComparison.Ordinal);
                Assert.False(vm.HasSnapshot);
            }

            Assert.Equal("{ not a snapshot", File.ReadAllText(file));
            Assert.False(File.Exists(file + ".unreadable"));
            await runner.DidNotReceive().RunProcessAsync(
                "powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A live Gaming Profile session must stop this tab from inventing a recovery baseline out of the
    /// profile's settings.
    /// </summary>
    /// <remarks>
    /// #1501 stopped the two tabs from snapshotting each other mid-change by taking the
    /// system-modification lock before CaptureSnapshotAsync. That lock is held per OPERATION, but a
    /// gaming SESSION outlives it: the profile applies, releases the lock, and its power plan and
    /// visual-effects values stay live until the game exits. The first mutating command in this tab
    /// during that window used to capture those borrowed values and PERSIST them as the user's
    /// original, so a later Restore All would put the machine on a gaming plan it had never been on.
    /// <para>The refusal is checked, not just the absence of a file: a test that only asserted
    /// "no snapshot written" would also pass if the command failed for some unrelated reason.</para>
    /// </remarks>
    [Fact]
    public async Task Apply_WhileAGameProfileIsRunning_RefusesInsteadOfRecordingTheProfilesSettings()
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var gaming = Substitute.For<IGamingProfileService>();
            gaming.IsActive.Returns(true);

            using var vm = new PerformanceViewModel(service, gaming);
            await vm.InitializationComplete;
            Assert.False(vm.HasSnapshot, "precondition: nothing is persisted, so a capture would happen");

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
            DialogService.Instance = dialog;
            try
            {
                // Must DIFFER from the current plan, or the command returns at its "already set to
                // the selected option" short-circuit and never reaches the snapshot step — which is
                // what the first version of this test did, passing for the wrong reason.
                vm.SelectedPlan = "high";
                await vm.ApplyPowerPlanCommand.ExecuteAsync(null);
            }
            finally { DialogService.Instance = previousDialog; }

            Assert.Contains("Stop the game profile first", vm.StatusMessage, StringComparison.Ordinal);
            Assert.Null(service.LoadSnapshot());
            Assert.False(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── After Undo Changes put the original back (#1525) ─────────────────────

    [Fact]
    public async Task AnApply_AfterTheRecordWasDeletedElsewhere_RecordsAFreshOriginal()
    {
        // Undo Changes puts the original back and deletes the record while this tab is out of sight. The tab kept its
        // copy in memory and skipped recording, so the next change was made with no original on disk: after a
        // restart there was nothing to restore it from.
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            Assert.True(service.SaveSnapshot(ValidSnapshot(Recorded)));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;
            Assert.True(vm.HasSnapshot, "precondition: the tab read the record at start");

            Assert.True(service.DeleteSnapshot());
            using var dialog = new DialogAnswer(confirm: true);
            vm.SelectedPlan = "high";
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

            Assert.NotNull(service.LoadSnapshot());
            Assert.True(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAll_AfterTheRecordWasDeletedElsewhere_SaysThereIsNothingToRestore()
    {
        // Declined either way, so the red run of this test cannot restore anything on the machine running it.
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            Assert.True(service.SaveSnapshot(ValidSnapshot(Recorded)));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            Assert.True(service.DeleteSnapshot());
            using var dialog = new DialogAnswer(confirm: false);
            await vm.RestoreAllCommand.ExecuteAsync(null);

            Assert.Equal(0, dialog.Calls);
            Assert.Equal("Nothing to restore — no changes have been applied yet.", vm.StatusMessage);
            Assert.False(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAll_WithARecordThatCannotBeReadJustNow_KeepsItsCopy_AndSaysToTryAgain()
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            Assert.True(service.SaveSnapshot(ValidSnapshot(Recorded)));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            using var dialog = new DialogAnswer(confirm: false);
            using (File.Open(Path.Combine(dir, "performance-snapshot.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                await vm.RestoreAllCommand.ExecuteAsync(null);

            Assert.Equal(0, dialog.Calls);
            Assert.Equal("SysManager could not read its record of your original settings just now. Try again in a moment.",
                vm.StatusMessage);
            Assert.True(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static readonly DateTimeOffset Recorded = new(2026, 10, 5, 19, 40, 0, TimeSpan.Zero);
}
