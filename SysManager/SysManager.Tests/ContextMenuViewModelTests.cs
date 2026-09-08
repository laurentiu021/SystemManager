// SysManager · ContextMenuViewModelTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using NSubstitute;
using SysManager.Helpers;
using SysManager.Models;
using SysManager.Services;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests for <see cref="ContextMenuViewModel"/>. Verifies constructor defaults,
/// filter state, counter consistency, and command wiring. The VM auto-scans the
/// registry on construction; <see cref="NewVm"/> awaits <see cref="ViewModelBase.InitializationComplete"/>
/// so assertions observe a settled state deterministically (no race with the scan).
/// </summary>
// Serialized: the toggle-failure tests pin elevation with AdminHelper.ForceElevation and swap
// DialogService.Instance, both process-wide. Required by
// ArchitectureTests.ProcessWideStaticUsers_AreInTheSerializedCollection.
[Collection("ProcessWideStatics")]
public class ContextMenuViewModelTests
{
    private static ContextMenuViewModel NewVm()
    {
        var vm = new ContextMenuViewModel(new ContextMenuService());
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void Constructor_LocationFilters_ContainsAllPlusSpecific()
    {
        var vm = NewVm();
        Assert.Contains("All", vm.LocationFilters);
        Assert.Contains("Files", vm.LocationFilters);
        Assert.Contains("Folders", vm.LocationFilters);
        Assert.Contains("Directory Background", vm.LocationFilters);
        Assert.Contains("Desktop", vm.LocationFilters);
        Assert.Equal(5, vm.LocationFilters.Count);
    }

    [Fact]
    public void Constructor_SelectedLocation_DefaultsToAll()
    {
        var vm = NewVm();
        Assert.Equal("All", vm.SelectedLocation);
    }

    [Fact]
    public void Constructor_FilterText_DefaultsEmpty()
    {
        var vm = NewVm();
        Assert.Equal("", vm.FilterText);
    }

    [Fact]
    public void AfterInit_Entries_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.Entries);
    }

    [Fact]
    public void AfterInit_TotalCount_MatchesEntries()
    {
        var vm = NewVm();
        Assert.Equal(vm.Entries.Count, vm.TotalCount);
    }

    [Fact]
    public void AfterInit_EnabledPlusDisabled_EqualsTotalCount()
    {
        var vm = NewVm();
        Assert.Equal(vm.TotalCount, vm.EnabledCount + vm.DisabledCount);
    }

    [Fact]
    public void AfterInit_IsBusy_IsFalse()
    {
        var vm = NewVm();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Commands_NotNull()
    {
        var vm = NewVm();
        Assert.NotNull(vm.ScanCommand);
        Assert.NotNull(vm.ToggleEntryCommand);
        Assert.NotNull(vm.RefreshCommand);
        Assert.NotNull(vm.ApplyPresetCommand);
    }

    [Fact]
    public void Constructor_ActivePresetId_DefaultsToMenuStyle()
    {
        var vm = NewVm();
        Assert.Contains(vm.ActivePresetId, new[] { "win10", "win11" });
    }

    // ---------- re-entrancy guard (regression: overlapping scan/preset runs) ----------

    [Fact]
    public void LongRunningCommands_DisabledWhileBusy()
    {
        // Scan, Refresh and ApplyPreset all mutate the shared _allEntries list off the UI
        // thread (ApplyPreset also restarts Explorer). The NotBusy gate disables them while
        // one runs so overlapping runs can't corrupt that list or race two restarts.
        // Drive IsBusy explicitly rather than asserting the post-construction baseline:
        // the constructor kicks off an async scan that briefly sets IsBusy itself.
        var vm = NewVm();

        vm.IsBusy = true;
        Assert.False(vm.ScanCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.ApplyPresetCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.ScanCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.ApplyPresetCommand.CanExecute(null));
    }

    // ── Toggling an entry ────────────────────────────────────────────────────
    //
    // None of this was assertable until IContextMenuService existed (#2180): a test could neither make a
    // toggle fail on purpose nor let it succeed, because a real toggle writes a shell key on the machine
    // running the suite — and on an elevated CI runner that write goes through. The substitute decides the
    // outcome, so nothing here reaches the registry.
    //
    // The tests above deliberately keep the REAL service: they assert state derived from an actual scan,
    // which a substitute returning nothing would make vacuous.

    /// <summary>A view model whose toggles answer <paramref name="toggleSucceeds"/> and touch nothing.</summary>
    private static ContextMenuViewModel NewVm(bool toggleSucceeds, out IContextMenuService service)
    {
        service = Substitute.For<IContextMenuService>();
        service.ScanEntries().Returns([]);
        service.EnableEntry(Arg.Any<ContextMenuEntry>()).Returns(toggleSucceeds);
        service.DisableEntry(Arg.Any<ContextMenuEntry>()).Returns(toggleSucceeds);

        var vm = new ContextMenuViewModel(service);
        vm.InitializationComplete.GetAwaiter().GetResult();
        return vm;
    }

    private static ContextMenuEntry NewEntry(bool enabled) => new()
    {
        Name = "Open with Example",
        Command = @"C:\Program Files\Example\app.exe",
        RegistryPath = @"HKCR\*\shell\Example",
        Location = "Files",
        IsEnabled = enabled,
    };

    /// <summary>
    /// A failed toggle WITHOUT admin rights offers to restart elevated, and says why.
    /// </summary>
    [Fact]
    public async Task ToggleEntry_WhenItFailsAndNotElevated_OffersToRestartAsAdministrator()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        var vm = NewVm(toggleSucceeds: false, out _);
        var entry = NewEntry(enabled: false);   // the user just clicked it off

        using var dialog = new DialogAnswer(false);   // declined, so nothing relaunches
        await vm.ToggleEntryCommand.ExecuteAsync(entry);

        Assert.Equal(1, dialog.Calls);
        Assert.Contains(dialog.Messages, m => m.Contains("administrator", StringComparison.OrdinalIgnoreCase));
        Assert.True(entry.IsEnabled, "a failed toggle must put the switch back where it was");
    }

    /// <summary>
    /// A failed toggle WITH admin rights says the entry is Windows-owned, and does not offer a restart.
    /// </summary>
    /// <remarks>
    /// This is the branch worth having. Elevating again cannot help against a TrustedInstaller-owned key,
    /// so offering it would send the user round a loop that ends where it started — and getting the two
    /// messages the wrong way round would do exactly that for every user, with nothing failing.
    /// </remarks>
    [Fact]
    public async Task ToggleEntry_WhenItFailsWhileElevated_SaysWindowsOwnsItAndOffersNoRestart()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        var vm = NewVm(toggleSucceeds: false, out _);
        var entry = NewEntry(enabled: false);

        using var dialog = new DialogAnswer(true);   // would say yes if it were ever asked
        await vm.ToggleEntryCommand.ExecuteAsync(entry);

        Assert.Equal(0, dialog.Calls);
        Assert.Contains("TrustedInstaller", vm.StatusMessage);
        Assert.True(entry.IsEnabled, "a failed toggle must put the switch back where it was");
    }

    [Theory]
    [InlineData(true, "enabled")]
    [InlineData(false, "disabled")]
    public async Task ToggleEntry_WhenItSucceeds_ReportsItAndDropsThePresetToCustom(
        bool desiredState, string expectedWord)
    {
        var vm = NewVm(toggleSucceeds: true, out var service);
        var entry = NewEntry(enabled: desiredState);

        using var dialog = new DialogAnswer(false);
        await vm.ToggleEntryCommand.ExecuteAsync(entry);

        Assert.Equal(0, dialog.Calls);
        Assert.Equal("custom", vm.ActivePresetId);
        Assert.Contains(expectedWord, vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(desiredState, entry.IsEnabled);

        // The direction matters: the command reads the switch's NEW position and asks the service to
        // match it, so an inverted call would disable what the user just turned on.
        if (desiredState) service.Received(1).EnableEntry(entry);
        else service.Received(1).DisableEntry(entry);
    }

    [Fact]
    public async Task ToggleEntry_WithSomethingThatIsNotAnEntry_DoesNothing()
    {
        var vm = NewVm(toggleSucceeds: false, out var service);

        using var dialog = new DialogAnswer(true);
        await vm.ToggleEntryCommand.ExecuteAsync("not an entry");

        Assert.Equal(0, dialog.Calls);
        service.DidNotReceive().EnableEntry(Arg.Any<ContextMenuEntry>());
        service.DidNotReceive().DisableEntry(Arg.Any<ContextMenuEntry>());
    }
}
