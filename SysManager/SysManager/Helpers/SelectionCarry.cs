// SysManager · SelectionCarry — keep the user's ticks when a bound list is rebuilt
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using Serilog;

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
/// Nine tabs rebuild a bound collection from a fresh scan. The six this started with used to re-derive the
/// tick state from a default — so a refresh discarded what the user had chosen. In four of them the fresh
/// rows arrive pre-selected, which meant a refresh did not merely forget the choice but REVERSED it, and the
/// next action then deleted, upgraded or exported exactly what had been excluded. <c>RefreshOnF5</c> reaches
/// all six, so pressing F5 was enough (#2300, #2301, #2304).
/// <para>Each of those six was fixed with its own copy of this logic first, deliberately, so the fixes could
/// ship with tests rather than waiting on a refactor. This is that refactor — one body, no behaviour change —
/// and three tabs have been keyed through it since.</para>
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
    /// take the default again instead of staying unticked for a choice nobody made. The other eight callers
    /// leave it null because their defaults are constants rather than measurements.
    /// </param>
    /// <returns>
    /// The keys that identify more than one row, each once, empty when the key is unique on both sides —
    /// see <see cref="DuplicateKeys{TItem, TKey}"/> for why that is worth knowing. Callers are free to
    /// ignore it: it is also logged at Debug, so a field report carries the evidence whether or not anyone
    /// opted in.
    /// </returns>
    /// <remarks>
    /// Note what this does NOT do: skip when nothing in <paramref name="previous"/> is selected. Unticking
    /// everything is a decision, and reading it as "nothing chosen yet" re-ticks the lot — which is the bug
    /// this exists to prevent. The first-population test is on the collection being empty, and every caller
    /// has a test for that specific distinction.
    /// </remarks>
    public static IReadOnlyList<TKey> Apply<TItem, TKey>(
        IReadOnlyCollection<TItem> previous,
        IReadOnlyCollection<TItem> fresh,
        Func<TItem, TKey> keyOf,
        IEqualityComparer<TKey>? comparer = null,
        Func<TItem, bool>? carriedADecision = null)
        where TItem : ISelectableRow
        where TKey : notnull
    {
        IEnumerable<TItem> carried = carriedADecision is null ? previous : previous.Where(carriedADecision);

        // Both sides are checked, and separately, because they break differently and neither implies the
        // other. A key claimed twice in PREVIOUS loses a decision — the last row's wins. A key claimed twice
        // in FRESH spreads one decision onto a row the user never touched, which is the half of #2402 the
        // user actually saw: one profile's untick turned up on the other. Checking only previous would miss
        // the case where the duplicate is new (a second Firefox profile created since the last scan), so
        // this runs before the first-population return as well — a first population has nothing to carry
        // wrongly YET, and the next rescan is when it bites.
        //
        // Not the concatenation of the two: a key appearing once on each side is a MATCH, which is the
        // entire point of the feature.
        var inPrevious = DuplicateKeys(carried, keyOf, comparer);
        var inFresh = DuplicateKeys(fresh, keyOf, comparer);
        IReadOnlyList<TKey> ambiguous =
            inPrevious.Count == 0 ? inFresh
            : inFresh.Count == 0 ? inPrevious
            : [.. inPrevious.Union(inFresh, comparer)];

        if (ambiguous.Count > 0)
        {
            Log.Debug("SelectionCarry: {Count} key(s) identify more than one row ({Keys}), so one row's "
                      + "tick can be applied to another. The key does not uniquely identify a row.",
                      ambiguous.Count, ambiguous);
        }

        if (previous.Count == 0) return ambiguous;

        var decided = new Dictionary<TKey, bool>(comparer);
        foreach (var row in carried)
        {
            decided[keyOf(row)] = row.IsSelected;
        }

        foreach (var row in fresh)
        {
            if (decided.TryGetValue(keyOf(row), out var wasSelected))
                row.IsSelected = wasSelected;
        }

        return ambiguous;
    }

    /// <summary>
    /// The keys that more than one row in <paramref name="rows"/> claims, each reported once, in the order
    /// the collision was first seen.
    /// </summary>
    /// <remarks>
    /// The uniqueness <see cref="Apply{TItem, TKey}"/> assumes, made checkable. A duplicate key is not a
    /// crash and is invisible on screen: the rows look correct, and the user's tick simply turns up on a row
    /// they never touched. #2402 is what that costs — two Firefox profiles each produced a row named plainly
    /// "Firefox", so Browser Cleaner's <c>(Browser, Category)</c> key identified two rows, and unticking
    /// cookies on one profile unticked them on the other. The hazard was documented on
    /// <see cref="Apply{TItem, TKey}"/>'s key parameter from the start and detected by nothing.
    /// <para>Apply calls this itself, so no caller has to opt in. A service can also call it over its own
    /// scan output to assert the invariant its carry key depends on, which is the only way to catch a
    /// duplicate that comes from the DATA rather than from the choice of key.</para>
    /// </remarks>
    public static IReadOnlyList<TKey> DuplicateKeys<TItem, TKey>(
        IEnumerable<TItem> rows,
        Func<TItem, TKey> keyOf,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        var seen = new HashSet<TKey>(comparer);
        HashSet<TKey>? reported = null;
        List<TKey>? duplicates = null;

        foreach (var row in rows)
        {
            var key = keyOf(row);
            if (seen.Add(key)) continue;

            // Lazily allocated: the case worth optimising is the one where the key IS unique, which is
            // every call the codebase currently makes on every rescan.
            reported ??= new HashSet<TKey>(comparer);
            if (!reported.Add(key)) continue;

            // The FIRST spelling, not this row's. Under a case-insensitive comparer "Firefox" and "firefox"
            // collide, and the one Apply's dictionary is actually keyed on is whichever arrived first —
            // reporting the later spelling would name something no lookup holds.
            seen.TryGetValue(key, out var asFirstSeen);
            (duplicates ??= []).Add(asFirstSeen!);
        }

        return duplicates is null ? [] : duplicates;
    }
}
