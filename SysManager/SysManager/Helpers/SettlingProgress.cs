// SysManager · SettlingProgress — progress reports stop once the outcome is known
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

namespace SysManager.Helpers;

/// <summary>
/// A progress reporter that delivers reports while an operation runs and drops every one that arrives after
/// it has ended, so whatever the caller writes afterwards is the value that stays on screen.
/// </summary>
/// <remarks>
/// <para><see cref="Progress{T}"/> delivers through <see cref="SynchronizationContext.Post"/>. On the UI
/// thread that context is the Dispatcher, and a post and an await continuation queued at the same priority
/// run in the order they were queued — so the shipped app happens to see the caller's final write last. That
/// is an ordering guarantee nothing states and no test can see: one <c>ConfigureAwait(false)</c>, one
/// <c>Task.Run</c> or one detached continuation revokes it, and a unit test has no context at all, which
/// makes both writes unordered threadpool work.</para>
/// <para>It has already gone wrong once. In <c>FileShredderViewModel</c> the service's last report — raised
/// from a <c>ConfigureAwait(false)</c> continuation — drained after the continuation that recorded the
/// outcome, so an erased file was left labelled <c>"Shredding pass 2/3..."</c> forever and a required check
/// went flaky on CI (#2391). Ten more callbacks across eight further view models write a property their own
/// post-await code also writes as its terminal value (#2392); the worst of them replaces an instruction the
/// user has to act on, <c>"Download complete. Click Install to restart with the new version."</c>, with a
/// byte count.</para>
/// <para>That first fix was a latch hand-rolled in place, deliberately, so it could ship with a test. This is
/// the shared version: one body, eleven sites across nine view models. Passing the operation in rather than
/// exposing the latch is what makes the correct use the only use — a caller cannot report past the end
/// without also having asked for the end.</para>
/// </remarks>
/// <typeparam name="T">The progress value the operation reports.</typeparam>
internal sealed class SettlingProgress<T> : IProgress<T>
{
    private readonly Lock _gate = new();
    private readonly IProgress<T> _inner;
    private bool _settled;

    /// <summary>
    /// Creates a reporter that invokes <paramref name="onReport"/> for every report until the operation ends.
    /// </summary>
    /// <param name="onReport">
    /// What to do with a report. Runs on the <see cref="SynchronizationContext"/> current HERE, so construct
    /// this on the UI thread when the handler writes bound state.
    /// </param>
    internal SettlingProgress(Action<T> onReport)
    {
        ArgumentNullException.ThrowIfNull(onReport);

        // Progress<T> captures the context in ITS constructor, so the inner instance is built here rather
        // than on first report: the capture has to happen where the caller created this object.
        _inner = new Progress<T>(value =>
        {
            // Held across the handler so that a report already running cannot interleave with the settle:
            // SettleAfterAsync waits for it, and the caller's terminal write therefore lands after it.
            lock (_gate)
            {
                if (!_settled) onReport(value);
            }
        });
    }

    /// <inheritdoc />
    public void Report(T value) => _inner.Report(value);

    /// <summary>
    /// Runs <paramref name="operation"/> with this reporter and stops reporting the moment it ends, whether
    /// it returned, threw or was cancelled — so every write the caller makes afterwards is final.
    /// </summary>
    /// <param name="operation">
    /// The operation, handed the reporter to pass on. Taking it as a parameter rather than letting the
    /// caller close over the field is what stops a caller settling one reporter while a service reports to
    /// another.
    /// </param>
    /// <returns>Whatever <paramref name="operation"/> returned.</returns>
    internal async Task<TResult> SettleAfterAsync<TResult>(Func<IProgress<T>, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            // No ConfigureAwait(false): callers are UI-facing command bodies whose continuations write bound
            // properties, and those throw off the Dispatcher. The await has to resume where it started.
            return await operation(this);
        }
        finally
        {
            lock (_gate) _settled = true;
        }
    }
}
