// SysManager · PrivacyViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Microsoft.Win32;
using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyViewModel"/>. Verifies toggle population,
/// category filtering, pending-change tracking, discard behavior, and the
/// staging of privacy choices imported from a profile, without writing to
/// the registry.
/// </summary>
[Collection("ProcessWideStatics")]
public class PrivacyViewModelTests
{
    // The VM loads its toggles asynchronously off the UI thread (so startup isn't blocked);
    // wait for that init to finish before asserting loaded state, so the tests observe the
    // populated collections deterministically instead of racing the background load.
    private static PrivacyViewModel NewVm() => NewVm(NoRestorePoint());

    private static PrivacyViewModel NewVm(ISessionRestorePoint restorePoint)
    {
        var vm = new PrivacyViewModel(new PrivacyService(), restorePoint, new PrivacyChoicesHandoff());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>
    /// A seam that answers "no point was created" — the common case on a consumer machine, where
    /// System Restore is off or Windows has already spent its 24-hour allowance.
    /// </summary>
    private static ISessionRestorePoint NoRestorePoint()
    {
        var rp = Substitute.For<ISessionRestorePoint>();
        rp.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        return rp;
    }

    [Fact]
    public void Constructor_Toggles_Populated_With12Items()
    {
        var vm = NewVm();
        Assert.Equal(12, vm.Toggles.Count);
    }

    [Fact]
    public void Constructor_Categories_ContainsAllPlusSpecific()
    {
        var vm = NewVm();
        Assert.Contains("All", vm.Categories);
        Assert.Contains("Telemetry", vm.Categories);
        Assert.Contains("UI Declutter", vm.Categories);
        Assert.Contains("Features", vm.Categories);
        Assert.Equal(4, vm.Categories.Count);
    }

    [Fact]
    public void Constructor_SelectedCategory_DefaultsToAll()
    {
        var vm = NewVm();
        Assert.Equal("All", vm.SelectedCategory);
    }

    [Theory]
    [InlineData("Telemetry", 4)]
    [InlineData("UI Declutter", 4)]
    [InlineData("Features", 4)]
    public void FilterByCategory_ShowsOnlyMatchingToggles(string category, int expectedCount)
    {
        var vm = NewVm();
        vm.SelectedCategory = category;
        Assert.Equal(expectedCount, vm.FilteredToggles.Count);
        Assert.All(vm.FilteredToggles, t => Assert.Equal(category, t.Category));
    }

    [Fact]
    public void FilterByAll_ShowsAllToggles()
    {
        var vm = NewVm();
        vm.SelectedCategory = "Telemetry"; // Filter first
        vm.SelectedCategory = "All";       // Then reset
        Assert.Equal(12, vm.FilteredToggles.Count);
    }

    [Fact]
    public void FilteredToggles_InitiallyMatchesAll()
    {
        var vm = NewVm();
        Assert.Equal(vm.Toggles.Count, vm.FilteredToggles.Count);
    }

    [Fact]
    public void Constructor_NoPendingChanges_AfterLoad()
    {
        var vm = NewVm();
        Assert.Equal(0, vm.PendingChangeCount);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void TogglingValue_IncrementsPendingChangeCount()
    {
        var vm = NewVm();
        var first = vm.Toggles[0];
        first.IsEnabled = !first.IsEnabled;

        Assert.Equal(1, vm.PendingChangeCount);
        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public void TogglingValueBackToBaseline_ResetsPendingCount()
    {
        var vm = NewVm();
        var first = vm.Toggles[0];
        var original = first.IsEnabled;

        first.IsEnabled = !original;
        first.IsEnabled = original;

        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public void DiscardChanges_RestoresAllTogglesToBaseline()
    {
        var vm = NewVm();
        var baseline = vm.Toggles.Select(t => t.IsEnabled).ToList();

        // Flip every toggle.
        foreach (var t in vm.Toggles)
            t.IsEnabled = !t.IsEnabled;

        vm.DiscardChangesCommand.Execute(null);

        for (int i = 0; i < vm.Toggles.Count; i++)
            Assert.Equal(baseline[i], vm.Toggles[i].IsEnabled);
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public void StatusMessage_MentionsPending_WhenChangesQueued()
    {
        var vm = NewVm();
        vm.Toggles[0].IsEnabled = !vm.Toggles[0].IsEnabled;

        Assert.Contains("pending", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyChanges_WithNoPending_SetsNoChangesMessage()
    {
        var vm = NewVm();
        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Contains("no changes", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyChanges_WithNoPending_TakesNoRestorePoint()
    {
        // Nothing is written, so nothing needs protecting — and Windows only grants about one point
        // per 24 hours, so spending it on a no-op would deny it to the change that follows.
        var restorePoint = NoRestorePoint();
        var vm = NewVm(restorePoint);

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyChanges_WhenTheUserDeclines_TakesNoRestorePoint()
    {
        var restorePoint = NoRestorePoint();
        using var dialog = new DialogAnswer(confirm: false);

        var vm = NewVm(restorePoint);
        vm.Toggles[0].IsEnabled = !vm.Toggles[0].IsEnabled;

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        // The snapshot is taken after the confirmation, so a declined apply costs the user nothing.
        await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyChanges_WhileSystemModificationLocked_RefusesAndTakesNoRestorePoint()
    {
        // #2510. A batch that is cancelled with nothing to name it when SysManager closes mid-run now
        // shares the lock the other tabs that take a restore point before changing the system already do.
        // It answers yes, so the service reads and writes under a throwaway HKCU key: the lock refuses before any
        // write, and a regression that let the apply through changes that key, never this machine's policies.
        var rootName = @"Software\SysManagerTests\PrivacyVm_" + Guid.NewGuid().ToString("N");
        var root = Registry.CurrentUser.CreateSubKey(rootName, writable: true)!;
        try
        {
            var restorePoint = NoRestorePoint();
            using var dialog = new DialogAnswer(confirm: true);
            var vm = new PrivacyViewModel(new PrivacyService(hkcuRoot: root, hklmRoot: root), restorePoint,
                new PrivacyChoicesHandoff());
            await vm.InitializationComplete;
            vm.Toggles[0].IsEnabled = !vm.Toggles[0].IsEnabled;
            var pendingBefore = vm.PendingChangeCount;
            using var held = OperationLockService.Instance.TryAcquire(OperationCategory.SystemModification, "Tweaks Hub");
            Assert.NotNull(held);

            await vm.ApplyChangesCommand.ExecuteAsync(null);

            await restorePoint.DidNotReceive().EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            Assert.Empty(root.GetSubKeyNames());   // reading creates nothing; any write would have
            Assert.Equal(pendingBefore, vm.PendingChangeCount);
            Assert.Equal("Cannot start — Tweaks Hub is already running.", vm.StatusMessage);
        }
        finally
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task ApplyChanges_SaysWhatTheRestorePointDoesToSystemProtection()
    {
        // #2483. The first change of a session takes the shared restore point, which turns System Protection
        // back on when it is off, and this confirmation never said so.
        var restorePoint = NoRestorePoint();
        restorePoint.ConfirmationNotice.Returns(SessionRestorePointTests.NoticeStandIn);
        using var dialog = new DialogAnswer(confirm: false);

        var vm = NewVm(restorePoint);
        vm.Toggles[0].IsEnabled = !vm.Toggles[0].IsEnabled;

        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.EndsWith(SessionRestorePointTests.NoticeStandIn, Assert.Single(dialog.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyChanges_WhenUserDeclinesConfirm_DoesNotApply_AndKeepsPending()
    {
        var prevDialog = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks "No"
        DialogService.Instance = dialog;
        try
        {
            var vm = NewVm();
            vm.Toggles[0].IsEnabled = !vm.Toggles[0].IsEnabled; // create a pending change
            var pendingBefore = vm.PendingChangeCount;

            await vm.ApplyChangesCommand.ExecuteAsync(null);

            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            // Declining must NOT write to the registry: the change is still pending.
            Assert.Equal(pendingBefore, vm.PendingChangeCount);
            Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DialogService.Instance = prevDialog;
        }
    }

    // ---------- progress feedback (regression) ----------
    // PrivacyView.xaml binds a progress bar to IsBusy and the sidebar spinner reads the same flag,
    // but this VM never assigned it — so reading every privacy registry key, which the VM's own
    // comment says has to happen off the UI thread, produced no feedback at all.

    [Fact]
    public async Task AfterConstruction_TheBusyFlagIsClear()
    {
        var vm = new PrivacyViewModel(new PrivacyService(), NoRestorePoint(), new PrivacyChoicesHandoff());

        await vm.InitializationComplete;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public async Task Refresh_RaisesIsBusyThenClearsIt()
    {
        var vm = NewVm();

        var seen = vm.RecordChangesOf(nameof(vm.IsBusy), () => vm.IsBusy);

        await vm.RefreshCommand.ExecuteAsync(null);

        // Observed through the change notifications: the registry read finishes too fast to sample
        // mid-flight, but the flag must still have gone up and then back down.
        Assert.Equal([true, false], seen);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Refresh_UsesAMarqueeBar()
    {
        // Reading N registry keys reports no meaningful percentage, so a determinate bar stuck at 0
        // would read as "stalled".
        var vm = NewVm();

        var seen = vm.RecordChangesOf(nameof(vm.IsProgressIndeterminate), () => vm.IsProgressIndeterminate);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([true, false], seen);
    }

    // ---------- privacy choices imported from a profile (#1530) ----------
    // A profile never writes the registry. Its choices arrive through the handoff and are staged the way a
    // click is, so the switches move, the pending count says how many, and only Apply writes. These run over
    // FakePrivacy, so which switches move does not depend on this PC's own privacy settings.

    /// <summary>The tab over chosen toggles and a given handoff, its first load settled.</summary>
    private static async Task<PrivacyViewModel> StagingVmAsync(
        IPrivacyService privacy, IPrivacyChoicesHandoff handoff, ISessionRestorePoint? restorePoint = null)
    {
        var vm = new PrivacyViewModel(privacy, restorePoint ?? NoRestorePoint(), handoff);
        await vm.InitializationComplete;
        return vm;
    }

    /// <summary>One protection per case that matters: off, on and machine-wide, and off in a second category.</summary>
    private static IPrivacyService ThisPc() => FakePrivacy.Returning(
        FakePrivacy.Toggle("tips", on: false),
        FakePrivacy.Toggle("widgets", on: true, hive: "HKLM", category: "Features"),
        FakePrivacy.Toggle("web-search", on: false, category: "Features"));

    private static PrivacyChoices Choices(params (string Key, bool On)[] choices) =>
        new() { Protections = choices.ToDictionary(c => c.Key, c => c.On, StringComparer.Ordinal) };

    private static PrivacyToggle Switch(PrivacyViewModel vm, string key) => vm.Toggles.Single(t => t.Key == key);

    [Fact]
    public async Task ImportedChoices_LeftBeforeTheTabWasBuilt_AreStagedWhenItsTogglesLoad()
    {
        // The import opens this tab, which may be its first visit: the view model is built after the offer.
        var handoff = new PrivacyChoicesHandoff();
        handoff.Offer(Choices(("tips", true), ("widgets", true), ("web-search", false)));

        var vm = await StagingVmAsync(ThisPc(), handoff);

        Assert.True(Switch(vm, "tips").IsEnabled);
        Assert.Equal(1, vm.PendingChangeCount);   // widgets and web-search were already as the profile has them
        Assert.True(vm.HasPendingChanges);
        Assert.Null(handoff.Take());              // taken, not left for the next visit
    }

    [Fact]
    public async Task ImportedChoices_LeftAfterTheTabLoaded_AreStagedWhenItIsShown()
    {
        var handoff = new PrivacyChoicesHandoff();
        var vm = await StagingVmAsync(ThisPc(), handoff);
        handoff.Offer(Choices(("tips", true)));
        Assert.Equal(0, vm.PendingChangeCount);

        vm.IsActive = true;

        Assert.True(Switch(vm, "tips").IsEnabled);
        Assert.Equal(1, vm.PendingChangeCount);
    }

    [Fact]
    public async Task ImportedChoices_WriteNothingUntilApply_AndApplyWritesThem()
    {
        var privacy = ThisPc();
        var handoff = new PrivacyChoicesHandoff();
        handoff.Offer(Choices(("tips", true)));
        var vm = await StagingVmAsync(privacy, handoff);

        privacy.DidNotReceiveWithAnyArgs().ApplyAll(default!);
        privacy.DidNotReceiveWithAnyArgs().ApplyToggle(default!);

        // Through the same confirmation as a click, and only the switch the profile moved.
        List<(string Key, bool On)> written = [];
        privacy.When(p => p.ApplyAll(Arg.Any<IEnumerable<PrivacyToggle>>()))
            .Do(call => written.AddRange(call.Arg<IEnumerable<PrivacyToggle>>().Select(t => (t.Key, t.IsEnabled))));
        using var dialog = new DialogAnswer(confirm: true);
        await vm.ApplyChangesCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.Equal([("tips", true)], written);
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public async Task ImportedChoices_AreStagedOnce()
    {
        // Taken when staged, so coming back to the tab after Discard does not put them back over that decision.
        var handoff = new PrivacyChoicesHandoff();
        handoff.Offer(Choices(("tips", true)));
        var vm = await StagingVmAsync(ThisPc(), handoff);
        vm.DiscardChangesCommand.Execute(null);

        vm.IsActive = false;
        vm.IsActive = true;

        Assert.False(Switch(vm, "tips").IsEnabled);
        Assert.Equal(0, vm.PendingChangeCount);
    }

    [Fact]
    public async Task ImportedChoices_LeaveTheSwitchesTheProfileDoesNotName()
    {
        var handoff = new PrivacyChoicesHandoff();
        var vm = await StagingVmAsync(ThisPc(), handoff);
        Switch(vm, "web-search").IsEnabled = true;   // the user's own click, not applied yet

        handoff.Offer(Choices(("tips", true)));
        vm.IsActive = true;

        Assert.True(Switch(vm, "web-search").IsEnabled);
        Assert.True(Switch(vm, "tips").IsEnabled);
        Assert.Equal(2, vm.PendingChangeCount);
    }

    [Fact]
    public async Task ImportedChoices_ShowEveryCategory()
    {
        // A switch the profile moved under a filtered-out category would be a pending change nobody can see.
        var handoff = new PrivacyChoicesHandoff();
        var vm = await StagingVmAsync(ThisPc(), handoff);
        vm.SelectedCategory = "Telemetry";
        Assert.Single(vm.FilteredToggles);

        handoff.Offer(Choices(("web-search", true)));
        vm.IsActive = true;

        Assert.Equal("All", vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredToggles.Count);
    }

    [Fact]
    public async Task ImportedChoices_ThatMatchThisPc_SayThereIsNothingToChange()
    {
        var handoff = new PrivacyChoicesHandoff();
        handoff.Offer(Choices(("tips", false), ("widgets", true)));

        var vm = await StagingVmAsync(ThisPc(), handoff);

        Assert.Equal(0, vm.PendingChangeCount);
        Assert.Equal("The imported privacy choices already match this PC, so there is nothing to change.", vm.StatusMessage);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ImportedChoices_ThatNeedAdmin_AreFlaggedOnlyWhenNotElevated(bool elevated, bool flagged)
    {
        // widgets is machine-wide: without elevation Apply cannot write it, and the staged switch does not survive
        // the restart into administrator mode, so the tab says so before Apply rather than after.
        using var probe = AdminHelper.ForceElevation(elevated);
        var handoff = new PrivacyChoicesHandoff();
        handoff.Offer(Choices(("tips", true), ("widgets", false)));

        var vm = await StagingVmAsync(ThisPc(), handoff);

        Assert.Equal(2, vm.PendingChangeCount);
        Assert.Equal(flagged, vm.StatusMessage.Contains("administrator", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 0, false, "The imported profile changes 1 privacy setting on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is.")]
    [InlineData(3, 1, false, "The imported profile changes 3 privacy settings on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is. 1 of them needs administrator "
        + "rights. To apply that one too, run SysManager as administrator and import the profile again.")]
    [InlineData(3, 2, false, "The imported profile changes 3 privacy settings on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is. 2 of them need administrator "
        + "rights. To apply those too, run SysManager as administrator and import the profile again.")]
    [InlineData(1, 1, false, "The imported profile changes 1 privacy setting on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is. It needs administrator rights, "
        + "so run SysManager as administrator and import the profile again to apply it.")]
    [InlineData(2, 2, false, "The imported profile changes 2 privacy settings on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is. They all need administrator "
        + "rights, so run SysManager as administrator and import the profile again to apply them.")]
    [InlineData(2, 2, true, "The imported profile changes 2 privacy settings on this PC. Nothing has changed yet: "
        + "check the switches, then press Apply, or Discard to keep this PC as it is.")]
    public void DescribeImportedChoices_SaysWhatChangesAndWhatNeedsAdmin(
        int changes, int needAdmin, bool elevated, string expected)
        => Assert.Equal(expected, PrivacyViewModel.DescribeImportedChoices(changes, needAdmin, elevated));

    [Fact]
    public async Task ImportedChoices_ArrivingDuringARefresh_AreStagedOnTheTogglesItLoads()
    {
        // A load replaces every toggle, so staging onto the old ones would lose the import when it lands.
        using var secondLoad = new ManualResetEventSlim(initialState: false);
        var loads = 0;
        var privacy = Substitute.For<IPrivacyService>();
        privacy.LoadToggles().Returns(_ =>
        {
            if (Interlocked.Increment(ref loads) > 1)
                Assert.True(secondLoad.Wait(TimeSpan.FromSeconds(30)), "the refresh was never released");
            return [FakePrivacy.Toggle("tips", on: false)];
        });
        var handoff = new PrivacyChoicesHandoff();
        var vm = await StagingVmAsync(privacy, handoff);
        var before = Switch(vm, "tips");

        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        handoff.Offer(Choices(("tips", true)));
        vm.IsActive = true;
        Assert.False(before.IsEnabled);

        secondLoad.Set();
        await refresh;

        Assert.NotSame(before, Switch(vm, "tips"));
        Assert.True(Switch(vm, "tips").IsEnabled);
        Assert.Equal(1, vm.PendingChangeCount);
    }

    [Fact]
    public async Task ImportedChoices_ArrivingWhileApplyWaitsForItsRestorePoint_AreStagedAfterTheWrite()
    {
        // The restore point can take long enough to open Profile Export / Import and import a profile. Staged
        // then, the import would move a switch the apply was about to write and change what it wrote.
        var privacy = ThisPc();
        List<(string Key, bool On)> written = [];
        privacy.When(p => p.ApplyAll(Arg.Any<IEnumerable<PrivacyToggle>>()))
            .Do(call => written.AddRange(call.Arg<IEnumerable<PrivacyToggle>>().Select(t => (t.Key, t.IsEnabled))));
        var restoring = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var restorePoint = Substitute.For<ISessionRestorePoint>();
        restorePoint.EnsureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(restoring.Task);
        var handoff = new PrivacyChoicesHandoff();
        using var dialog = new DialogAnswer(confirm: true);
        var vm = await StagingVmAsync(privacy, handoff, restorePoint);
        vm.IsActive = true;
        Switch(vm, "tips").IsEnabled = true;

        var apply = vm.ApplyChangesCommand.ExecuteAsync(null);
        handoff.Offer(Choices(("tips", false), ("web-search", true)));
        vm.IsActive = false;
        vm.IsActive = true;
        Assert.True(Switch(vm, "tips").IsEnabled);
        Assert.False(Switch(vm, "web-search").IsEnabled);

        restoring.SetResult(false);
        await apply;

        Assert.Equal([("tips", true)], written);              // what the user confirmed
        Assert.False(Switch(vm, "tips").IsEnabled);           // then the import, staged on top of the write
        Assert.True(Switch(vm, "web-search").IsEnabled);
        Assert.Equal(2, vm.PendingChangeCount);
    }

    [Fact]
    public async Task TheShellMarksTheTabAsShown()
    {
        // The wiring: MainWindowViewModel.SetActive is what tells a tab it is on screen, and so what stages an
        // import that arrived while another tab was showing.
        var handoff = new PrivacyChoicesHandoff();
        var vm = await StagingVmAsync(ThisPc(), handoff);
        handoff.Offer(Choices(("tips", true)));

        MainWindowViewModel.SetActive(vm, active: true);

        Assert.True(vm.IsActive);
        Assert.True(Switch(vm, "tips").IsEnabled);

        MainWindowViewModel.SetActive(vm, active: false);
        Assert.False(vm.IsActive);
    }
}
