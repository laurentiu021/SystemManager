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
                        && message.Contains("• GPU → unchanged (SysManager no longer finds the NVIDIA card this was "
                                            + "recorded on)", StringComparison.Ordinal)
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreAll_DoesWhatItsQuestionSaidAboutTheGraphicsCard_WhenTheCardChangesMeanwhile(
        bool thereWhenAsked)
    {
        // The card is looked for when the question is asked, and is found the other way by the time the restore
        // could look again. Restore All hands the restore the card its question found (#2618), so the setting is
        // written to the card the question promised and not to one it said was gone. The whole restore runs: the
        // service's writers are the constructor's defaults, which change nothing on the PC running the suite, and the
        // graphics write is a stand-in that records what it was asked.
        var dir = CreateTempDirectory();
        var (asked, meanwhile) = thereWhenAsked
            ? (PerformanceService.RecordedAdapter.Present, PerformanceService.RecordedAdapter.Gone)
            : (PerformanceService.RecordedAdapter.Gone, PerformanceService.RecordedAdapter.Present);
        var card = asked;
        var gpuWrites = new List<(string SubKey, bool MaxPerformance)>();
        try
        {
            var runner = NewRunner();
            using var service = new PerformanceService(
                runner, new RestorePointService(runner), dir,
                findAdapter: _ => card,
                restoreGpu: (subKey, maxPerformance) =>
                {
                    gpuWrites.Add((subKey, maxPerformance));
                    return true;
                });
            Assert.True(service.SaveSnapshot(ValidSnapshot() with { GpuDynamicPstate = true, NvidiaSubKey = "0001" }));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
            {
                card = meanwhile;
                return true;
            });
            DialogService.Instance = dialog;
            try
            {
                await vm.RestoreAllCommand.ExecuteAsync(null);
            }
            finally
            {
                DialogService.Instance = previousDialog;
            }

            // Dynamic P-state was on when it was recorded, so putting it back writes "not max performance".
            (string, bool)[] expected = thereWhenAsked ? [("0001", false)] : [];
            Assert.Equal(expected, gpuWrites);
            Assert.Null(service.LoadSnapshot());   // the restore ran to its end and cleared the record
            Assert.Equal(
                thereWhenAsked
                    ? "Original settings restored. Reboot required for GPU changes."
                    : "Original settings restored.",
                vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- a power plan that is gone (#2716) ----------
    // The restore switched the recorded plan on first, and powercfg failed for one that was deleted, or whose plans an OEM
    // tool or powercfg -restoredefaultschemes had reset: the restore stopped there, before every other setting, and
    // every retry failed the same way. The plans are a seam, so none of the PC running the suite is read.

    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>The service over <paramref name="dir"/>, with the plans on the PC and the visual-effects write given.</summary>
    private static PerformanceService ServiceWithPlans(
        string dir, IPowerShellRunner runner, Func<IReadOnlySet<Guid>?> plans, List<bool> uiEffects) =>
        new(runner, new RestorePointService(runner), dir, setUiEffects: uiEffects.Add, plansOnThePc: plans);

    private static async Task<string?> RestoreAllAnsweringAsync(PerformanceViewModel vm, bool answer, Action? meanwhile = null)
    {
        string? asked = null;
        var previousDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            asked = call.ArgAt<string>(0);
            meanwhile?.Invoke();
            return answer;
        });
        DialogService.Instance = dialog;
        try
        {
            await vm.RestoreAllCommand.ExecuteAsync(null);
        }
        finally
        {
            DialogService.Instance = previousDialog;
        }
        return asked;
    }

    [Fact]
    public async Task RestoreAll_ForAPowerPlanThatIsGone_LeavesItOut_AndPutsTheRestBack()
    {
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            // What powercfg answers for a plan that is not there.
            runner.RunProcessAsync("powercfg.exe", $"/setactive {Balanced}", Arg.Any<CancellationToken>(),
                    Arg.Any<System.Text.Encoding?>())
                .Returns(1);
            var uiEffects = new List<bool>();
            using var service = ServiceWithPlans(dir, runner, () => new HashSet<Guid> { HighPerformance }, uiEffects);
            Assert.True(service.SaveSnapshot(ValidSnapshot()));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;
            runner.ClearReceivedCalls();

            var asked = await RestoreAllAnsweringAsync(vm, answer: true);

            Assert.Contains("• Power plan → unchanged (Balanced is no longer on this PC)", asked, StringComparison.Ordinal);
            await runner.DidNotReceive().RunProcessAsync("powercfg.exe", $"/setactive {Balanced}",
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            Assert.Equal([true], uiEffects);         // the next setting went back
            Assert.Null(service.LoadSnapshot());     // and the restore ran to its end
            Assert.Equal("Original settings restored.", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // the plans could not be listed: switched on, as before
    public async Task RestoreAll_ForAPowerPlanThatIsThere_OrNotKnown_SwitchesItOn(bool listed)
    {
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            using var service = ServiceWithPlans(dir, runner,
                () => listed ? new HashSet<Guid> { Balanced, HighPerformance } : null, []);
            Assert.True(service.SaveSnapshot(ValidSnapshot()));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            var asked = await RestoreAllAnsweringAsync(vm, answer: true);

            Assert.Contains($"• Power plan → Balanced ({Balanced})", asked, StringComparison.Ordinal);
            await runner.Received(1).RunProcessAsync("powercfg.exe", $"/setactive {Balanced}",
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreAll_LeavesThePlanAlone_WhenItWasGoneWhenAsked_OrIsGoneByTheTimeItIsRestored(bool thereWhenAsked)
    {
        // Gone when the question was asked and back since: the question said it stays as it is, so it does. There when
        // asked and deleted since: there is nothing to switch on, and the rest still goes back.
        var dir = CreateTempDirectory();
        var plans = thereWhenAsked ? new HashSet<Guid> { Balanced } : [HighPerformance];
        try
        {
            var runner = NewRunner();
            using var service = ServiceWithPlans(dir, runner, () => plans, []);
            Assert.True(service.SaveSnapshot(ValidSnapshot()));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            await RestoreAllAnsweringAsync(vm, answer: true,
                meanwhile: () => plans = thereWhenAsked ? [HighPerformance] : [Balanced, HighPerformance]);

            await runner.DidNotReceive().RunProcessAsync("powercfg.exe", $"/setactive {Balanced}",
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
            Assert.Null(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- the NVIDIA setting, written only when it differs (#2716) ----------
    // The restore wrote the card's setting back on every Restore All, set as recorded or not, and only an administrator
    // can write it: a standard user's restore put four settings back and stopped at the card, before the processor
    // minimum, every time, and an administrator's always asked for a restart.

    /// <summary>A service over <paramref name="dir"/> whose recorded card is there, set as <paramref name="maxPerformanceNow"/>.</summary>
    private static PerformanceService ServiceWithCard(
        string dir, IPowerShellRunner runner, bool? maxPerformanceNow, bool elevated, List<bool> uiEffects,
        List<(string SubKey, bool MaxPerformance)> gpuWrites, bool gpuWriteWorks = true) =>
        new(runner, new RestorePointService(runner), dir,
            findAdapter: _ => PerformanceService.RecordedAdapter.Present,
            setUiEffects: uiEffects.Add,
            restoreGpu: (subKey, maxPerformance) =>
            {
                gpuWrites.Add((subKey, maxPerformance));
                return gpuWriteWorks;
            },
            readGpu: _ => maxPerformanceNow,
            isElevated: () => elevated);

    // Adaptive when it was recorded.
    private static PerformanceService.OriginalSnapshot WithAdaptiveCard() =>
        ValidSnapshot() with { GpuDynamicPstate = true, NvidiaSubKey = "0001" };

    [Fact]
    public async Task RestoreAll_ThatHasToWriteTheCard_WithoutAdministratorRights_AsksNothing_AndChangesNothing()
    {
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            List<bool> uiEffects = [];
            List<(string, bool)> gpuWrites = [];
            using var service = ServiceWithCard(dir, runner, maxPerformanceNow: true, elevated: false, uiEffects, gpuWrites);
            Assert.True(service.SaveSnapshot(WithAdaptiveCard()));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;
            runner.ClearReceivedCalls();

            var asked = await RestoreAllAnsweringAsync(vm, answer: true);

            Assert.Null(asked);
            Assert.Equal(PerformanceService.GraphicsNeedsAdministrator, vm.StatusMessage);
            Assert.Empty(uiEffects);
            Assert.Empty(gpuWrites);
            Assert.Empty(runner.ReceivedCalls());
            Assert.NotNull(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAll_ForACardSetAsRecorded_NeedsNoAdministrator_NorARestart()
    {
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            List<bool> uiEffects = [];
            List<(string, bool)> gpuWrites = [];
            using var service = ServiceWithCard(dir, runner, maxPerformanceNow: false, elevated: false, uiEffects, gpuWrites);
            Assert.True(service.SaveSnapshot(WithAdaptiveCard()));
            using var vm = new PerformanceViewModel(service, NoGamingSession());
            await vm.InitializationComplete;

            var asked = await RestoreAllAnsweringAsync(vm, answer: true);

            Assert.Contains("• GPU → Dynamic P-state (set already)", asked, StringComparison.Ordinal);
            Assert.DoesNotContain("reboot needed", asked, StringComparison.Ordinal);
            Assert.Empty(gpuWrites);
            Assert.Equal([true], uiEffects);
            Assert.Null(service.LoadSnapshot());
            Assert.Equal("Original settings restored.", vm.StatusMessage);
            Assert.False(vm.NeedsReboot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheRestore_RefusesBeforeChangingAnything_WhenTheCardNeedsAWriteWithoutAdministratorRights()
    {
        // Undo Changes reaches the same restore, so it refuses too, whatever asked.
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            List<bool> uiEffects = [];
            List<(string, bool)> gpuWrites = [];
            using var service = ServiceWithCard(dir, runner, maxPerformanceNow: null, elevated: false, uiEffects, gpuWrites);
            Assert.True(service.SaveSnapshot(WithAdaptiveCard()));

            var thrown = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.RestoreOriginalAsync(WithAdaptiveCard(), PerformanceService.RecordedAdapter.Present));

            Assert.Equal(PerformanceService.GraphicsNeedsAdministrator, thrown.Message);
            Assert.Empty(runner.ReceivedCalls());
            Assert.Empty(uiEffects);
            Assert.Empty(gpuWrites);
            Assert.NotNull(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheRestore_WritesTheCardLast_SoAWriteThatFailsLeavesTheProcessorMinimumBack()
    {
        var dir = CreateTempDirectory();
        try
        {
            var runner = NewRunner();
            List<(string, bool)> gpuWrites = [];
            using var service = ServiceWithCard(dir, runner, maxPerformanceNow: true, elevated: true, [], gpuWrites,
                gpuWriteWorks: false);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RestoreOriginalAsync(WithAdaptiveCard(), PerformanceService.RecordedAdapter.Present));

            await runner.Received(1).RunProcessAsync("powercfg.exe",
                "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5", Arg.Any<CancellationToken>(),
                Arg.Any<System.Text.Encoding?>());
            Assert.Equal([("0001", false)], gpuWrites);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAll_ForAPlanTheRecordCouldNotRead_SaysItIsUnchanged()
    {
        // The restore leaves such a plan alone, so the question cannot promise one named "Unknown".
        var dir = CreateTempDirectory();
        try
        {
            var snapshot = ValidSnapshot() with { PowerPlanGuid = "", PowerPlanName = "Unknown" };
            using var service = NewService(dir);
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
                        && message.Contains("• Power plan → unchanged (it could not be read when this was recorded)",
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
                // The Game Mode switch, which has no check of its own, so what refuses is the capture: the power
                // plan, visual effects and processor switches refuse before their question (#2711). It must DIFFER
                // from the current state, or the command returns at its "already in the selected state"
                // short-circuit and never reaches the snapshot step — which is what the first version of this
                // test did, passing for the wrong reason.
                vm.WantGameModeOff = vm.Profile.GameModeEnabled;
                await vm.ApplyGameModeCommand.ExecuteAsync(null);
            }
            finally { DialogService.Instance = previousDialog; }

            Assert.Contains("Stop the game profile first", vm.StatusMessage, StringComparison.Ordinal);
            Assert.Contains("recording them as your original settings", vm.StatusMessage, StringComparison.Ordinal);
            Assert.Null(service.LoadSnapshot());
            Assert.False(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Game mode left on, and changes made under game mode (#2711) ─────────
    //
    // The capture checked only IsActive, which lives in memory: after a crash it is false while the record of the
    // session left on is still on disk, so the leftover gaming plan could be recorded as the user's own. Restore All and
    // the Apply buttons checked nothing, so a change made under a session was undone when it ended. Every write here goes
    // nowhere and every process call to a substitute, so a refusal that regressed changes nothing on the PC running the
    // suite.

    private static IGamingProfileService GameModeLeftOn(PendingRecoveryKind kind)
    {
        var gaming = Substitute.For<IGamingProfileService>();
        gaming.IsActive.Returns(false);
        gaming.ReadPendingRecovery().Returns(new PendingRecovery(kind));
        return gaming;
    }

    [Theory]
    [InlineData(PendingRecoveryKind.LeftOn, "Turn game mode off first, in Undo Changes")]
    [InlineData(PendingRecoveryKind.Unreadable, "could not read just now whether game mode was left on")]
    public async Task AnApply_UnderGameModeLeftOnByAnEarlierRun_RefusesToRecordAnOriginal(
        PendingRecoveryKind kind, string said)
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            using var vm = new PerformanceViewModel(service, GameModeLeftOn(kind));
            await vm.InitializationComplete;
            Assert.False(vm.HasSnapshot, "precondition: nothing is persisted, so a capture would happen");

            // The Game Mode switch has no check of its own: the capture's is all that stands in the way.
            using var dialog = new DialogAnswer(confirm: true);
            vm.WantGameModeOff = vm.Profile.GameModeEnabled;
            await vm.ApplyGameModeCommand.ExecuteAsync(null);

            Assert.Contains(said, vm.StatusMessage, StringComparison.Ordinal);
            Assert.Null(service.LoadSnapshot());
            Assert.False(vm.HasSnapshot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The changes game mode holds the settings of.
    private static readonly string[] HeldChangeNames = ["power plan", "visual effects", "processor state", "restore all"];

    public static TheoryData<string> HeldChanges()
    {
        var data = new TheoryData<string>();
        foreach (var change in HeldChangeNames) data.Add(change);
        return data;
    }

    public static TheoryData<string, PendingRecoveryKind> HeldChangesUnderARecord()
    {
        var data = new TheoryData<string, PendingRecoveryKind>();
        foreach (var change in HeldChangeNames)
        {
            data.Add(change, PendingRecoveryKind.LeftOn);
            data.Add(change, PendingRecoveryKind.Unreadable);
        }
        return data;
    }

    // Each change set to differ from what was read, so that nothing but game mode stops it.
    private static Task MakeHeldChange(PerformanceViewModel vm, string change)
    {
        switch (change)
        {
            case "power plan":
                vm.SelectedPlan = vm.SelectedPlan == "high" ? "balanced" : "high";
                return vm.ApplyPowerPlanCommand.ExecuteAsync(null);
            case "visual effects":
                vm.WantVisualEffectsReduced = !vm.Profile.VisualEffectsReduced;
                return vm.ApplyVisualEffectsCommand.ExecuteAsync(null);
            case "processor state":
                vm.WantProcessorMaxState = !vm.Profile.ProcessorMaxState;
                return vm.ApplyProcessorStateCommand.ExecuteAsync(null);
            default:
                return vm.RestoreAllCommand.ExecuteAsync(null);
        }
    }

    // A record on disk, so no capture is needed and Restore All has something to put back.
    private static async Task<PerformanceViewModel> ViewModelOverARecord(
        PerformanceService service, IGamingProfileService gaming)
    {
        Assert.True(service.SaveSnapshot(ValidSnapshot(Recorded)));
        var vm = new PerformanceViewModel(service, gaming);
        await vm.InitializationComplete;
        Assert.False(vm.IsProcessorStateLocked, "precondition: the processor switch can be applied");
        return vm;
    }

    [Theory]
    [MemberData(nameof(HeldChanges))]
    public async Task AChange_WhileGameModeIsOn_IsRefused_BeforeTheQuestion(string change)
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var gaming = Substitute.For<IGamingProfileService>();
            gaming.IsActive.Returns(true);
            using var vm = await ViewModelOverARecord(service, gaming);
            using var dialog = new DialogAnswer(confirm: true);

            await MakeHeldChange(vm, change);

            Assert.Equal(0, dialog.Calls);
            Assert.Equal(PerformanceViewModel.ChangeDuringGameMode, vm.StatusMessage);
            Assert.NotNull(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(HeldChangesUnderARecord))]
    public async Task AChange_WhileGameModeIsLeftOnOrNotKnown_IsRefused_BeforeTheQuestion(
        string change, PendingRecoveryKind kind)
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            using var vm = await ViewModelOverARecord(service, GameModeLeftOn(kind));
            using var dialog = new DialogAnswer(confirm: true);

            await MakeHeldChange(vm, change);

            Assert.Equal(0, dialog.Calls);
            Assert.Equal(
                kind == PendingRecoveryKind.LeftOn
                    ? PerformanceViewModel.ChangeUnderLeftoverGameMode
                    : PerformanceViewModel.GameModeNotKnown,
                vm.StatusMessage);
            Assert.NotNull(service.LoadSnapshot());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(HeldChanges))]
    public async Task AChange_WhenGameModeStartsWhileItsQuestionIsOpen_IsRefusedOnceTheLockIsHeld(string change)
    {
        var dir = CreateTempDirectory();
        try
        {
            using var service = NewService(dir);
            var on = false;
            var gaming = Substitute.For<IGamingProfileService>();
            gaming.IsActive.Returns(_ => on);
            using var vm = await ViewModelOverARecord(service, gaming);

            var previousDialog = DialogService.Instance;
            var dialog = Substitute.For<IDialogService>();
            // Game mode starts while the question is open, and the answer is yes.
            dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
            {
                on = true;
                return true;
            });
            DialogService.Instance = dialog;
            try
            {
                await MakeHeldChange(vm, change);
            }
            finally { DialogService.Instance = previousDialog; }

            dialog.ReceivedWithAnyArgs(1).Confirm(default!, default!);
            Assert.Equal(PerformanceViewModel.ChangeDuringGameMode, vm.StatusMessage);
            Assert.NotNull(service.LoadSnapshot());
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
