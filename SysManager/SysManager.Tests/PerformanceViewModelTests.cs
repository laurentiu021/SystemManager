// SysManager · PerformanceViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using System.Reflection;
using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PerformanceViewModel"/>. Verifies initial state,
/// per-section commands, and property defaults.
/// </summary>
/// <remarks>
/// In the DialogService collection (serialized): the operation-lock regression
/// tests below swap the global <see cref="DialogService.Instance"/> and take the
/// shared <see cref="OperationLockService"/>, both process-wide singletons.
/// </remarks>
[Collection("ProcessWideStatics")]
public class PerformanceViewModelTests
{
    /// <summary>A view model whose process calls — powercfg among them — go to a substituted runner.</summary>
    /// <param name="completeInitialization">Let the constructor's process calls finish, so initialization settles.</param>
    /// <param name="configure">Applied after the defaults, so a test can make one specific call answer differently.</param>
    /// <param name="processes">What Trim RAM works through. Left out, it is none, never the machine's own (#2557).</param>
    /// <param name="trim">The trim call for each of <paramref name="processes"/>.</param>
    /// <param name="writeGpu">The NVIDIA setting's write. Left out, there is no card, never this PC's own.</param>
    /// <param name="setUiEffects">The visual-effects write. Left out, nothing, never this PC's own.</param>
    /// <param name="setGameMode">The Game Mode write, as above.</param>
    /// <param name="setXboxGameBar">The Xbox Game Bar write, as above.</param>
    /// <param name="configDir">The folder the service keeps its files in; a new one of the test's own unless given.</param>
    private static PerformanceViewModel NewVm(bool completeInitialization = false, Action<IPowerShellRunner>? configure = null,
                                              Func<System.Diagnostics.Process[]>? processes = null,
                                              Func<System.Diagnostics.Process, bool>? trim = null,
                                              Func<bool, (bool Found, bool Ok)>? writeGpu = null,
                                              Action<bool>? setUiEffects = null,
                                              Action<bool>? setGameMode = null,
                                              Action<bool, bool>? setXboxGameBar = null,
                                              string? configDir = null)
    {
        var ps = Substitute.For<IPowerShellRunner>();
        var processCall = ps.RunProcessAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
        if (completeInitialization)
        {
            processCall.Returns(0);
            configure?.Invoke(ps);
        }
        else
        {
            // Keep constructor-state tests independent of the host registry/P/Invoke state.
            // Tests that need initialized state opt in and await InitializationComplete.
            var pending = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            processCall.Returns(pending.Task);
        }
        configDir ??= Path.Combine(
            Path.GetTempPath(),
            "SysManagerPerformanceTests",
            Guid.NewGuid().ToString("N"));
        return new(new PerformanceService(ps, new RestorePointService(ps), configDir, processes, trim,
                                          writeGpu: writeGpu, setUiEffects: setUiEffects,
                                          setGameMode: setGameMode, setXboxGameBar: setXboxGameBar),
                   NoGamingSession());
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

    // ── Commands exist ──

    [Fact]
    public void Constructor_GlobalCommands_Exist()
    {
        var vm = NewVm();
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.RestoreAllCommand);
    }

