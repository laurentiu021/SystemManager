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

    // ── Rewriting the collection from inside a change handler ──
    // ReplaceWith mutates Items (the backing list) directly. That is deliberate — it is how one Reset
    // replaces N+1 events — but it also skips the CheckReentrancy call every ObservableCollection
    // mutator makes first. Without an explicit check, a handler that re-enters ReplaceWith rewrites the
    // collection in the middle of event dispatch and a bound ItemsControl's container generator can
    // desync from its source. Failing fast is the documented contract.

    [Fact]
    public void ReplaceWith_ReenteredFromAChangeHandler_ThrowsInsteadOfRewritingMidDispatch()
    {
        var collection = new BulkObservableCollection<int>();
        collection.ReplaceWith([1, 2, 3]);

        Exception? reentrant = null;

        // Two subscribers, because ObservableCollection only treats reentrancy as an error when more
        // than one handler is attached — which is the real situation: WPF's ListCollectionView is
        // subscribed alongside application code.
        collection.CollectionChanged += (_, _) =>
            reentrant ??= Record.Exception(() => collection.ReplaceWith([7, 8, 9]));
        collection.CollectionChanged += (_, _) => { };

        collection.ReplaceWith([4, 5, 6]);

        Assert.IsType<InvalidOperationException>(reentrant);
        Assert.Equal([4, 5, 6], collection); // the reentrant rewrite did not take effect
    }

    [Fact]
    public void ReplaceWith_WithOneSubscriber_StillRefreshesNormally()
    {
        // Guard against over-correcting: the ordinary single-subscriber refresh must keep working.
        var collection = new BulkObservableCollection<int>();
        var resets = 0;
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
        };

        collection.ReplaceWith([1, 2, 3]);
        collection.ReplaceWith([4, 5]);

        Assert.Equal([4, 5], collection);
        Assert.Equal(2, resets);
    }
}
