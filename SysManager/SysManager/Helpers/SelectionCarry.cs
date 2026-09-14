// SysManager · SelectionCarry — keep the user's ticks when a bound list is rebuilt
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// A row the user can tick, and which can therefore lose that tick when its list is rebuilt.
/// </summary>
/// <remarks>
/// Implemented by every model whose collection is replaced by a rescan. Existing purely so
/// <see cref="SelectionCarry.Apply{TItem, TKey}"/> can read and write the tick without each caller having
/// to hand it a getter and a setter delegate.
/// </remarks>
public interface ISelectableRow
{
    /// <summary>Whether the user has this row ticked.</summary>
    bool IsSelected { get; set; }
}

/// <summary>
/// Copies the ticks the user set from a previous list onto a freshly built one.
/// </summary>
/// <remarks>
/// Six tabs rebuild a bound collection from a fresh scan, and every one of them used to re-derive the tick
/// state from a default — so a refresh discarded what the user had chosen. In four of them the fresh rows
/// arrive pre-selected, which meant a refresh did not merely forget the choice but REVERSED it, and the next
/// action then deleted, upgraded or exported exactly what had been excluded. <c>RefreshOnF5</c> reaches all
/// six, so pressing F5 was enough (#2300, #2301, #2304).
/// <para>Each of the six was fixed with its own copy of this logic first, deliberately, so the fixes could
/// ship with tests rather than waiting on a refactor. This is that refactor: one body, six callers, no
/// behaviour change.</para>
/// </remarks>
public static class SelectionCarry
{
    /// <summary>
    /// Applies the previous list's ticks to <paramref name="fresh"/>, matching rows by
    /// <paramref name="keyOf"/>.
    /// </summary>
    /// <param name="previous">
    /// The rows as they were before the rebuild. EMPTY means the first population — the only time the
    /// caller's own default is the answer — and nothing is applied.
    /// </param>
    /// <param name="fresh">The newly built rows, which are mutated in place.</param>
    /// <param name="keyOf">
    /// The stable identity of a row. Choosing this wrongly is the subtle way to get this feature back to
    /// front: keyed too narrowly a row looks new and silently takes the default, keyed too broadly one
    /// row's choice is applied to another.
    /// </param>
    /// <param name="comparer">How keys are compared. Pass a case-insensitive comparer for filesystem paths.</param>
    /// <param name="carriedADecision">
    /// Optional filter on which PREVIOUS rows count as the user's choice. Only Deep Cleanup needs it: an
    /// empty category was unticked by the scan rather than by the user, so if it has content now it should
    /// take the default again instead of staying unticked for a choice nobody made. The other five callers
    /// leave it null because their defaults are constants rather than measurements.
    /// </param>
    /// <remarks>
    /// Note what this does NOT do: skip when nothing in <paramref name="previous"/> is selected. Unticking
    /// everything is a decision, and reading it as "nothing chosen yet" re-ticks the lot — which is the bug
    /// this exists to prevent. The first-population test is on the collection being empty, and every caller
    /// has a test for that specific distinction.
    /// </remarks>
    public static void Apply<TItem, TKey>(
        IReadOnlyCollection<TItem> previous,
        IReadOnlyCollection<TItem> fresh,
        Func<TItem, TKey> keyOf,
        IEqualityComparer<TKey>? comparer = null,
        Func<TItem, bool>? carriedADecision = null)
        where TItem : ISelectableRow
        where TKey : notnull
    {
        if (previous.Count == 0) return;

        var decided = new Dictionary<TKey, bool>(comparer);
        foreach (var row in previous)
        {
            if (carriedADecision is null || carriedADecision(row))
                decided[keyOf(row)] = row.IsSelected;
        }

        foreach (var row in fresh)
        {
            if (decided.TryGetValue(keyOf(row), out var wasSelected))
                row.IsSelected = wasSelected;
        }
    }
}
