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
}