    [Fact]
    public void Constructor_PerSectionCommands_Exist()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ApplyPowerPlanCommand);
        Assert.NotNull(vm.ApplyVisualEffectsCommand);
        Assert.NotNull(vm.ApplyGameModeCommand);
        Assert.NotNull(vm.ApplyXboxGameBarCommand);
        Assert.NotNull(vm.ApplyGpuCommand);
        Assert.NotNull(vm.ApplyProcessorStateCommand);
    }

    // ── Default state ──

    [Fact]
    public void Constructor_Profile_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Profile);
    }

    [Fact]
    public void Constructor_Summary_HasDefaultValue()
    {
        var vm = NewVm();
        Assert.False(string.IsNullOrEmpty(vm.Summary));
    }

    [Fact]
    public void Constructor_SelectedPlan_DefaultBalanced()
    {
        var vm = NewVm();
        Assert.Equal("balanced", vm.SelectedPlan);
    }

    [Fact]
    public async Task Initialization_WithoutPersistedSnapshot_HasSnapshotFalse()
    {
        using var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        Assert.False(vm.HasSnapshot);
    }

    [Fact]
    public void Constructor_NeedsReboot_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.NeedsReboot);
    }

    [Fact]
    public void Constructor_WantToggles_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.WantVisualEffectsReduced);
        Assert.False(vm.WantGameModeOff);
        Assert.False(vm.WantXboxGameBarOff);
        Assert.False(vm.WantGpuMaxPerformance);
        Assert.False(vm.WantProcessorMaxState);
    }

    // ── Property changes ──

    // SelectedPlan_CanBeChanged was removed as a pure setter round-trip: the default is pinned by
    // Constructor_SelectedPlan_DefaultBalanced and the notification by SelectedPlan_NotifiesPropertyChanged.

    [Fact]
    public void WantVisualEffectsReduced_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantVisualEffectsReduced = true;
        Assert.True(vm.WantVisualEffectsReduced);
        vm.WantVisualEffectsReduced = false;
        Assert.False(vm.WantVisualEffectsReduced);
    }

    [Fact]
    public void WantGameModeOff_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantGameModeOff = true;
        Assert.True(vm.WantGameModeOff);
    }

    [Fact]
    public void WantXboxGameBarOff_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantXboxGameBarOff = true;
        Assert.True(vm.WantXboxGameBarOff);
    }

    [Fact]
    public void WantGpuMaxPerformance_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantGpuMaxPerformance = true;
        Assert.True(vm.WantGpuMaxPerformance);
    }

    [Fact]
    public void WantProcessorMaxState_CanBeToggled()
    {
        var vm = NewVm();
        vm.WantProcessorMaxState = true;
        Assert.True(vm.WantProcessorMaxState);
    }

    [Fact]
    public void NvidiaGpuName_DefaultEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.NvidiaGpuName);
    }

    [Fact]
    public void HasNvidiaGpu_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.HasNvidiaGpu);
    }

    // This class is the reason PropertyChangeRecorder exists (#2169, #2175). Its local version of the
    // helper lived here, and the concurrent writer that made it necessary is specific to this file: the
    // constructor calls InitializeAsync, whose continuations resume on the thread pool because
    // ViewModelBase awaits with ConfigureAwait(false), and NewVm deliberately does NOT wait for it — its
    // RunProcessAsync returns a task that never completes, which is what keeps these constructor-state
    // tests independent of the host. So the writer cannot be removed; the recorder has to tolerate it.
    // It took ~5,000 concurrent tests to surface once and passed six for six in isolation, which is
    // precisely why it was worth fixing rather than watching.

    [Fact]
    public void SelectedPlan_NotifiesPropertyChanged()
    {
        var vm = NewVm();
        var changed = vm.RecordPropertyChanges();
        vm.SelectedPlan = "high";
        Assert.Contains("SelectedPlan", changed);
    }

    // ── Processor state lock (issue #103) ──

    [Fact]
    public void IsProcessorStateLocked_DefaultFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsProcessorStateLocked);
    }

    [Fact]
    public void IsProcessorStateEditable_InverseOfLocked()
    {
        var vm = NewVm();
        Assert.True(vm.IsProcessorStateEditable);
        vm.IsProcessorStateLocked = true;
        Assert.False(vm.IsProcessorStateEditable);
    }

    [Fact]
    public void IsProcessorStateLocked_NotifiesEditable()
    {
        var vm = NewVm();
        var changed = vm.RecordPropertyChanges();
        vm.IsProcessorStateLocked = true;
        Assert.Contains("IsProcessorStateLocked", changed);
        Assert.Contains("IsProcessorStateEditable", changed);
    }

    [Fact]
    public void TrimRamCommand_IsAsync()
    {
        // TrimRam enumerates every process and calls EmptyWorkingSet (P/Invoke) on each;
        // it must run off the UI thread. An IAsyncRelayCommand proves the work is awaited
        // (offloaded), not executed synchronously on the dispatcher.
        var vm = NewVm();
        Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(vm.TrimRamCommand);
    }

    [Fact]
    public async Task TrimRam_ReportsTheProcessesItsServiceTrimmed()
    {
        // #2557. The command trims through the service it was given, so a test drives it with a list of its own. It
        // called the static trim, which a test could only run by trimming every process on the machine.
        var answers = new Queue<bool>([true, true, false]);
        var vm = NewVm(completeInitialization: true,
            processes: () => [new(), new(), new()], trim: _ => answers.Dequeue());
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.TrimRamCommand.ExecuteAsync(null);

        Assert.Equal("✓ Trimmed working set of 2 processes.", vm.StatusMessage);
        Assert.Empty(answers);
    }

    // ── System-modification lock ──
    //
    // Every mutating command (Apply* / Restore All / Trim RAM / Create restore point /
    // Toggle hibernation) must serialize through OperationLockService before touching the
    // system. Without it, Restore All can null the snapshot mid-Apply and leave a tweak
    // applied with nothing to revert it. These pin the guard the way ShortcutCleaner's
    // DeleteSelected_WhenDiskLocked_DoesNotDelete pins its Disk-lock guard: stub the dialog
    // to "Yes", hold the SystemModification lock, and prove the command bails at the guard.

    private static async Task SeedSnapshotAsync(PerformanceViewModel vm)
    {
        // Await initialization BEFORE seeding. InitAsync hydrates the persisted snapshot with
        // `_snapshot ??= await Task.Run(_service.LoadSnapshot)`, which reads the field, performs
        // disk I/O, then assigns — so a value written during that await is overwritten by the
        // deferred assignment. Seeding first therefore raced the load: on a fast machine the I/O
        // finished before the seed and the test passed, on a slower runner it did not and
        // `_snapshot` came back null, making Restore All bail at its "nothing to restore" guard
        // before ever reaching the lock guard under test. Ordering the wait explicitly keeps
        // this deterministic without a sleep.
        await vm.InitializationComplete;

        // Restore All early-returns when there is no snapshot (before the lock guard). Save one through the tab's
        // own service, so the command reaches the guard we're testing. On disk rather than in the private field:
        // Restore All reads the record from disk, because Undo Changes can delete it while the tab holds a copy
        // (#1525), so a snapshot only in memory is one the tab would rightly find gone.
        var snapshot = new PerformanceService.OriginalSnapshot(
            PowerPlanGuid: "381b4222-f694-41f0-9685-ff5bb260df2e", PowerPlanName: "Balanced", UiEffectsEnabled: true,
            GameModeEnabled: true, XboxGameBarEnabled: true, XboxGameDvrEnabled: true,
            GpuDynamicPstate: true, ProcessorMinPercentAc: 5, NvidiaSubKey: null);
        var service = (PerformanceService)typeof(PerformanceViewModel)
            .GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;
        Assert.True(service.SaveSnapshot(snapshot));
    }

    [Fact]
    public async Task RestoreAll_WhenSystemModificationLocked_BailsAtGuard()
    {
        // completeInitialization: the seed below must happen after InitAsync has finished, and
        // InitializationComplete only settles once the stubbed process calls return.
        // The plan switch fails, so a guard that stopped holding would throw at the first step of the restore —
        // before visual effects, Game Mode and the Game Bar, which are written to this PC's own registry. The seed
        // is a real record on disk, which the restore would otherwise carry out.
        IPowerShellRunner? runner = null;
        var vm = NewVm(completeInitialization: true, ps =>
        {
            runner = ps;
            ps.RunProcessAsync("powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                               Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(5);
        });
        await SeedSnapshotAsync(vm);

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true); // user clicks "Yes"
        DialogService.Instance = dialog;

        // Hold the SystemModification lock so Restore All must bail rather than race an Apply.
        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.RestoreAllCommand.ExecuteAsync(null);

            // Confirm was shown, but the lock was unavailable → the command reported the contention and did NOT run
            // the restore: its first step, switching the power plan, never ran.
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("already running", vm.StatusMessage);
            await runner!.DidNotReceive().RunProcessAsync("powercfg.exe",
                Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task TrimRam_WhenSystemModificationLocked_BailsAtGuard()
    {
        // Initialisation is awaited before the command runs, and that is load-bearing rather than tidy. The
        // constructor fires InitAsync and forgets it; InitAsync awaits the snapshot gate, so its continuation
        // is still pending here. `await ExecuteAsync` yields, the continuation gets pumped, and RefreshAsync
        // overwrites StatusMessage with "Reading performance settings…" — on top of the guard message this
        // test is asserting. It passed 40/40 locally and failed on CI, which is what an unpumped continuation
        // looks like: the race is decided by how loaded the machine is.
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;

        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.TrimRamCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            Assert.Contains("already running", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task ApplyPowerPlan_WhenSystemModificationLocked_BailsAtGuard()
    {
        // Awaited for the reason TrimRam's sibling above documents: the constructor's pending InitAsync
        // continuation otherwise lands during `await ExecuteAsync` and overwrites the guard message. This is
        // the test that actually went red on CI while passing 40/40 locally.
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        // Force SelectedPlan away from the current plan so the "already set" early-return
        // (before the lock guard) doesn't short-circuit the command first. AFTER the await, because
        // initialisation ends in SyncTogglesFromProfile, which assigns SelectedPlan itself.
        vm.SelectedPlan = "ultimate";

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;

        using var held = OperationLockService.Instance.TryAcquire(
            OperationCategory.SystemModification, "Test Holder");
        Assert.NotNull(held);
        try
        {
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);
            Assert.Contains("already running", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ── What was asked for stands, while the settings are read again under the question (#1525) ──
    //
    // Undo Changes can put Performance Mode back while one of this tab's questions is open, and the tab then reads its
    // settings again, which sets the toggles to what is on the PC. Each callback below does that to the toggle the
    // command reads, as the read would.

    /// <summary>A question that, while it is open, does what the tab's read after a put-back does, and says yes.</summary>
    private static IDisposable AnswerYesWhile(Action meanwhile)
    {
        var previous = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(_ =>
        {
            meanwhile();
            return true;
        });
        DialogService.Instance = dialog;
        return new Restore(() => DialogService.Instance = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // ── a plan that is none of the three options (#2717) ──
    // Every other plan read as Balanced: Balanced was ticked under the real plan's name, and Apply on Balanced said it
    // was set already, so a PC on Power saver or an OEM plan could not get to Balanced from this tab.

    /// <summary>A runner configuration under which powercfg reports <paramref name="line"/> as the active plan.</summary>
    private static Action<IPowerShellRunner> ActivePlanIs(string? line) => ps =>
        ps.RunProcessAsync("powercfg.exe", "/getactivescheme", Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
          .Returns(_ =>
          {
              if (line is not null)
                  ps.LineReceived += Raise.Event<Action<PowerShellLine>>(PowerShellLine.Output(line));
              return 0;
          });

    [Theory]
    [InlineData("Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)")]
    [InlineData("Power Scheme GUID: 9897998c-92de-4669-853f-b7cd3ecb2790  (AMD Ryzen Balanced)")]
    [InlineData("Power Scheme GUID: 0d2e9b4f-1a0c-4c6b-8d7e-2f3a4b5c6d7e  (My plan)")]
    [InlineData(null)]   // the active plan could not be read
    public async Task APlanThatIsNoneOfTheThree_SelectsNone(string? activePlan)
    {
        var vm = NewVm(completeInitialization: true, ActivePlanIs(activePlan));
        await vm.InitializationComplete;

        Assert.Equal(PerformanceViewModel.OtherPlan, vm.SelectedPlan);
        Assert.False(vm.IsProcessorStateLocked);
    }

    [Fact]
    public async Task AnUltimateCopyNamedInEnglish_StillSelectsUltimate()
    {
        var vm = NewVm(completeInitialization: true,
            ActivePlanIs("Power Scheme GUID: 3ff9831b-6f80-4830-8178-736cd4229e7b  (Ultimate Performance)"));
        await vm.InitializationComplete;

        Assert.Equal("ultimate", vm.SelectedPlan);
    }

    [Fact]
    public async Task TheUltimateCopyItMade_UnderATranslatedName_SelectsUltimate_AndApplyMakesNoOther()
    {
        // Home and Pro hide the stock scheme, so the Ultimate plan there is a copy, named in the display language. It
        // read as a plan that is none of the three, and every Apply on Ultimate made another copy (#2717).
        const string copy = "3ff9831b-6f80-4830-8178-736cd4229e7b";
        var dir = Path.Combine(Path.GetTempPath(), "SysManagerPerformanceTests", Guid.NewGuid().ToString("N"));
        try
        {
            var maker = Substitute.For<IPowerShellRunner>();
            maker.RunProcessAsync("powercfg.exe", $"-duplicatescheme {PerformanceService.UltimatePerfScheme}",
                                  Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
                 .Returns(_ =>
                 {
                     maker.LineReceived += Raise.Event<Action<PowerShellLine>>(
                         PowerShellLine.Output($"Identificador del esquema: {copy}  (Máximo rendimiento)"));
                     return 0;
                 });
            using (var made = new PerformanceService(maker, new RestorePointService(maker), dir, processes: null))
                Assert.Equal(copy, await made.EnsureUltimatePerformancePlanAsync());

            IPowerShellRunner? runner = null;
            var vm = NewVm(completeInitialization: true, ps =>
            {
                runner = ps;
                ActivePlanIs($"Identificador del esquema: {copy}  (Máximo rendimiento)")(ps);
            }, configDir: dir);
            await vm.InitializationComplete;

            Assert.Equal("ultimate", vm.SelectedPlan);
            Assert.True(vm.IsProcessorStateLocked);

            using (AnswerYesWhile(() => { }))
                await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

            Assert.Equal("Power plan is already set to the selected option.", vm.StatusMessage);
            await runner!.DidNotReceive().RunProcessAsync(
                "powercfg.exe", Arg.Is<string>(a => a.StartsWith("-duplicatescheme", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Balanced_IsAppliedFromPowerSaver()
    {
        IPowerShellRunner? runner = null;
        var vm = NewVm(completeInitialization: true, ps =>
        {
            runner = ps;
            ActivePlanIs("Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)")(ps);
        });
        await vm.InitializationComplete;
        vm.SelectedPlan = "balanced";

        using (AnswerYesWhile(() => { }))
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

        await runner!.Received(1).RunProcessAsync("powercfg.exe", $"/setactive {PerformanceService.BalancedGuid}",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.Equal("Power plan set to Balanced.", vm.StatusMessage);
    }

    [Fact]
    public async Task Apply_WithNoneOfTheThreeChosen_AsksToChooseOne_AndChangesNothing()
    {
        IPowerShellRunner? runner = null;
        var vm = NewVm(completeInitialization: true, ps =>
        {
            runner = ps;
            ActivePlanIs("Power Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)")(ps);
        });
        await vm.InitializationComplete;

        await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

        Assert.Equal("Choose Balanced, High Performance or Ultimate Performance first.", vm.StatusMessage);
        await runner!.DidNotReceive().RunProcessAsync("powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task ApplyPowerPlan_SwitchesToThePlanItsQuestionAskedAbout()
    {
        IPowerShellRunner? runner = null;
        var vm = NewVm(completeInitialization: true, ps => runner = ps);
        await vm.InitializationComplete;
        var onThePc = vm.SelectedPlan;
        vm.SelectedPlan = "high";
        Assert.NotEqual(onThePc, vm.SelectedPlan);

        using (AnswerYesWhile(() => vm.SelectedPlan = onThePc))
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

        await runner!.Received(1).RunProcessAsync("powercfg.exe", $"/setactive {PerformanceService.HighPerfGuid}",
            Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>());
        Assert.Equal("Power plan set to High Performance.", vm.StatusMessage);
    }

    [Fact]
    public async Task ApplyProcessorState_SetsTheMinimumItsQuestionAskedAbout()
    {
        IPowerShellRunner? runner = null;
        var vm = NewVm(completeInitialization: true, ps => runner = ps);
        await vm.InitializationComplete;
        Assert.False(vm.IsProcessorStateLocked);
        Assert.False(vm.WantProcessorMaxState);
        vm.WantProcessorMaxState = true;

        using (AnswerYesWhile(() => vm.WantProcessorMaxState = false))
            await vm.ApplyProcessorStateCommand.ExecuteAsync(null);

        await runner!.Received(1).RunProcessAsync("powercfg.exe",
            "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 100", Arg.Any<CancellationToken>(),
            Arg.Any<System.Text.Encoding?>());
    }

    [Fact]
    public async Task RestoreAll_RestoresTheRecordItsQuestionDescribed_EvenIfTheTabsCopyWasClearedMeanwhile()
    {
        // The plan switch fails, so the restore stops at its first step, before anything written to this PC's own
        // registry: what is asserted is only that it was the record the question described that it set out to restore.
        var vm = NewVm(completeInitialization: true, ps =>
            ps.RunProcessAsync("powercfg.exe", Arg.Is<string>(a => a.StartsWith("/setactive ", StringComparison.Ordinal)),
                               Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(5));
        await SeedSnapshotAsync(vm);
        var field = typeof(PerformanceViewModel).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using (AnswerYesWhile(() => field.SetValue(vm, null)))
            await vm.RestoreAllCommand.ExecuteAsync(null);

        Assert.Equal("Restore all settings failed: powercfg failed to activate the power plan (exit code 5).", vm.StatusMessage);
    }

    // ── Success is reported only for what happened (#2438) ──
    //
    // Both commands run against a substituted runner, so no plan is switched and hibernation is not touched.

    [Fact]
    public async Task ApplyPowerPlan_WhenNoUltimatePlanComesBack_SaysSo_NotThatItIsSet()
    {
        // EnsureUltimatePerformancePlanAsync returns "" when Windows yields no plan — every powercfg call here
        // answers with nothing. The command skipped the switch for an empty GUID and still said "Power plan set".
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.SelectedPlan = "ultimate";   // after the await: initialization assigns SelectedPlan itself
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Power plan set", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToggleHibernation_WhenPowercfgFails_SaysSo_NotThatItChanged()
    {
        // SetHibernationAsync discarded powercfg's exit code, so a machine without hibernation support was told
        // "✓ Hibernation enabled." Both directions answer 1 here, so the test holds whichever state the host is in.
        using var elevated = Helpers.AdminHelper.ForceElevation(true);
        var vm = NewVm(completeInitialization: true, ps =>
            ps.RunProcessAsync("powercfg.exe", Arg.Is<string>(a => a.StartsWith("/hibernate", StringComparison.Ordinal)),
                               Arg.Any<CancellationToken>(), Arg.Any<System.Text.Encoding?>())
              .Returns(1));
        await vm.InitializationComplete;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ToggleHibernationCommand.ExecuteAsync(null);

        Assert.DoesNotContain("✓", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("failed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── Disposal and the recovery snapshot ──
    // Every Apply secures a snapshot first so the change can be reverted. Dispose clears that snapshot
    // and disposes the gate that makes it exclusive, so a tab closed mid-Apply is the moment where a
    // tweak could be applied with nothing left to revert it.

    [Fact]
    public async Task ApplyAfterDispose_RefusesRatherThanChangingAnythingUnprotected()
    {
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;

        try
        {
            vm.Dispose();

            // The snapshot path cannot secure a snapshot on a disposed view model, so the Apply must
            // report the refusal rather than proceeding. The command catches the refusal itself, so what
            // is observable is the status text — and crucially NOT a success message.
            await vm.ApplyPowerPlanCommand.ExecuteAsync(null);

            Assert.DoesNotContain("Switched", vm.StatusMessage);
            Assert.DoesNotContain("Applied", vm.StatusMessage);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    [Fact]
    public async Task DoubleDispose_IsHarmless()
    {
        // MainWindowViewModel disposes every tab at shutdown, and a tab can also be disposed by its own
        // teardown — the second call must not throw on the already-disposed snapshot gate.
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        vm.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task Dispose_ClearsTheSnapshotItHeld()
    {
        // The clear moved inside the gate; it must still happen, or Dispose would leak a snapshot
        // reference and the "cleared on teardown" behaviour would silently disappear.
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;

        var field = typeof(PerformanceViewModel)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, new PerformanceService.OriginalSnapshot(
            PowerPlanGuid: "guid", PowerPlanName: "Balanced", UiEffectsEnabled: true,
            GameModeEnabled: true, XboxGameBarEnabled: true, XboxGameDvrEnabled: true,
            GpuDynamicPstate: true, ProcessorMinPercentAc: 5, NvidiaSubKey: null));
        Assert.NotNull(field.GetValue(vm));

        vm.Dispose();

        Assert.Null(field.GetValue(vm));
    }

    // ---------- what the hibernation confirmation says (#2505) ----------

    [Fact]
    public async Task DisablingHibernation_Confirmation_SaysFastStartupAndHybridSleepGoToo()
    {
        using var elevated = Helpers.AdminHelper.ForceElevation(true);
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.IsHibernationEnabled = true;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ToggleHibernationCommand.ExecuteAsync(null);

        var message = Assert.Single(dialog.Messages);
        Assert.Contains("Disable hibernation?", message, StringComparison.Ordinal);
        Assert.Contains("turns off Fast Startup and hybrid sleep", message, StringComparison.Ordinal);
    }

    // ---------- the reboot notice, and a read that overlaps a command (#2607) ----------

    /// <summary>A graphics change says it needs a reboot after the read that shows the new state, too.</summary>
    /// <remarks>
    /// The Apply set the notice and "Reboot required.", then read the settings to show them, and that read cleared
    /// the notice and replaced the line with "Settings loaded.", so both were gone at once. The write is the
    /// substitute's; this PC's graphics setting is not touched. <c>HasNvidiaGpu</c> is set by hand because the read
    /// decides it from the registry of the PC running the test.
    /// </remarks>
    [Fact]
    public async Task ApplyGpu_KeepsItsRebootNotice_AfterTheReadThatShowsTheNewState()
    {
        var vm = NewVm(completeInitialization: true, writeGpu: _ => (Found: true, Ok: true));
        await vm.InitializationComplete;
        vm.HasNvidiaGpu = true;
        vm.WantGpuMaxPerformance = !vm.Profile.GpuMaxPerformance;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyGpuCommand.ExecuteAsync(null);

        Assert.True(vm.NeedsReboot, "the reboot notice was cleared by the read after the change");
        Assert.Contains("Reboot required", vm.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>A Refresh does not clear a reboot that is still pending: nothing has restarted.</summary>
    [Fact]
    public async Task Refresh_KeepsARebootThatIsStillPending()
    {
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.NeedsReboot = true;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.NeedsReboot);
    }

    /// <summary>Refresh, and F5 through it, is off while a command runs, and comes back when it ends.</summary>
    /// <remarks>
    /// Refresh had no <c>CanExecute</c>, so pressing it during an Apply started a read beside it: the read's own end
    /// cleared <c>IsBusy</c> while the Apply was still running, and its status line replaced the Apply's.
    /// </remarks>
    [Fact]
    public async Task Refresh_IsOffWhileTheTabIsBusy()
    {
        var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        var raised = 0;
        vm.RefreshCommand.CanExecuteChanged += (_, _) => raised++;
        Assert.True(vm.RefreshCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.Same(vm.RefreshCommand, vm.RefreshOnF5);

        vm.IsBusy = false;
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(raised >= 2, $"CanExecuteChanged was raised {raised} time(s), so the button would not follow IsBusy");
    }

    // ── Each Apply touches only its own section (#2836) ──
    //
    // Every Apply ended in a read that set every switch from the PC, so a switch changed in another section and not
    // applied yet went back, and that section's Apply then found nothing to do. The writes go to recorders here; the
    // reads are this PC's, which are only read.

    [Fact]
    public async Task AnAppliedSection_LeavesAnotherSectionsSwitchAsTheyLeftIt()
    {
        var uiEffects = new List<bool>();
        using var vm = NewVm(completeInitialization: true, setUiEffects: uiEffects.Add);
        await vm.InitializationComplete;
        // The opposite of what this PC has, so it is a change the person made, which the read cannot set back.
        var gameModeOff = vm.Profile.GameModeEnabled;
        vm.WantGameModeOff = gameModeOff;
        var reduce = !vm.Profile.VisualEffectsReduced;
        vm.WantVisualEffectsReduced = reduce;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyVisualEffectsCommand.ExecuteAsync(null);

        Assert.Equal([!reduce], uiEffects);
        Assert.Equal(gameModeOff, vm.WantGameModeOff);
    }

    [Fact]
    public async Task ADeclinedApply_PutsBackOnlyItsOwnSwitch()
    {
        using var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.Profile = new PerformanceProfile { GameModeEnabled = true, VisualEffectsReduced = false };
        vm.WantGameModeOff = true;
        vm.WantVisualEffectsReduced = true;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ApplyVisualEffectsCommand.ExecuteAsync(null);

        Assert.False(vm.WantVisualEffectsReduced);
        Assert.True(vm.WantGameModeOff);
    }

    [Fact]
    public async Task AnApplyTheLockRefuses_PutsBackOnlyItsOwnSwitch()
    {
        using var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.Profile = new PerformanceProfile { GameModeEnabled = true, VisualEffectsReduced = false };
        vm.WantGameModeOff = true;
        vm.WantVisualEffectsReduced = true;
        using var dialog = new DialogAnswer(confirm: true);
        using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Undo");
        Assert.NotNull(held);

        await vm.ApplyGameModeCommand.ExecuteAsync(null);

        Assert.False(vm.WantGameModeOff);
        Assert.True(vm.WantVisualEffectsReduced);
    }

    // ── A half-on Xbox Game Bar (#2836) ──

    [Theory]
    [InlineData(true)]    // asked to turn it off: the overlay is still on
    [InlineData(false)]   // asked to turn it on: per-game recording is still off
    public async Task AHalfOnXboxGameBar_IsInNeitherState_SoItsApplyAsks(bool turnOff)
    {
        using var vm = NewVm(completeInitialization: true);
        await vm.InitializationComplete;
        vm.Profile = new PerformanceProfile { XboxGameBarOn = true, XboxGameDvrOn = false };
        vm.WantXboxGameBarOff = turnOff;
        using var dialog = new DialogAnswer(confirm: false);

        await vm.ApplyXboxGameBarCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.NotEqual("Xbox Game Bar is already in the selected state.", vm.StatusMessage);
    }

    [Fact]
    public async Task TurningAHalfOnXboxGameBarOff_WritesBothValuesOff()
    {
        var writes = new List<(bool Bar, bool Dvr)>();
        using var vm = NewVm(completeInitialization: true, setXboxGameBar: (bar, dvr) => writes.Add((bar, dvr)));
        await vm.InitializationComplete;
        vm.Profile = new PerformanceProfile { XboxGameBarOn = true, XboxGameDvrOn = false };
        vm.WantXboxGameBarOff = true;
        using var dialog = new DialogAnswer(confirm: true);

        await vm.ApplyXboxGameBarCommand.ExecuteAsync(null);

        Assert.Equal([(false, false)], writes);
    }
}
