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
    private bool _suppressNotifications;

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

        _suppressNotifications = true;
        try
        {
            Items.Clear();
            for (int i = 0; i < snapshot.Count; i++)
                Items.Add(snapshot[i]);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
    }
}
