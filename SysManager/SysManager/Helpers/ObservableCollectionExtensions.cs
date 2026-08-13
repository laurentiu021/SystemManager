// SysManager · ObservableCollectionExtensions
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SysManager.Helpers;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports bulk replacement
/// with a single <see cref="NotifyCollectionChangedAction.Reset"/> notification.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    // Immutable and identical on every call. ReplaceWith is the standard bulk-refresh path for
    // poll-driven lists, so it runs on the UI thread on every refresh tick; the BCL caches exactly
    // these three instances internally for the same reason.
    private static readonly PropertyChangedEventArgs CountChanged = new("Count");
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs CollectionReset =
        new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Replaces all items with a single Reset notification instead of
    /// N+1 individual change events.
    /// </summary>
    /// <remarks>
    /// <paramref name="items"/> is materialized BEFORE the collection is touched. Every caller passes a
    /// lazy LINQ query, which made two cases silently wrong:
    /// <list type="bullet">
    /// <item>Passing this collection as its own source: <c>Items.Clear()</c> runs before anything is
    /// added back, so the source was emptied first and every item lost.</item>
    /// <item>A source that throws part-way: the collection was left half-replaced (and still bound to
    /// the UI). It is now left exactly as it was.</item>
    /// </list>
    /// </remarks>
    public void ReplaceWith(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Snapshot first: never enumerate the caller's sequence while mutating Items (see remarks).
        // Always copied rather than reused when it is already a list — the source could be this very
        // collection, which Items.Clear() below would empty before a single item was carried over.
        var snapshot = items.ToList();

        // Refuse to rewrite the collection from inside a change handler. Items.Clear()/Items.Add()
        // below bypass the base class's own CheckReentrancy calls, so without this a handler that
        // re-enters ReplaceWith would silently rewrite the collection mid-dispatch and desync an
        // ItemsControl from its source; failing fast is the documented ObservableCollection contract.
        CheckReentrancy();

        // Mutating Items (the backing list) directly rather than via Clear()/Add() is the whole point:
        // those route through ClearItems/InsertItem and would raise N+1 notifications. Items does not,
        // which is why the three explicit raises below are needed — and why no suppression flag is:
        // nothing here can raise an event that would need suppressing.
        Items.Clear();
        for (int i = 0; i < snapshot.Count; i++)
            Items.Add(snapshot[i]);

        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(CollectionReset);
    }
}
