// SysManager · UiThreadContext
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Collections.Concurrent;

namespace SysManager.Tests;

/// <summary>
/// Runs what is posted to it on the thread that pumps it, as WPF's dispatcher runs it on the UI thread.
/// </summary>
/// <remarks>
/// A test has no context of its own, so a <see cref="Progress{T}"/> report and an await continuation both run on the
/// thread pool. Installed on the test thread and pumped with <see cref="RunUntil"/>, this puts them back on that
/// thread, so a test can tell what the window's thread sets from what a worker sets.
/// </remarks>
internal sealed class UiThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Work, object? State)?> _work = new();

    public override void Post(SendOrPostCallback d, object? state) => _work.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Runs posted work on this thread until <paramref name="task"/> is done, then surfaces its fault.</summary>
    public void RunUntil(Task task)
    {
        task.ContinueWith(_ => _work.Add(null), TaskScheduler.Default);
        while (_work.Take() is { } item)
            item.Work(item.State);
        task.GetAwaiter().GetResult();
    }
}
