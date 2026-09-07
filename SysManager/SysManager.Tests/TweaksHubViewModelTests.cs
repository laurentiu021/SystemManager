// SysManager · TweaksHubViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the confirm-gate tests swap the static DialogService.Instance.
[Collection("ProcessWideStatics")]
public class TweaksHubViewModelTests
{
    private static TweakItem Tweak(string name, string hive, bool applied)
    {
        var toggle = new PrivacyToggle
        {
            Name = name,
            Description = "d",
            Category = "c",
            RegistryPath = $@"{hive}\Software\Test\{name}",
            ValueName = "v",
            EnabledValue = 1,
            DisabledValue = 0,
            IsEnabled = applied,
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

    // Construct the VM and wait for its fire-and-forget async load to finish, so the tweak lists
    // are populated deterministically before a test observes them (the tab now loads off the UI
    // thread). Mirrors AppBlockerViewModelTests / AudioMixerViewModelTests.
    private static TweaksHubViewModel NewVm(ITweaksHubService service)
    {
        var vm = new TweaksHubViewModel(service);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    // ── Apply confirm gate ─────────────────────────────────────────────────

    [Fact]
    public void ApplySelected_WhenUserDeclines_DoesNotApply()
    {
        var item = Tweak("a", "HKCU", applied: false);
        item.IsSelected = true;
        var svc = NewService(item);
        var vm = NewVm(svc);

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
        var vm = NewVm(svc);

        var prev = DialogService.Instance;
        var dialog = Substitute.For<IDialogService>();
        dialog.Confirm(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        DialogService.Instance = dialog;
        try
        {
            vm.ApplySelectedCommand.Execute(null);
            // Only the selected, not-yet-applied item is sent to enable=true.
            svc.Received(1).ApplyAsync(
                Arg.Is<IReadOnlyList<TweakItem>>(l => l != null && l.Count == 1 && l[0] == sel),
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
        var vm = NewVm(svc);

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
        var vm = NewVm(NewService(item));
        Assert.False(vm.ApplySelectedCommand.CanExecute(null));
        Assert.True(vm.UndoSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void SelectingItem_UpdatesPendingApply_AndEnablesCommand()
    {
        var item = Tweak("a", "HKCU", applied: false);
        var vm = NewVm(NewService(item));
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
        var vm = NewVm(NewService(hkcu, hklm));
        Assert.Single(vm.Essential);
        Assert.Single(vm.Advanced);
    }

    /// <summary>
    /// The REAL tweak catalogue fills both tiers, which is what makes this tab's empty state unreachable.
    /// </summary>
    /// <remarks>
    /// This is the verification behind an exemption rather than a behaviour test.
    /// <c>ArchitectureTests.EveryViewThatListsACollection_HasSomethingToSayWhenItIsEmpty</c> excuses
    /// <c>TweaksHubView</c> from needing empty-state copy, on the grounds that
    /// <c>LoadTweaks()</c> is the static privacy toggles partitioned by <c>ClassifyTier</c> and both sides
    /// are non-empty by construction. An exemption nobody checked is a false claim sitting in the test
    /// suite, so it is checked here — against the real definitions, not a substitute, because a substitute
    /// would be asserting the fixture.
    /// <para>Straight through <c>PrivacyService</c> and <c>TweakItem.ClassifyTier</c> rather than through the
    /// view-model: the claim is about the DATA, and building the view-model would drag in a registry read
    /// per toggle for no extra coverage.</para>
    /// </remarks>
    [Fact]
    public void TheRealTweakCatalogue_FillsBothTiers()
    {
        var toggles = new PrivacyService().LoadToggles();
        Assert.True(toggles.Count >= 10,
            $"only {toggles.Count} toggle definitions — the catalogue lookup is wrong and this would pass "
            + "vacuously");

        var tiers = toggles.Select(t => TweakItem.ClassifyTier(t.RegistryPath)).ToList();

        Assert.Contains(TweakTier.Essential, tiers);
        Assert.Contains(TweakTier.Advanced, tiers);
    }
}
