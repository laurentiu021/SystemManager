// SysManager · SelectionCarryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The contract of <see cref="SelectionCarry.Apply{TItem, TKey}"/>, which nine tabs now share.
/// </summary>
/// <remarks>
/// The first six shipped their own copy of this logic before the refactor, so its behaviour is already
/// covered several more times through the callers. These tests exist because the shared body is now the
/// single place the rules live, and three of those rules are easy to "simplify" into a bug: the
/// empty-versus-nothing-selected distinction, the optional decision filter only Deep Cleanup needs, and the
/// duplicate-key report — which has to notice a collision WITHOUT changing what the collision does.
/// </remarks>
public class SelectionCarryTests
{
    private sealed class Row(string key, bool selected, long size = 1) : ISelectableRow
    {
        public string Key { get; } = key;
        public long Size { get; } = size;
        public bool IsSelected { get; set; } = selected;
    }

    private static IReadOnlyList<string> Apply(IReadOnlyCollection<Row> previous, IReadOnlyCollection<Row> fresh,
                                              IEqualityComparer<string>? comparer = null,
                                              Func<Row, bool>? carriedADecision = null)
        => SelectionCarry.Apply(previous, fresh, r => r.Key, comparer, carriedADecision);

    [Fact]
    public void EmptyPrevious_IsTheFirstPopulation_AndChangesNothing()
    {
        Row[] fresh = [new("a", true), new("b", false)];

        Apply([], fresh);

        Assert.True(fresh[0].IsSelected);
        Assert.False(fresh[1].IsSelected);
    }

