// SysManager · SelectionCarryTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Helpers;

namespace SysManager.Tests;

/// <summary>
/// The contract of <see cref="SelectionCarry.Apply{TItem, TKey}"/>, which six tabs now share.
/// </summary>
/// <remarks>
/// Each of the six shipped its own copy of this logic first, so its behaviour is already covered five more
/// times through the callers. These tests exist because the shared body is now the single place the rules
/// live, and two of those rules are easy to "simplify" into a bug: the empty-versus-nothing-selected
/// distinction, and the optional decision filter only Deep Cleanup needs.
/// </remarks>
public class SelectionCarryTests
{
    private sealed class Row(string key, bool selected, long size = 1) : ISelectableRow
    {
        public string Key { get; } = key;
        public long Size { get; } = size;
        public bool IsSelected { get; set; } = selected;
    }

    private static void Apply(IReadOnlyCollection<Row> previous, IReadOnlyCollection<Row> fresh,
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
}
