// SysManager · FakeWindowsUpdateAgent
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using SysManager.Services;

namespace SysManager.Tests;

// A Windows Update Agent made of plain objects, for WindowsUpdateService to read late-bound the way it reads WUA's
// own (#2791). Each type has the members the service reads, under WUA's names, and nothing else. Public, because the
// dynamic calls that read them are made from the app's own assembly.

/// <summary>WUA's <c>Microsoft.Update.Session</c>, which a scan and an install start from.</summary>
public sealed class FakeUpdateSession
{
    /// <summary>What a search finds.</summary>
    public List<FakeUpdate> Pending { get; } = [];

    /// <summary>The criteria of every search made, in order.</summary>
    public List<string> Searches { get; } = [];

    /// <summary>Thrown by a search instead of an answer, when set.</summary>
    public Exception? SearchFails { get; set; }

    /// <summary>A search runs until it is asked to stop, when set, as an online search can for minutes.</summary>
    public bool SearchRunsUntilStopped { get; set; }

    /// <summary>A download runs until it is asked to stop, when set.</summary>
    public bool DownloadRunsUntilStopped { get; set; }

    /// <summary>Completes once a search has started, so a test cancels while one runs and not before.</summary>
    public TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a download has started.</summary>
    public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every job started, searches and downloads, in order.</summary>
    public List<FakeJob> Jobs { get; } = [];

    /// <summary>Called with each update as it downloads, before the download is done: a test cancels here.</summary>
    public Action<FakeUpdate>? Downloading { get; set; }

    /// <summary>Called with each update as it installs, before the install is done.</summary>
    public Action<FakeUpdate>? Installing { get; set; }

    /// <summary>The IDs of the updates installed, in order.</summary>
    public List<string> Installed { get; } = [];

    public FakeUpdateSearcher CreateUpdateSearcher() => new(this);

    public FakeUpdateDownloader CreateUpdateDownloader() => new(this);

    public FakeUpdateInstaller CreateUpdateInstaller() => new(this);
}

/// <summary>WUA's update downloader, which downloads as a job: every update, unless the job is stopped.</summary>
public sealed class FakeUpdateDownloader(FakeUpdateSession session)
{
    public FakeCollection<FakeUpdate>? Updates { get; set; }

    public FakeJob BeginDownload(object onProgressChanged, object onCompleted, object? state)
    {
        var job = new FakeJob(onCompleted);
        session.Jobs.Add(job);
        session.DownloadStarted.TrySetResult();
        for (var i = 0; i < Updates!.Count; i++)
            session.Downloading?.Invoke(Updates.Item(i));
        if (!session.DownloadRunsUntilStopped) job.Finish();
        return job;
    }

    public FakeOperationResult EndDownload(FakeJob job)
    {
        if (job.StopRequested) return new FakeOperationResult(FakeOperationResult.Aborted, 0);
        for (var i = 0; i < Updates!.Count; i++)
            Updates.Item(i).IsDownloaded = true;
        return new FakeOperationResult(FakeOperationResult.Succeeded, 0);
    }
}

/// <summary>WUA's update installer: it installs the updates it was given, every one successfully.</summary>
public sealed class FakeUpdateInstaller(FakeUpdateSession session)
{
    public FakeCollection<FakeUpdate>? Updates { get; set; }

    public FakeOperationResult Install()
    {
        var reboot = false;
        for (var i = 0; i < Updates!.Count; i++)
        {
            var update = Updates.Item(i);
            session.Installing?.Invoke(update);
            session.Installed.Add(update.Id);
            reboot |= update.NeedsRestart;
        }

        return new FakeOperationResult(FakeOperationResult.Succeeded, 0, reboot);
    }
}

/// <summary>A download's or an install's result, which is also the one update's own when there is one update.</summary>
public sealed class FakeOperationResult(int resultCode, int hResult, bool rebootRequired = false)
{
    /// <summary>WUA's <c>OperationResultCode.orcSucceeded</c>.</summary>
    public const int Succeeded = 2;

    /// <summary>WUA's <c>OperationResultCode.orcAborted</c>.</summary>
    public const int Aborted = 5;

    public int ResultCode { get; } = resultCode;

    public int HResult { get; } = hResult;

    public bool RebootRequired { get; } = rebootRequired;

    public FakeOperationResult GetUpdateResult(int index) => this;
}

/// <summary>WUA's update searcher, which searches as a job.</summary>
public sealed class FakeUpdateSearcher(FakeUpdateSession session)
{
    public bool IncludePotentiallySupersededUpdates { get; set; }

    public FakeJob BeginSearch(string criteria, object onCompleted, object? state)
    {
        session.Searches.Add(criteria);
        var job = new FakeJob(onCompleted);
        session.Jobs.Add(job);
        session.SearchStarted.TrySetResult();
        if (!session.SearchRunsUntilStopped) job.Finish();
        return job;
    }

    public FakeSearchResult EndSearch(FakeJob job)
    {
        if (session.SearchFails is { } failure) throw failure;
        return new FakeSearchResult(new FakeCollection<FakeUpdate>([.. session.Pending]));
    }
}

/// <summary>A WUA job: it calls its callback when it is done, and asking it to stop makes it done.</summary>
public sealed class FakeJob(object onCompleted)
{
    public bool StopRequested { get; private set; }

    public bool CleanedUp { get; private set; }

    public void RequestAbort()
    {
        StopRequested = true;
        Finish();
    }

    public void CleanUp() => CleanedUp = true;

    /// <summary>Calls the callback late-bound, as WUA calls it through its dispatch ID 0.</summary>
    public void Finish() => ((dynamic)onCompleted).Invoke(this, null);
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

/// <summary>A WUA update, with what a scan, an install and the pill read off one.</summary>
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

    public bool IsDownloaded { get; set; }

    public bool EulaAccepted { get; set; } = true;

    public void AcceptEula() => EulaAccepted = true;

    /// <summary>Whether installing it needs Windows restarted, which the installer's result carries.</summary>
    public bool NeedsRestart { get; init; }
}