    [Fact]
    public void AMatchingRow_TakesThePreviousTick_InBothDirections()
    {
        Row[] previous = [new("a", false), new("b", true)];
        Row[] fresh = [new("a", true), new("b", false)];

        Apply(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    [Fact]
    public void NothingSelectedInPrevious_IsHonoured_NotTreatedAsFirstPopulation()
    {
        // THE rule that is easiest to break by "simplifying" the empty check into a selection check.
        // Unticking everything is a decision; reading it as "nothing chosen yet" re-ticks the lot.
        Row[] previous = [new("a", false), new("b", false)];
        Row[] fresh = [new("a", true), new("b", true)];

        Apply(previous, fresh);

        Assert.DoesNotContain(fresh, r => r.IsSelected);
    }

    [Fact]
    public void ARowWithNoPreviousMatch_KeepsItsDefault()
    {
        Row[] previous = [new("a", false)];
        Row[] fresh = [new("a", true), new("new", true)];

        Apply(previous, fresh);

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);   // never seen before, so nothing of the user's to honour
    }

    [Fact]
    public void APreviousRowWithNoFreshMatch_IsSimplyDropped()
    {
        Row[] previous = [new("a", false), new("gone", false)];
        Row[] fresh = [new("a", true)];

        Apply(previous, fresh);

        Assert.Single(fresh);
        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void TheComparerDecidesWhatCountsAsTheSameRow()
    {
        Row[] previous = [new(@"C:\Path\App.lnk", false)];

        Row[] caseInsensitive = [new(@"c:\path\app.lnk", true)];
        Apply(previous, caseInsensitive, StringComparer.OrdinalIgnoreCase);
        Assert.False(caseInsensitive[0].IsSelected);

        // Default comparer: the same text in different case is a different row, so it keeps the default.
        Row[] caseSensitive = [new(@"c:\path\app.lnk", true)];
        Apply(previous, caseSensitive);
        Assert.True(caseSensitive[0].IsSelected);
    }

    [Fact]
    public void CarriedADecision_ExcludesPreviousRowsTheUserNeverReallyChose()
    {
        // Deep Cleanup's case: a category with nothing in it was unticked by the SCAN, not by the user, so
        // once it has content it must take the default again rather than staying unticked forever.
        Row[] previous = [new("wasEmpty", selected: false, size: 0), new("hadContent", selected: false, size: 500)];
        Row[] fresh = [new("wasEmpty", selected: true, size: 700), new("hadContent", selected: true, size: 510)];

        Apply(previous, fresh, carriedADecision: r => r.Size > 0);

        Assert.True(fresh[0].IsSelected);    // the scan's default applies again
        Assert.False(fresh[1].IsSelected);   // a real decision, honoured
    }

    [Fact]
    public void CarriedADecision_DoesNotTurnAnAllFilteredPreviousIntoAFirstPopulation()
    {
        // If the filter excludes every previous row there is nothing to carry, and the fresh rows must keep
        // their defaults — not be forced to false by an empty map.
        Row[] previous = [new("a", selected: false, size: 0)];
        Row[] fresh = [new("a", selected: true, size: 100)];

        Apply(previous, fresh, carriedADecision: r => r.Size > 0);

        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void ALaterPreviousRow_WinsOnADuplicateKey_RatherThanThrowing()
    {
        // Keys are expected to be unique, but a duplicate must not take down a scan. Indexer assignment
        // rather than ToDictionary is what makes that true.
        Row[] previous = [new("dup", true), new("dup", false)];
        Row[] fresh = [new("dup", true)];

        Apply(previous, fresh);

        Assert.False(fresh[0].IsSelected);
    }

    [Fact]
    public void ATupleKey_Works_ForRowsIdentifiedByTwoFields()
    {
        // Browser Cleaner keys on browser plus category; this is that shape, on the shared body.
        var previous = new[] { new Row("Chrome|Cache", false), new Row("Edge|Cache", true) };
        var fresh = new[] { new Row("Chrome|Cache", true), new Row("Edge|Cache", true) };

        SelectionCarry.Apply(previous, fresh, r => (r.Key.Split('|')[0], r.Key.Split('|')[1]));

        Assert.False(fresh[0].IsSelected);
        Assert.True(fresh[1].IsSelected);
    }

    // ---- The duplicate-key report (#2405) -------------------------------------------------------------
    // A key claimed by two rows is the one failure mode of this helper that is invisible: nothing throws,
    // the grid looks right, and a tick turns up on a row the user never touched (#2402). Apply now reports
    // it. What it must NOT do is act on it — the collision behaviour above is deliberate and stays.

    [Fact]
    public void ADuplicatePreviousKey_IsReported_AndStillResolvesTheSameWay()
    {
        Row[] previous = [new("dup", true), new("dup", false)];
        Row[] fresh = [new("dup", true)];

        var ambiguous = Apply(previous, fresh);

        Assert.Equal("dup", Assert.Single(ambiguous));
        Assert.False(fresh[0].IsSelected);   // last-wins, exactly as before the report existed
    }

    [Fact]
    public void ADuplicateFreshKey_IsReported_EvenWhenPreviousIsUnique()
    {
        // The half of #2402 the user actually sees: one remembered decision spread onto two rows. Checking
        // only the previous list would call this clean — the duplicate profile is NEW since the last scan.
        Row[] previous = [new("Firefox|Cookies", false)];
        Row[] fresh = [new("Firefox|Cookies", true), new("Firefox|Cookies", true)];

        var ambiguous = Apply(previous, fresh);

        Assert.Equal("Firefox|Cookies", Assert.Single(ambiguous));
        Assert.DoesNotContain(fresh, r => r.IsSelected);
    }

    [Fact]
    public void ADuplicateFreshKey_IsReportedOnAFirstPopulation_BeforeItCanCarryAnythingWrongly()
    {
        // Nothing has gone wrong yet — there is nothing to carry. The next rescan is when it bites, so the
        // earliest scan that can prove the key is not unique is the one that should say so.
        Row[] fresh = [new("a", true), new("a", false)];

        var ambiguous = Apply([], fresh);

        Assert.Equal("a", Assert.Single(ambiguous));
        Assert.True(fresh[0].IsSelected);    // first population still changes nothing
        Assert.False(fresh[1].IsSelected);
    }

    [Fact]
    public void AKeyOnBothSides_IsAMatch_NotADuplicate()
    {
        // The whole point of the feature. Reporting across the two lists instead of within each one would
        // flag every successfully carried row, which is the easy way to write this wrongly.
        Row[] previous = [new("a", false), new("b", true)];
        Row[] fresh = [new("a", true), new("b", false)];

        Assert.Empty(Apply(previous, fresh));
    }

    [Fact]
    public void AKeyClaimedThreeTimes_IsReportedOnce()
    {
        Row[] fresh = [new("dup", true), new("dup", true), new("dup", true)];

        Assert.Equal("dup", Assert.Single(Apply([], fresh)));
    }

    [Fact]
    public void ADuplicateAmongPreviousRowsTheFilterExcluded_IsNotReported()
    {
        // Only one of the two ever reaches the map, so no decision is lost and there is nothing to report.
        // Deep Cleanup is the caller that would otherwise see phantom collisions on its empty categories.
        Row[] previous = [new("dup", selected: false, size: 0), new("dup", selected: true, size: 500)];
        Row[] fresh = [new("dup", selected: false, size: 600)];

        var ambiguous = Apply(previous, fresh, carriedADecision: r => r.Size > 0);

        Assert.Empty(ambiguous);
        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void TheReport_UsesTheSameComparerAsTheCarry()
    {
        Row[] previous = [new(@"C:\Path\App.lnk", true), new(@"c:\path\app.lnk", false)];
        Row[] fresh = [new(@"C:\Path\App.lnk", true)];

        Assert.Single(Apply(previous, fresh, StringComparer.OrdinalIgnoreCase));

        // Case-sensitive, the same two rows are two rows — so there is no collision to report.
        Assert.Empty(Apply(previous, fresh));
    }

    [Fact]
    public void DuplicateKeys_ReportsNothing_ForAnEmptyOrUniqueSequence()
    {
        Assert.Empty(SelectionCarry.DuplicateKeys(Array.Empty<Row>(), r => r.Key));
        Assert.Empty(SelectionCarry.DuplicateKeys<Row, string>([new("a", true), new("b", true)], r => r.Key));
    }

    [Fact]
    public void DuplicateKeys_ReportsEachCollidingKeyOnce_InTheOrderTheCollisionAppeared()
    {
        Row[] rows = [new("y", true), new("x", true), new("x", true), new("y", true), new("x", true)];

        // x collides first (index 2), y second (index 3) — the order the keys are first CLAIMED is not it.
        Assert.Equal(new[] { "x", "y" }, SelectionCarry.DuplicateKeys(rows, r => r.Key));
    }

    [Fact]
    public void DuplicateKeys_HonoursTheComparer()
    {
        Row[] rows = [new("Firefox", true), new("firefox", true)];

        // Reported as the FIRST spelling, because that is the one Apply's dictionary ends up keyed on —
        // naming the later spelling would put something in the log that no lookup holds.
        Assert.Equal("Firefox", Assert.Single(SelectionCarry.DuplicateKeys(rows, r => r.Key, StringComparer.OrdinalIgnoreCase)));
        Assert.Empty(SelectionCarry.DuplicateKeys(rows, r => r.Key));
    }

    [Fact]
    public void DuplicateKeys_WorksOnATupleKey_TheShapeTwoCallersUse()
    {
        Row[] rows = [new("Firefox|Cookies", true), new("Firefox|Cache", true), new("Firefox|Cookies", true)];

        var ambiguous = SelectionCarry.DuplicateKeys(rows, r => (r.Key.Split('|')[0], r.Key.Split('|')[1]));

        Assert.Equal(("Firefox", "Cookies"), Assert.Single(ambiguous));
    }
}
