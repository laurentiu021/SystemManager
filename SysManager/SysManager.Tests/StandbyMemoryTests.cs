// SysManager · StandbyMemoryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

public class StandbyMemoryTests
{
    [Theory]
    [InlineData(512, 1024, true)]    // 512 MB available, threshold 1 GB → purge
    [InlineData(1023, 1024, true)]   // just below → purge
    [InlineData(1024, 1024, false)]  // exactly at threshold → no purge
    [InlineData(2048, 1024, false)]  // plenty available → no purge
    public void ShouldAutoPurge_FiresBelowThreshold(double availableMb, double thresholdMb, bool expected)
        => Assert.Equal(expected, StandbyMemoryViewModel.ShouldAutoPurge(availableMb, thresholdMb));

    [Fact]
    public void ShouldAutoPurge_GuardsZeroOrNegative()
    {
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(0, 1024));   // no reading → don't purge
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(512, 0));    // no threshold → don't purge
        Assert.False(StandbyMemoryViewModel.ShouldAutoPurge(-1, 1024));
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

    [Fact]
    public async Task ManualPurge_RaisesIsBusyThenClearsIt()
    {
        using var temp = new TempConfigDir();
        var vm = NewVm(temp.Path);

        var seen = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsBusy)) seen.Add(vm.IsBusy);
        };

        await vm.PurgeCommand.ExecuteAsync(null);

        if (vm.IsElevated)
        {
            // Elevated: the purge ran, so the flag went up and came back down.
            Assert.Equal([true, false], seen);
        }
        else
        {
            // Not elevated: the command short-circuits before any work, so the bar must never appear
            // — flashing it for an operation that was refused would be its own bug.
            Assert.Empty(seen);
            Assert.Contains("administrator", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ManualPurge_LeavesTheBarIndeterminateThenClear()
    {
        // There is no percentage to report for a native purge call, so the bar must be marquee.
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
