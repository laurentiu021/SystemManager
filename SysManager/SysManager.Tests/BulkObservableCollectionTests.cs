// SysManager · BulkObservableCollectionTests
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Specialized;
using SysManager.Helpers;

namespace SysManager.Tests;

public class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceWith_EmptyCollection_ClearsAll()
    {
        var collection = new BulkObservableCollection<int>();
        collection.Add(1);
        collection.Add(2);
        collection.Add(3);

        collection.ReplaceWith(Array.Empty<int>());

        Assert.Empty(collection);
    }

    [Fact]
    public void ReplaceWith_PopulatesWithNewItems()
    {
        var collection = new BulkObservableCollection<string>();
        collection.Add("old");

        collection.ReplaceWith(new[] { "alpha", "beta", "gamma" });

        Assert.Equal(3, collection.Count);
        Assert.Equal("alpha", collection[0]);
        Assert.Equal("beta", collection[1]);
        Assert.Equal("gamma", collection[2]);
    }

    [StaFact]
    public void ReplaceWith_FiresSingleResetNotification()
    {
        var collection = new BulkObservableCollection<int>();
        collection.Add(1);
        collection.Add(2);

        var resetEvents = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => resetEvents.Add(e);

        collection.ReplaceWith(new[] { 10, 20, 30 });

        var resets = resetEvents.Where(e => e.Action == NotifyCollectionChangedAction.Reset).ToList();
        Assert.Single(resets);
    }

    [StaFact]
    public void ReplaceWith_SuppressesIndividualNotifications()
    {
        var collection = new BulkObservableCollection<int>();
        collection.Add(1);
        collection.Add(2);

        var events = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => events.Add(e);

        collection.ReplaceWith(new[] { 10, 20, 30 });

        // Should not have any Add or Remove events — only the final Reset
        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Add);
        Assert.DoesNotContain(events, e => e.Action == NotifyCollectionChangedAction.Remove);
    }

    [Fact]
    public void ReplaceWith_NullItems_ThrowsArgumentNullException()
    {
        var collection = new BulkObservableCollection<int>();

        Assert.Throws<ArgumentNullException>(() => collection.ReplaceWith(null!));
    }

    [Fact]
    public void ReplaceWith_EmptyEnumerable_ResultsInEmptyCollection()
    {
        var collection = new BulkObservableCollection<string>();
        collection.Add("existing");

        collection.ReplaceWith(Enumerable.Empty<string>());

        Assert.Empty(collection);
    }

    // ── The source is materialized before the collection is touched (regression) ──
    // Every caller passes a lazy LINQ query, and ReplaceWith used to enumerate it WHILE rebuilding
    // Items. The two cases below were silently wrong because of it. (Notification suppression already
    // meant a Reset subscriber could not observe the rebuild, so that was never the problem — this is
    // about what the lazy source itself sees.)

    [Fact]
    public void ReplaceWith_ItsOwnContents_KeepsThemRatherThanClearingItself()
    {
        // Items.Clear() runs before anything is added back, so a source that IS this collection would
        // be emptied first and everything lost. The snapshot is taken up front to prevent that.
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceWith(new[] { 1, 2, 3 });

        collection.ReplaceWith(collection);

        Assert.Equal([1, 2, 3], collection);
    }

    [Fact]
    public void ReplaceWith_AThrowingSourceLeavesTheCollectionUntouched()
    {
        // The snapshot is built before Items.Clear(), so a query that fails part-way cannot leave a
        // half-rebuilt collection bound to the UI.
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceWith(new[] { 1, 2, 3 });

        static IEnumerable<int> Failing()
        {
            yield return 9;
            throw new InvalidOperationException("source failed");
        }

        Assert.Throws<InvalidOperationException>(() => collection.ReplaceWith(Failing()));
        Assert.Equal([1, 2, 3], collection);
    }
}
