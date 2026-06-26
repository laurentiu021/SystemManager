// SysManager · PrivacyViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="PrivacyViewModel"/>. Verifies toggle population,
/// category filtering, pending-change tracking, and discard behavior
/// without writing to the registry.
/// </summary>
[Collection("DialogService")]
public class PrivacyViewModelTests
{
    // The VM loads its toggles asynchronously off the UI thread (so startup isn't blocked);
    // wait for that init to finish before asserting loaded state, so the tests observe the
    // populated collections deterministically instead of racing the background load.
    private static PrivacyViewModel NewVm()
    {
        var vm = new PrivacyViewModel(new PrivacyService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
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
    public void ApplyChanges_WithNoPending_SetsNoChangesMessage()
    {
        var vm = NewVm();
        vm.ApplyChangesCommand.Execute(null);

        Assert.Contains("no changes", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyChanges_WhenUserDeclinesConfirm_DoesNotApply_AndKeepsPending()
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

            vm.ApplyChangesCommand.Execute(null);

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
}
