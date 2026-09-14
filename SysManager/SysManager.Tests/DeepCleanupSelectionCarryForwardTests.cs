// SysManager · DeepCleanupSelectionCarryForwardTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.IO;
using SysManager.Models;
using SysManager.ViewModels;

namespace SysManager.Tests;

/// <summary>
/// Tests that a Deep Cleanup rescan keeps the ticks the user set (#2301).
/// </summary>
/// <remarks>
/// The scan summary tells the user to "untick anything you want to keep", and then a rescan re-ticked all
/// of it: categories arrive from the service pre-selected and the view model replaced the whole collection.
/// <c>RefreshOnF5</c> is <c>ScanCommand</c>, so pressing F5 was enough.
/// <para>The carry-forward is tested directly rather than through the command, because
/// <c>DeepCleanupService</c> is concrete and a scan walks the real filesystem — driving the command would
/// make these assertions depend on what happens to be in the machine's temp folders. The last test holds
/// the view model to actually calling it, so the logic cannot end up correct and orphaned.</para>
/// </remarks>
public class DeepCleanupSelectionCarryForwardTests
{
    private static CleanupCategory Cat(string name, long bytes, bool selected, bool destructive = false) => new()
    {
        Name = name,
        Description = name + " description",
        Paths = [@"C:\nowhere\" + name],
        TotalSizeBytes = bytes,
        FileCount = bytes > 0 ? 3 : 0,
        IsDestructiveHint = destructive,
        IsSelected = selected
    };

    /// <summary>A fresh scan result, selected the way the service selects: has content and is not destructive.</summary>
    private static CleanupCategory Scanned(string name, long bytes, bool destructive = false) =>
        Cat(name, bytes, selected: bytes > 0 && !destructive, destructive: destructive);

    [Fact]
    public void FirstScan_LeavesTheServiceDefaultsAlone()
    {
        // Nothing to preserve, so the scan's own choice stands. Getting this wrong would ship a tab where
        // the first scan selects nothing and the Clean button is dead.
        CleanupCategory[] fresh = [Scanned("Temp", 500), Scanned("Empty", 0), Scanned("Dumps", 900, destructive: true)];

        DeepCleanupViewModel.CarryForwardSelection([], fresh);

        Assert.True(fresh[0].IsSelected);
        Assert.False(fresh[1].IsSelected);   // empty
        Assert.False(fresh[2].IsSelected);   // destructive
    }

    [Fact]
    public void Rescan_KeepsACategoryTheUserUnticked()
    {
        // THE regression. The user unticked a category with content; the rescan must not re-tick it.
        CleanupCategory[] previous = [Cat("Temp", 500, selected: false), Cat("Logs", 800, selected: true)];
        CleanupCategory[] fresh = [Scanned("Temp", 520), Scanned("Logs", 810)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void Rescan_KeepsEverythingUnticked_WhenTheUserUntickedEverything()
    {
        // The case that a "did they select anything?" test would get wrong: unticking the lot is a
        // decision, not an absence of one, and reading it as "nothing chosen yet" re-ticks all of it.
        CleanupCategory[] previous = [Cat("Temp", 500, selected: false), Cat("Logs", 800, selected: false)];
        CleanupCategory[] fresh = [Scanned("Temp", 520), Scanned("Logs", 810)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.DoesNotContain(fresh, c => c.IsSelected);
    }

    [Fact]
    public void Rescan_KeepsACategoryTheUserTicked_EvenThoughItIsDestructive()
    {
        // A destructive category defaults to unticked, but the user is allowed to override that, and the
        // override has to survive — otherwise ticking it is undone by the next refresh.
        CleanupCategory[] previous = [Cat("Dumps", 900, selected: true, destructive: true)];
        CleanupCategory[] fresh = [Scanned("Dumps", 950, destructive: true)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void ACategoryThatWasEmpty_TakesTheDefaultAgainWhenItFillsUp()
    {
        // It was unticked by the SCAN, not by the user — there was nothing to decide about. Carrying that
        // forward would leave it permanently unticked for a choice its owner never made.
        CleanupCategory[] previous = [Cat("UpdateCache", 0, selected: false)];
        CleanupCategory[] fresh = [Scanned("UpdateCache", 700)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void ACategoryThatWasEmptyAndStaysEmpty_StaysUnticked()
    {
        CleanupCategory[] previous = [Cat("UpdateCache", 0, selected: false)];
        CleanupCategory[] fresh = [Scanned("UpdateCache", 0)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void ACategoryThatIsNewSinceTheLastScan_TakesTheDefault()
    {
        // The user never saw it, so there is nothing of theirs to honour.
        CleanupCategory[] previous = [Cat("Temp", 500, selected: false)];
        CleanupCategory[] fresh = [Scanned("Temp", 520), Scanned("BrandNew", 300)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void ACategoryThatDisappeared_IsSimplyNotCarried()
    {
        // No exception, and nothing invented: a name in the previous set with no match in the fresh one is
        // dropped along with the category.
        CleanupCategory[] previous = [Cat("Temp", 500, selected: false), Cat("Gone", 100, selected: false)];
        CleanupCategory[] fresh = [Scanned("Temp", 520)];

        DeepCleanupViewModel.CarryForwardSelection(previous, fresh);

        Assert.Single(fresh);
        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void TheScan_ActuallyCallsTheCarryForward()
    {
        // Without this, the logic above could be perfectly correct and wired to nothing — the defect class
        // this repo hits most often. A scan walks the real filesystem, so the call site is asserted in the
        // source rather than by running it; the assertions above cover the behaviour.
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(CleanupCategory).Assembly.Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "SysManager", "ViewModels")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "SysManager", "ViewModels", "DeepCleanupViewModel.cs"));

        // The call, not merely the name: the declaration itself contains "CarryForwardSelection".
        Assert.Contains("CarryForwardSelection(Categories,", source, StringComparison.Ordinal);

        // And it must run BEFORE the collection is replaced, or there is nothing left to read the old
        // ticks from.
        var call = source.IndexOf("CarryForwardSelection(Categories,", StringComparison.Ordinal);
        var replace = source.IndexOf("Categories.ReplaceWith(", StringComparison.Ordinal);
        Assert.True(replace > 0, "DeepCleanupViewModel no longer calls Categories.ReplaceWith — re-point this guard.");
        Assert.True(call < replace,
            "CarryForwardSelection runs after Categories.ReplaceWith, so the previous ticks are already gone.");
    }
}
