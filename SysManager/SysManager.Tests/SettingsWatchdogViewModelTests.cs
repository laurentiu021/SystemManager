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
}
