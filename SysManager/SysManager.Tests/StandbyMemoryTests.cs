// SysManager · StandbyMemoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

// Serialized: the purge gate tests replace AdminHelper.ElevationProbe, which is process-wide state.
[Collection("ProcessWideStatics")]
public class StandbyMemoryTests
{
    /// <summary>
    /// Auto-purge fires on low free memory with a standby list worth purging, and not on low available memory (#2768).
    /// </summary>
    /// <remarks>
    /// It compared the threshold with available memory, the free and standby lists together, which a purge does not
    /// raise: under memory pressure it purged every 2 seconds and freed next to nothing, and with a large standby list
    /// and little free memory, the case it exists for, available memory was high and it never fired.
    /// </remarks>
    [Theory]
    [InlineData(300, 6000, 1024, true)]     // little free memory, a large standby list → purge
    [InlineData(1023, 1024, 1024, true)]    // just below, and just enough standby → purge
    [InlineData(1024, 6000, 1024, false)]   // free memory at the threshold → no purge
    [InlineData(4000, 6000, 1024, false)]   // plenty free → no purge
    [InlineData(300, 1023, 1024, false)]    // a standby list too small to be worth it, as after a purge → no purge
    [InlineData(300, 0, 1024, false)]
    public void ShouldAutoPurge_FiresOnLowFreeMemory_WithAStandbyListWorthPurging(
        double freeMb, double standbyMb, double thresholdMb, bool expected)
        => Assert.Equal(expected, StandbyMemoryViewModel.ShouldAutoPurge(freeMb, standbyMb, thresholdMb, sinceLastPurge: null));

