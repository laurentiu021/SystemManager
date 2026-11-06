// SysManager · ActivationGate
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// What a tab's poll loop waits at while the tab is hidden, so a hidden tab wakes nothing.
/// </summary>
/// <remarks>
/// Process Manager, Volume Control and Bandwidth Monitor slept their interval and then skipped the work while hidden,
/// so once opened each posted to the window's thread once a second for the rest of the run (#2853). Volume Control's
/// meter loop parked on a <see cref="TaskCompletionSource"/> that showing the tab completed and hiding it replaced,
/// and this is that, shared. The tab opens it when shown and closes it when hidden; the loop waits at it before each
/// interval.
/// <para>Safe from any thread: a loop started without a dispatcher runs on the pool while the tab is shown and hidden
/// from the window's thread. A wait that began just before a close ends at once, so the loop still checks that the
/// tab is shown after its interval, and goes round to wait here when it is not.</para>
/// </remarks>
internal sealed class ActivationGate
{
    private readonly Lock _lock = new();

    // Completed while the tab is shown. Hiding it puts an incomplete one in its place, which showing it completes.
    private TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A gate that starts open or closed, as its tab starts shown or hidden.</summary>
    public ActivationGate(bool open)
    {
        if (open) _open.SetResult();
    }

    /// <summary>True while the tab is shown.</summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock) return _open.Task.IsCompleted;
        }
    }

    /// <summary>The tab is shown: every wait ends, and the next ones do not wait.</summary>
    public void Open()
    {
        lock (_lock) _open.TrySetResult();
    }

    /// <summary>The tab is hidden: the next wait lasts until <see cref="Open"/>.</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_open.Task.IsCompleted)
                _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Ends when the tab is shown, at once if it is, or when <paramref name="ct"/> is cancelled.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        Task shown;
        lock (_lock) shown = _open.Task;
        return shown.WaitAsync(ct);
    }
}
