// SysManager · FakeWindowsUpdateAgent
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Tests;

// A Windows Update Agent made of plain objects, for WindowsUpdateService to read late-bound the way it reads WUA's
// own (#2791). Each type has the members the service reads, under WUA's names, and nothing else. Public, because the
// dynamic calls that read them are made from the app's own assembly.

/// <summary>WUA's <c>Microsoft.Update.Session</c>, which a scan starts from.</summary>
public sealed class FakeUpdateSession
{
    /// <summary>What a search finds.</summary>
    public List<FakeUpdate> Pending { get; } = [];

    /// <summary>The criteria of every search made, in order.</summary>
    public List<string> Searches { get; } = [];

    /// <summary>Thrown by a search instead of an answer, when set.</summary>
    public Exception? SearchFails { get; set; }

    public FakeUpdateSearcher CreateUpdateSearcher() => new(this);
}

/// <summary>WUA's update searcher.</summary>
public sealed class FakeUpdateSearcher(FakeUpdateSession session)
{
    public bool IncludePotentiallySupersededUpdates { get; set; }

    public FakeSearchResult Search(string criteria)
    {
        session.Searches.Add(criteria);
        if (session.SearchFails is { } failure) throw failure;
        return new FakeSearchResult(new FakeCollection<FakeUpdate>([.. session.Pending]));
    }
}

/// <summary>A search's result.</summary>
public sealed class FakeSearchResult(FakeCollection<FakeUpdate> updates)
{
    public FakeCollection<FakeUpdate> Updates { get; } = updates;
}

/// <summary>A WUA collection: a count, the item at an index, and an Add for one the caller fills.</summary>
public sealed class FakeCollection<T>(List<T> items)
{
    public FakeCollection() : this([])
    {
    }

    public int Count => items.Count;

    public T Item(int index) => items[index];

    public void Add(T item) => items.Add(item);
}

/// <summary>An update's identity.</summary>
public sealed class FakeIdentity(string updateId)
{
    public string UpdateID { get; } = updateId;
}

/// <summary>A category an update is in: a classification such as Drivers, or a product.</summary>
public sealed class FakeCategory(string name, string categoryId)
{
    public string Name { get; } = name;
    public string CategoryID { get; } = categoryId;
}

/// <summary>A WUA update, with what a scan and the pill read off one.</summary>
public sealed class FakeUpdate(params FakeCategory[] categories)
{
    public string Title { get; init; } = "";

    /// <summary>The update's ID, which <see cref="Identity"/> carries.</summary>
    public string Id { get; init; } = "";

    /// <summary>WUA's update type: 1 for software, 2 for a driver.</summary>
    public int Type { get; init; } = 1;

    public decimal MaxDownloadSize { get; init; }

    public bool IsHidden { get; init; }

    /// <summary>The KB numbers, without "KB", that <see cref="KBArticleIDs"/> lists.</summary>
    public string[] KbNumbers { get; init; } = [];

    public FakeIdentity Identity => new(Id);

    public FakeCollection<string> KBArticleIDs => new([.. KbNumbers]);

    public FakeCollection<FakeCategory> Categories => new([.. categories]);
}
