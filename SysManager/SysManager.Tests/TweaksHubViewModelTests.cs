// SysManager · TweaksHubViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the confirm-gate tests swap the static DialogService.Instance.
[Collection("DialogService")]
public class TweaksHubViewModelTests
{
    private static TweakItem Tweak(string name, string hive, bool applied)
    {
        var toggle = new PrivacyToggle
        {
            Name = name, Description = "d", Category = "c",
            RegistryPath = $@"{hive}\Software\Test\{name}", ValueName = "v",
            EnabledValue = 1, DisabledValue = 0, IsEnabled = applied,
        };
        return TweakItem.From(toggle);
    }

    private static ITweaksHubService NewService(params TweakItem[] tweaks)
    {
        var svc = Substitute.For<ITweaksHubService>();
        svc.LoadTweaks().Returns(tweaks);
        svc.ApplyAsync(Arg.Any<IReadOnlyList<TweakItem>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TweakApplyResult([], false)));
        return svc;
    }

    // ── Apply confirm gate ─────────────────────────────────────────────────

    [Fact]
    public void ApplySelected_WhenUserDeclines_DoesNotApply()
    {
        var item = Tweak("a", "HKCU", applied: false);
        item.IsSelected = true;
        var svc = NewService(item);
        var vm = new TweaksHubViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplySelectedCommand.Execute(null);
            dialog.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>());
            svc.DidNotReceive().ApplyAsync(Arg.Any<IReadOnlyList<TweakItem>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { DialogService.Instance = prev; }
    }

    [Fact]
    public void ApplySelected_WhenConfirmed_AppliesOnlySelectedNotYetApplied()
    {
        var sel = Tweak("a", "HKCU", applied: false); sel.IsSelected = true;
        var already = Tweak("b", "HKCU", applied: true); already.IsSelected = true; // applied → not pending-apply
        var unsel = Tweak("c", "HKCU", applied: false); // not selected
        var svc = NewService(sel, already, unsel);
        var vm = new TweaksHubViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplySelectedCommand.Execute(null);
            // Only the selected, not-yet-applied item is sent to enable=true.
            svc.Received(1).ApplyAsync(
                Arg.Is<IReadOnlyList<TweakItem>>(l => l.Count == 1 && l[0] == sel),
                true, Arg.Any<CancellationToken>());
        }
        finally { DialogService.Instance = prev; }
    }

    // ── Undo confirm gate ──────────────────────────────────────────────────

    [Fact]
    public void UndoSelected_WhenUserDeclines_DoesNotUndo()
    {
        var item = Tweak("a", "HKCU", applied: true); item.IsSelected = true;
        var svc = NewService(item);
        var vm = new TweaksHubViewModel(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        DialogService.Instance = dialog;
        try
        {
            vm.UndoSelectedCommand.Execute(null);
            svc.DidNotReceive().ApplyAsync(Arg.Any<IReadOnlyList<TweakItem>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        }
        finally { DialogService.Instance = prev; }
    }

    // ── CanExecute tracks pending counts ───────────────────────────────────

    [Fact]
    public void ApplyCommand_CanExecute_FalseWhenNothingPending()
    {
        // An applied + selected item is pending-UNDO, not pending-APPLY.
        var item = Tweak("a", "HKCU", applied: true); item.IsSelected = true;
        var vm = new TweaksHubViewModel(NewService(item));
        Assert.False(vm.ApplySelectedCommand.CanExecute(null));
        Assert.True(vm.UndoSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void SelectingItem_UpdatesPendingApply_AndEnablesCommand()
    {
        var item = Tweak("a", "HKCU", applied: false);
        var vm = new TweaksHubViewModel(NewService(item));
        Assert.Equal(0, vm.PendingApply);
        Assert.False(vm.ApplySelectedCommand.CanExecute(null));

        item.IsSelected = true; // fires PropertyChanged → RecountPending

        Assert.Equal(1, vm.PendingApply);
        Assert.True(vm.ApplySelectedCommand.CanExecute(null));
    }

    [Fact]
    public void Load_ClassifiesIntoEssentialAndAdvancedByHive()
    {
        var hkcu = Tweak("a", "HKCU", applied: false);
        var hklm = Tweak("b", "HKLM", applied: false);
        var vm = new TweaksHubViewModel(NewService(hkcu, hklm));
        Assert.Single(vm.Essential);
        Assert.Single(vm.Advanced);
    }
}
