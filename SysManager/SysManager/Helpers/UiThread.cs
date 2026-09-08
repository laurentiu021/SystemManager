// SysManager · UiThread — posts work to the UI thread without waiting for it
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows;
using System.Windows.Threading;

namespace SysManager.Helpers;

/// <summary>
/// Runs an action on the UI thread. Already there, it runs inline; otherwise it is posted and the
/// caller continues without waiting.
/// </summary>
/// <remarks>
/// The pattern this replaces appeared ten times across six files, each written as
/// <c>if (Application.Current?.Dispatcher is { } d) d.Invoke(Update); else Update();</c>. That guard
/// asks whether an <see cref="Application"/> exists, which is not the question that matters — an
/// <c>Application</c> can exist while nothing is pumping its dispatcher, and a synchronous
/// <c>Invoke</c> then waits for a queue no one is draining. A hang dump from the integration suite
/// showed ten threads parked exactly there (#2152).
/// <para><b>Why posting is the right default.</b> A background thread that has finished its work and
/// wants the UI updated has nothing to gain by waiting for the UI thread to have run the update. The
/// project already agreed: <c>BeginInvoke</c> is used in 19 places against these ten, and
/// <c>BulkInstallerViewModel.LoadIconsAsync</c> carries a comment saying so — eighteen lines above a
/// blocking <c>Invoke</c> in the next method.</para>
/// <para><b>When NOT to use this.</b> If the next statement reads what the action wrote, posting
/// changes the answer. Those callers await <c>Dispatcher.InvokeAsync</c> directly instead, which keeps
/// the ordering without parking a thread: an un-resumed continuation costs nothing, a blocked thread
/// costs a thread. <c>StartupViewModel.ScanAsync</c> and <c>ServicesViewModel.RefreshAsync</c> are
/// both that case.</para>
/// <para><b>Where exceptions land.</b> With <c>Invoke</c>, an exception thrown by the action surfaced
/// on the calling thread, where a surrounding <c>catch</c> could see it. Posted, it surfaces on the
/// dispatcher thread. Every call site converted to this helper passes an action that only assigns
/// properties or raises an event, so nothing was relying on catching it — the two sites whose actions
/// run inside a meaningful <c>try</c> await instead, precisely so their <c>catch</c> still works.</para>
/// </remarks>
internal static class UiThread
{
    /// <summary>Posts <paramref name="action"/> to the application's UI thread.</summary>
    internal static void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        => Post(Application.Current?.Dispatcher, action, priority);

    /// <summary>
    /// Posts <paramref name="action"/> to <paramref name="dispatcher"/>. A null dispatcher means
    /// there is no UI thread to marshal to — headless tests and the designer — so the action runs
    /// on the caller's thread, which is what those callers want.
    /// </summary>
    internal static void Post(Dispatcher? dispatcher, Action action,
                              DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            _ = dispatcher.InvokeAsync(action, priority);
    }
}
