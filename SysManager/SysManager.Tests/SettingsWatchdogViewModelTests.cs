// SysManager · SettingsWatchdogViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the confirm-gate tests swap the static DialogService.Instance.
[Collection("DialogService")]
public class SettingsWatchdogViewModelTests
{
    private static WatchedSetting Setting(string key) => new(
        key, $"Name {key}", "desc", "Privacy", $@"HKLM\SOFTWARE\Test\{key}", "Val",
        new Dictionary<int, string> { [0] = "Off", [1] = "On" });

    private static SettingDrift Drift(string key, bool canRestore = true) =>
        new(Setting(key), BaselineValue: 0, CurrentValue: 1, CanRestore: canRestore);

    private static ISettingsWatchdogService NewService(params SettingDrift[] drifts)
    {
        var svc = Substitute.For<ISettingsWatchdogService>();
        svc.Catalog.Returns([]);
        svc.LoadBaseline().Returns(new BaselineSnapshot(new DateTime(2026, 1, 1), []));
        svc.HasBaseline.Returns(true);
        svc.DetectDrift().Returns(drifts);
        return svc;
    }

    // ── SaveBaseline confirm gate ──────────────────────────────────────────

    [Fact]
    public void SaveBaseline_WhenBaselineExists_AndUserDeclines_DoesNotSave()
    {
        var svc = NewService();
        var vm = new SettingsWatchdogViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false); // user clicks No
        DialogService.Instance = dialog;
        try
        {
            vm.SaveBaselineCommand.Execute(null);
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            svc.DidNotReceive().SaveBaseline(Arg.Any<DateTime>());
        }
        finally { DialogService.Instance = prev; }
    }

    [Fact]
    public void SaveBaseline_WhenConfirmed_Saves()
    {
        var svc = NewService();
        var vm = new SettingsWatchdogViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.SaveBaselineCommand.Execute(null);
            svc.Received(1).SaveBaseline(Arg.Any<DateTime>());
        }
        finally { DialogService.Instance = prev; }
    }

    [Fact]
    public void SaveBaseline_WhenNoBaseline_SkipsConfirm_AndSaves()
    {
        var svc = Substitute.For<ISettingsWatchdogService>();
        svc.Catalog.Returns([]);
        svc.LoadBaseline().Returns((BaselineSnapshot?)null);
        svc.HasBaseline.Returns(false);
        svc.DetectDrift().Returns([]);
        var vm = new SettingsWatchdogViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        DialogService.Instance = dialog;
        try
        {
            vm.SaveBaselineCommand.Execute(null);
            // First-time save shouldn't prompt to overwrite.
            dialog.DidNotReceive().Confirm(Arg.Any<string>(), Arg.Any<string>());
            svc.Received(1).SaveBaseline(Arg.Any<DateTime>());
        }
        finally { DialogService.Instance = prev; }
    }

    // ── RestoreSelected confirm gate ───────────────────────────────────────

    [Fact]
    public void RestoreSelected_WhenUserDeclines_DoesNotRestore()
    {
        var svc = NewService(Drift("a"));
        var vm = new SettingsWatchdogViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.RestoreSelectedCommand.Execute(null);
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            svc.DidNotReceive().Restore(Arg.Any<SettingDrift>());
        }
        finally { DialogService.Instance = prev; }
    }

    [Fact]
    public void RestoreSelected_WhenConfirmed_RestoresEachRestorableDrift()
    {
        var svc = NewService(Drift("a"), Drift("b"));
        svc.Restore(Arg.Any<SettingDrift>()).Returns(true);
        var vm = new SettingsWatchdogViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.RestoreSelectedCommand.Execute(null);
            svc.Received(2).Restore(Arg.Any<SettingDrift>());
        }
        finally { DialogService.Instance = prev; }
    }

    [Fact]
    public void RestoreSelected_CanExecute_FalseWhenNoRestorableDrift()
    {
        // A drift that can't be restored must not enable the command.
        var svc = NewService(Drift("a", canRestore: false));
        var vm = new SettingsWatchdogViewModel(svc);
        Assert.False(vm.RestoreSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Refresh_NoBaseline_SetsHasBaselineFalse()
    {
        var svc = Substitute.For<ISettingsWatchdogService>();
        svc.Catalog.Returns([]);
        svc.LoadBaseline().Returns((BaselineSnapshot?)null);
        svc.DetectDrift().Returns([]);
        var vm = new SettingsWatchdogViewModel(svc);
        Assert.False(vm.HasBaseline);
        Assert.False(vm.HasDrift);
    }

    // ── "What is being watched" ──────────────────────────────────────────────────────────────────
    //
    // `Watched` was populated in the constructor and bound by nothing, so the tab showed only settings
    // that had ALREADY drifted; before a drift the page held the intro sentence and an empty state.
    // A watchdog that will not say what it watches is asking for trust it has not earned.
    // RowMarkBindingTests' sibling in this batch asserts the binding half; these cover the data.

    private static ISettingsWatchdogService WithCatalog(
        IReadOnlyDictionary<string, int?> current, params SettingDrift[] drifts)
    {
        var svc = Substitute.For<ISettingsWatchdogService>();
        svc.Catalog.Returns([Setting("telemetry"), Setting("widgets"), Setting("web-search")]);
        svc.LoadBaseline().Returns(new BaselineSnapshot(new DateTime(2026, 1, 1), []));
        svc.HasBaseline.Returns(true);
        svc.DetectDrift().Returns(drifts);
        svc.ReadCurrent().Returns(current);
        return svc;
    }

    [Fact]
    public void TheWatchedList_ShowsEveryCatalogEntry_EvenWithNoDrift()
    {
        // The case that was invisible: nothing has drifted, so the drift list is empty — and that is
        // precisely when the user most needs to see what is covered.
        var vm = new SettingsWatchdogViewModel(
            WithCatalog(new Dictionary<string, int?> { ["telemetry"] = 0, ["widgets"] = 1, ["web-search"] = null }));

        Assert.False(vm.HasDrift);
        Assert.Empty(vm.Drifts);
        Assert.Equal(3, vm.Watched.Count);
        Assert.Equal(["Name telemetry", "Name widgets", "Name web-search"], vm.Watched.Select(w => w.Name));
    }

    [Fact]
    public void TheWatchedList_RendersEachValueInPlainLanguage()
    {
        // Same wording as the drift list, via WatchedSetting.Describe — a bare number would tell the
        // target user nothing, and "0" beside "telemetry" is actively misleading (it means Off, not
        // absent).
        var vm = new SettingsWatchdogViewModel(
            WithCatalog(new Dictionary<string, int?> { ["telemetry"] = 0, ["widgets"] = 1, ["web-search"] = null }));

        Assert.Equal("Off", vm.Watched.Single(w => w.Setting.Key == "telemetry").CurrentLabel);
        Assert.Equal("On", vm.Watched.Single(w => w.Setting.Key == "widgets").CurrentLabel);
        // Absent from the registry reads as "Not set", not as a blank cell or a zero.
        Assert.Equal("Not set", vm.Watched.Single(w => w.Setting.Key == "web-search").CurrentLabel);
    }

    [Fact]
    public void ASettingMissingFromTheCurrentRead_ReadsAsNotSet()
    {
        // ReadCurrent returns one entry per catalog key, but a key it could not read must degrade to
        // "Not set" rather than throwing — the list has to render on a machine where a policy key is
        // simply absent, which is the common case for several of these.
        var vm = new SettingsWatchdogViewModel(WithCatalog(new Dictionary<string, int?>()));

        Assert.Equal(3, vm.Watched.Count);
        Assert.All(vm.Watched, w => Assert.Equal("Not set", w.CurrentLabel));
    }

    [Fact]
    public void ADriftedSetting_IsMarkedInTheWatchedList_SoTheTwoListsCannotDisagree()
    {
        // Both lists describe the same settings. If one said a setting was settled while the other
        // flagged it as changed, the page would contradict itself — the defect class already fixed in
        // Tune-Up, where a "1 recommendation" headline sat directly above a row reading "Healthy".
        var vm = new SettingsWatchdogViewModel(
            WithCatalog(new Dictionary<string, int?> { ["telemetry"] = 1, ["widgets"] = 1, ["web-search"] = 1 },
                        Drift("widgets")));

        Assert.True(vm.HasDrift);
        Assert.True(vm.Watched.Single(w => w.Setting.Key == "widgets").HasDrifted);
        Assert.False(vm.Watched.Single(w => w.Setting.Key == "telemetry").HasDrifted);
        Assert.False(vm.Watched.Single(w => w.Setting.Key == "web-search").HasDrifted);
    }

    [Fact]
    public void TheWatchedList_IsRebuiltOnRefresh_SoItsValuesAreNeverStale()
    {
        // Rebuilt per refresh rather than once in the constructor. Showing a watched list carrying the
        // values from app start would be its own quiet lie — the user presses Refresh precisely to find
        // out what the machine looks like NOW.
        var svc = WithCatalog(new Dictionary<string, int?> { ["telemetry"] = 0, ["widgets"] = 0, ["web-search"] = 0 });
        var vm = new SettingsWatchdogViewModel(svc);
        Assert.Equal("Off", vm.Watched.Single(w => w.Setting.Key == "telemetry").CurrentLabel);

        svc.ReadCurrent().Returns(new Dictionary<string, int?> { ["telemetry"] = 1, ["widgets"] = 0, ["web-search"] = 0 });
        vm.RefreshCommand.Execute(null);

        Assert.Equal("On", vm.Watched.Single(w => w.Setting.Key == "telemetry").CurrentLabel);
    }

    [Fact]
    public void TheWatchedList_ExposesTheRegistryLocation()
    {
        // Shown as a tooltip, not a column: the target user does not read registry paths, but anyone who
        // wants to verify what the app claims to watch should not have to read the source to do it.
        var vm = new SettingsWatchdogViewModel(WithCatalog(new Dictionary<string, int?>()));

        Assert.Equal(@"HKLM\SOFTWARE\Test\telemetry\Val",
            vm.Watched.Single(w => w.Setting.Key == "telemetry").Location);
    }
}