    [Fact]
    public void ShouldAutoPurge_GuardsAnUnknownReadingAndNoThreshold()
    {
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(null, 6000, 1024, null));   // free not reported
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(300, null, 1024, null));    // standby not reported
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(300, 6000, 0, null));       // no threshold
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(-1, 6000, 1024, null));
    }

    /// <summary>An automatic purge is not repeated until the cooldown has passed, whatever the memory says (#2768).</summary>
    [Fact]
    public void ShouldAutoPurge_WaitsOutTheCooldownAfterAPurge()
    {
        var cooldown = StandbyMemoryViewModel.AutoPurgeCooldown;

        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(300, 6000, 1024, cooldown - TimeSpan.FromSeconds(1)));
        Assert.True(StandbyMemoryViewModel.ShouldAutoPurge(300, 6000, 1024, cooldown));
    }

    /// <summary>
    /// The free and standby lists are read on this PC, and with the available memory they make up: what a purge moves
    /// between them is what auto-purge and the command line now look at (#2768).
    /// </summary>
    [Fact]
    public void GetMemoryStatus_ReadsTheFreeAndStandbyLists_WhichMakeUpAvailableMemory()
    {
        var status = new Services.StandbyMemoryService().GetMemoryStatus();

        Assert.True(status.FreeBytes is not null && status.StandbyBytes is not null,
            "Windows did not report the memory lists, so neither auto-purge nor the command line has its figures");
        // Read a moment apart from GlobalMemoryStatusEx, so they are close rather than equal.
        var lists = (double)status.FreeBytes!.Value + status.StandbyBytes!.Value;
        Assert.InRange(lists, status.AvailableBytes * 0.9, status.AvailableBytes * 1.1);
    }

    // ── When the 2-second poll may run ──────────────────────────────────────────────────────────────
    // The poll used to start in the constructor and never stop, so opening this tab ONCE left a
    // dispatcher tick firing every 2 seconds for the rest of the session — including while minimised or
    // closed to the tray, with an unsupervised privileged purge reachable from it. Visibility alone is
    // the wrong gate though: auto-purge is set-and-forget, so gating purely on IsActive would silently
    // stop watching free memory the moment the user navigated away.

    [Fact]
    public void ShouldPoll_WhileTheTabIsVisible()
        => Assert.True(StandbyMemoryViewModel.ShouldPoll(isActive: true, autoPurgeEnabled: false, isElevated: false));

    [Fact]
    public void ShouldNotPoll_WhenHiddenAndAutoPurgeIsOff()
        => Assert.False(StandbyMemoryViewModel.ShouldPoll(isActive: false, autoPurgeEnabled: false, isElevated: true));

    [Fact]
    public void ShouldPoll_WhenHiddenButAutoPurgeIsArmed()
    {
        // The whole point of auto-purge: the user arms it, navigates away, and it keeps watching free
        // memory. Gating this on visibility would turn the feature off without telling anyone — a worse
        // bug than the one being fixed.
        Assert.True(StandbyMemoryViewModel.ShouldPoll(isActive: false, autoPurgeEnabled: true, isElevated: true));
    }

    [Fact]
    public void ShouldNotPoll_WhenAutoPurgeIsArmedButPurgingIsImpossible()
    {
        // Armed without administrator: a purge can never happen, so a hidden tick could only re-read
        // memory into a tab nobody is looking at — the exact waste this gate removes.
        Assert.False(StandbyMemoryViewModel.ShouldPoll(isActive: false, autoPurgeEnabled: true, isElevated: false));
    }

    [Fact]
    public void ShouldPoll_WhenVisible_EvenWithoutElevation()
    {
        // The tab shows live memory figures to every user, so visibility is sufficient on its own — the
        // elevation condition must not leak into the visible case.
        Assert.True(StandbyMemoryViewModel.ShouldPoll(isActive: true, autoPurgeEnabled: true, isElevated: false));
    }

    [Fact]
    public void MemoryStatus_FormatsAndComputesMb()
    {
        var s = new MemoryStatus(16UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024, 75);
        Assert.Equal("16.0 GB", s.TotalDisplay);
        Assert.Equal("4.0 GB", s.AvailableDisplay);
        Assert.Equal("75%", s.LoadDisplay);
        Assert.Equal(4096, s.AvailableMb, 0);
    }

    [Fact]
    public void MemoryStatus_Empty_IsZero()
    {
        Assert.Equal(0UL, MemoryStatus.Empty.TotalBytes);
        Assert.Equal("0 B", MemoryStatus.Empty.AvailableDisplay);
    }

    // ── Progress feedback on the manual purge (regression) ──
    // StandbyMemoryView.xaml binds a progress bar to IsBusy, but the VM never set it, so the bar was
    // structurally incapable of appearing while a purge of a multi-gigabyte cache blocked.

    private static StandbyMemoryViewModel NewVm(string configDir) =>
        new(new Services.StandbyMemoryService(), new Services.StandbyPreferenceService(configDir));

    /// <summary>
    /// Elevated, the manual purge raises the busy flag and clears it, so the bar can appear.
    /// </summary>
    /// <remarks>
    /// This was one test with an <c>if (vm.IsElevated)</c> inside it, asserting one contract on an
    /// elevated host and a different one otherwise (#2171). It always passed and never verified either
    /// branch on both, which is the pattern <c>AdminHelper.ForceElevation</c> exists to replace — its own
    /// documentation names it as the reason the seam was added.
    /// <para>Forcing elevation ON is safe here whatever the host actually is. Past the gate the purge
    /// calls <c>NtSetSystemInformation</c>, which drops a cache and destroys nothing; on a host that is
    /// not really elevated the call simply fails, and either way the flag goes up and comes back down,
    /// which is what this asserts.</para>
    /// </remarks>
    [Fact]
    public async Task ManualPurge_WhenElevated_RaisesIsBusyThenClearsIt()
    {
        using var elevated = AdminHelper.ForceElevation(true);
        using var temp = new TempConfigDir();
        var vm = NewVm(temp.Path);
        Assert.True(vm.IsElevated, "the scope must reach the view-model's constructor");

        var seen = vm.RecordChangesOf(nameof(vm.IsBusy), () => vm.IsBusy);

        await vm.PurgeCommand.ExecuteAsync(null);

        Assert.Equal([true, false], seen);
        Assert.False(vm.IsBusy);
    }

    /// <summary>
    /// Without administrator rights the purge refuses, says why, and never shows the bar.
    /// </summary>
    /// <remarks>
    /// Flashing a progress bar for an operation that was refused would be its own bug, so the absence of
    /// any <c>IsBusy</c> notification is as much the point as the message.
    /// </remarks>
    [Fact]
    public async Task ManualPurge_WhenNotElevated_SaysSoAndNeverShowsTheBar()
    {
        using var notElevated = AdminHelper.ForceElevation(false);
        using var temp = new TempConfigDir();
        var vm = NewVm(temp.Path);
        Assert.False(vm.IsElevated, "the scope must reach the view-model's constructor");

        var seen = vm.RecordChangesOf(nameof(vm.IsBusy), () => vm.IsBusy);

        await vm.PurgeCommand.ExecuteAsync(null);

        Assert.Empty(seen);
        Assert.Contains("administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ManualPurge_LeavesTheBarIndeterminateThenClear()
    {
        // There is no percentage to report for a native purge call, so the bar must be marquee.
        // Elevation forced ON so the command actually enters the body: on a non-elevated host it
        // returns at the gate, and the assertion below would then hold without the purge ever setting
        // the flag it is about.
        using var elevated = AdminHelper.ForceElevation(true);
        using var temp = new TempConfigDir();
        var vm = NewVm(temp.Path);

        await vm.PurgeCommand.ExecuteAsync(null);

        Assert.False(vm.IsProgressIndeterminate);
    }

    [Fact]
    public void AutoPurgeIsDeliberatelyExcludedFromTheProgressBar()
    {
        // The 2 s auto-purge tick must NOT drive the bar: it would strobe on and off in the background
        // every couple of seconds. Pinned so the next refactor does not "fix" the asymmetry back.
        var tick = typeof(StandbyMemoryViewModel).GetMethod(
            "Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(tick);
        // The guard flag the tick uses instead of IsBusy — a separate, non-UI-bound field.
        Assert.NotNull(typeof(StandbyMemoryViewModel).GetField(
            "_autoPurgeInFlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
    }

    // ── A setting that cannot be saved (#2521) ──

    [Fact]
    public void ChangingASetting_ThatCannotBeSaved_SaysSo_AndLeavesTheSavedOne()
    {
        using var temp = new TempConfigDir();
        var file = System.IO.Path.Combine(temp.Path, "standby-preference.json");
        new Services.StandbyPreferenceService(temp.Path).Save(new Services.StandbyPreference(true, 2048));
        var before = System.IO.File.ReadAllBytes(file);

        // Held with delete sharing only while the tab opens, so it cannot read what was saved.
        StandbyMemoryViewModel vm;
        using (new System.IO.FileStream(
                   file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Delete))
            vm = NewVm(temp.Path);

        vm.ThresholdMb = 4096;

        Assert.Equal(ViewModelBase.ChangeNotSavedStatus, vm.StatusMessage);
        Assert.Equal(before, System.IO.File.ReadAllBytes(file));
    }

    /// <summary>A throwaway preference directory, so the developer's real settings are untouched.</summary>
    private sealed class TempConfigDir : IDisposable
    {
        public string Path { get; }

        public TempConfigDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "SysManagerStandbyVmTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true); }
            catch (System.IO.IOException) { /* a leftover temp dir must never fail a test run */ }
        }
    }
}
